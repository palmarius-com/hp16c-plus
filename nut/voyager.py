"""Voyager calculator (10C/11C/12C/15C/16C) around the Nut CPU, headless.

Chip map, keyboard and LCD segment map come from Nonpareil's .ncd.tmpl
files (nonpareil/); behaviour of the R2D2 display
chip follows Nonpareil's src/voyager_lcd.c.
"""
import pathlib
import xml.etree.ElementTree as ET

from .cpu import NutCPU, W

HERE = pathlib.Path(__file__).resolve().parent
NONPAREIL = HERE.parent / "nonpareil"

SEG_BITS = {s: 1 << i for i, s in enumerate("abcdefg")}

# 7-segment pattern (bit0 = a ... bit6 = g) -> character
SEVEN_SEG = {
    0x00: " ", 0x3F: "0", 0x06: "1", 0x5B: "2", 0x4F: "3", 0x66: "4",
    0x6D: "5", 0x7D: "6", 0x07: "7", 0x27: "7", 0x7F: "8", 0x6F: "9",
    0x67: "9", 0x77: "A", 0x7C: "b", 0x39: "C", 0x58: "c", 0x5E: "d",
    0x79: "E", 0x71: "F", 0x50: "r", 0x5C: "o", 0x74: "h", 0x76: "H",
    0x73: "P", 0x38: "L", 0x3E: "U", 0x1C: "u", 0x54: "n", 0x40: "-",
    0x6E: "y", 0x04: "i", 0x10: "i", 0x30: "I", 0x08: "_", 0x3D: "G",
    0x31: "r", 0x37: "N", 0x78: "t",
}


def _parse_ncd(path):
    root = ET.parse(path).getroot()
    chips = []
    for chip in root.findall("chip"):
        mems = []
        for mem in chip.findall("memory"):
            mems.append((mem.get("addr_space"), int(mem.get("base_addr"), 16),
                         int(mem.get("size"), 16)))
        chips.append((chip.get("type"), int(chip.get("index", "0")), mems))
    lcd = {}
    for digit in root.iter("digit"):
        d = int(digit.get("digit"))
        for seg in digit.findall("segment"):
            reg, bit = int(seg.get("reg")), int(seg.get("bit"))
            if reg >= 0:
                lcd[(d, seg.get("segment"))] = (reg, bit)
    keys = {}
    for key in root.iter("key"):
        keys[int(key.get("user_keycode"))] = int(key.get("hw_keycode"), 16)
    objs = [o.text.strip() for o in root.findall("obj_file")]
    return chips, lcd, keys, objs


class Voyager:
    def __init__(self, model="16c", rom_dir=NONPAREIL, ncd=None, rom_files=None,
                 patches=(), chip_ncd=None):
        """chip_ncd: take the chip map (ROM/RAM) from another calculator,
        e.g. the 16C ROM on the 15C-192 chips for the 16C+."""
        ncd = ncd or (rom_dir / f"{model}.ncd.tmpl")
        chips, self.lcd, self.keys, objs = _parse_ncd(ncd)
        if chip_ncd:
            chips = _parse_ncd(chip_ncd)[0]
        self.cpu = cpu = NutCPU()
        self.display_enable = False
        self.blink = False
        for ctype, index, mems in chips:
            for space, base, size in mems:
                if space == "data":
                    cpu.create_ram(base, size)
            if ctype == "voyager_r2d2":
                io = index * 0x10 + 0x08
                cpu.create_ram(io, 3)
                cpu.ram_read_fn[io] = lambda a: [0] * W
                cpu.ram_write_fn[io] = lambda a, d: None
                cpu.ram_read_fn[io + 1] = self._bitmap_read
                cpu.ram_read_fn[io + 2] = self._bitmap_read
        cpu.extra_ops[0x030] = self._op_blink
        cpu.extra_ops[0x2E0] = self._op_display_off
        cpu.extra_ops[0x320] = self._op_display_toggle
        cpu.on_sleep.append(self._on_sleep)
        for name in (rom_files or objs):
            cpu.load_rom_lines((rom_dir / name).read_text().splitlines())
        for p in patches:
            cpu.load_rom_lines(pathlib.Path(p).read_text().splitlines())

    # ----------------------------------------------------------- R2D2 chip
    def _bitmap_read(self, addr):
        r = self.cpu.ram[addr][:]
        r[1] &= 0xC
        r[0] = 0
        return r

    def _set_display(self, on):
        self.display_enable = on
        self.cpu.display_enable = on

    def _op_blink(self, cpu, op):
        self._set_display(True)
        self.blink = True

    def _op_display_off(self, cpu, op):
        self._set_display(False)
        self.blink = False

    def _op_display_toggle(self, cpu, op):
        self._set_display(not self.display_enable)

    def _on_sleep(self, cpu):
        if not self.display_enable:
            cpu.carry = 1  # deep sleep

    # ------------------------------------------------------------- display
    def segments(self):
        """Per LCD digit 0..10: dict segment -> bool."""
        ram = self.cpu.ram
        out = []
        for d in range(11):
            segs = {}
            for s in "abcdefghij":
                rb = self.lcd.get((d, s))
                if rb:
                    reg, bit = rb
                    segs[s] = bool(ram[reg][bit // 4] & (1 << (bit & 3)))
            out.append(segs)
        return out

    def display(self):
        """Display as text: sign, digits with '.' and ',' marks.
        Annunciators (segment j) are returned by annunciators()."""
        if not self.display_enable:
            return ""
        segs = self.segments()
        text = "-" if segs[0].get("g") else " "
        for d in range(1, 11):
            s = segs[d]
            pat = sum(SEG_BITS[k] for k in "abcdefg" if s.get(k))
            ch = SEVEN_SEG.get(pat, "?")
            text += ch
            if s.get("h") and s.get("i"):
                text += ","
            elif s.get("h"):
                text += "."
        return text

    def annunciators(self):
        return [d for d, s in enumerate(self.segments()) if s.get("j")]

    # ---------------------------------------------------------------- keys
    def power_on(self, max_cycles=2_000_000):
        """Reset (as after inserting batteries) and run to sleep."""
        self.cpu.reset()
        self._set_display(False)
        return self.cpu.run(max_cycles)

    def key(self, user_keycode, hold_cycles=200_000, max_cycles=5_000_000):
        """Press a key (HP row/column code, 41 = ON), run, release, run."""
        cpu = self.cpu
        hw = self.keys[user_keycode]
        if not cpu.press(hw):
            return False
        cpu.run(hold_cycles)
        cpu.release()
        cpu.run(max_cycles)
        return True

    def keys_seq(self, codes, **kw):
        for k in codes:
            self.key(k, **kw)

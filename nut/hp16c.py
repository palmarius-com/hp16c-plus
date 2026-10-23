"""HP-16C keystroke layer on top of the Voyager emulator.

Script tokens (space separated):
  key names     ENTER, STO, f, g, ... (see KEYS)
  shifted names WSIZE, DBLX, SHOWDEC, ... (prefix inserted automatically)
  digit strings 1234, 7F, 0 (each character is one key)
"""
from .voyager import Voyager

# user keycode: (unshifted, f-shifted, g-shifted)
KEYS = {
    11: ("A", "SL", "LJ"), 12: ("B", "SR", "ASR"), 13: ("C", "RL", "RLC"),
    14: ("D", "RR", "RRC"), 15: ("E", "RLN", "RLCN"), 16: ("F", "RRN", "RRCN"),
    17: ("7", "MASKL", "#B"), 18: ("8", "MASKR", "ABS"),
    19: ("9", "RMD", "DBLR"), 10: ("/", "XOR", "DBL/"),
    21: ("GSB", "X<>(I)", "RTN"), 22: ("GTO", "X<>I", "LBL"),
    23: ("HEX", "SHOWHEX", "DSZ"), 24: ("DEC", "SHOWDEC", "ISZ"),
    25: ("OCT", "SHOWOCT", "SQRT"), 26: ("BIN", "SHOWBIN", "1/X"),
    27: ("4", "SB", "SF"), 28: ("5", "CB", "CF"), 29: ("6", "B?", "F?"),
    20: ("*", "AND", "DBLX"),
    31: ("R/S", "(I)", "P/R"), 32: ("SST", "I", "BST"),
    33: ("RDN", "CLRPRGM", "RUP"), 34: ("X<>Y", "CLRREG", "PSE"),
    35: ("BSP", "CLRPREFIX", "CLX"), 36: ("ENTER", "WINDOW", "LSTX"),
    37: ("1", "1S", "X<=Y"), 38: ("2", "2S", "X<0"), 39: ("3", "UNSGN", "X>Y"),
    30: ("-", "NOT", "X>0"),
    41: ("ON", None, None), 42: ("f", None, None), 43: ("g", None, None),
    44: ("STO", "WSIZE", "<"), 45: ("RCL", "FLOAT", ">"),
    47: ("0", "MEM", "X#Y"), 48: (".", "STATUS", "X#0"),
    49: ("CHS", "EEX", "X=Y"), 40: ("+", "OR", "X=0"),
}

NAMES = {}
for _code, (_u, _f, _g) in KEYS.items():
    NAMES[_u] = [_code]
    if _f:
        NAMES[_f] = [42, _code]
    if _g:
        NAMES[_g] = [43, _code]
NAMES["f"], NAMES["g"] = [42], [43]
HEXDIGITS = "0123456789ABCDEF"


def tokens_to_keys(script):
    codes = []
    for tok in script.split():
        if tok in NAMES:
            codes += NAMES[tok]
        elif tok.upper() in NAMES and tok.upper() not in ("F", "G"):
            codes += NAMES[tok.upper()]
        elif all(ch in HEXDIGITS for ch in tok.upper()):
            codes += [NAMES[ch][0] for ch in tok.upper()]
        else:
            raise ValueError(f"unknown key token {tok!r}")
    return codes


class HP16C(Voyager):
    def __init__(self, plus=None, **kw):
        """plus: None = stock 16C chip map; a list of object files (may be
        empty) = the 16C ROM on the 15C-192 chip map (2nd ROM 0x2000-0x37FF,
        RAM 0x10-0x17 and 0x40-0xFF) with those files loaded on top: the
        16C+ machine."""
        kw.setdefault("model", "16c")
        if plus is not None:
            from .voyager import NONPAREIL
            kw["chip_ncd"] = NONPAREIL / "15c-192.ncd.tmpl"
            kw["patches"] = list(plus)
        super().__init__(**kw)

    def start(self):
        """Fresh machine: power on (Pr Error), clear it with a key."""
        self.power_on()
        self.key(41)          # ON
        self.key(35)          # BSP clears the "Pr Error" message
        return self

    def run(self, script, hold=100):
        for code in tokens_to_keys(script):
            self.key(code, hold_cycles=hold)
        return self.display()

    # ------------------------------------------------- integer state
    # Values are raw 64-bit patterns.
    _HI = {"x": 0, "y": 3, "z": 5, "t": 7, "lastx": 9}   # RAM 0x07 digit
    _LO = {"y": 0x00, "z": 0x01, "t": 0x02, "lastx": 0x03}

    @staticmethod
    def _val(digits):
        v = 0
        for d in reversed(digits):
            v = (v << 4) | d
        return v

    def reg64(self, name):
        cpu = self.cpu
        # X: N is the canonical copy; M is a working copy that can be stale
        # (after CLX, STO).
        lo = cpu.n if name == "x" else cpu.ram[self._LO[name]]
        h = self._HI[name]
        hi = (cpu.ram[0x07][h + 1] << 4) | cpu.ram[0x07][h]
        return (hi << 56) | self._val(lo)

    def stack(self):
        return {n: self.reg64(n) for n in ("x", "y", "z", "t", "lastx")}

    def reg_i(self):
        """I, 68 bits: RAM 0xFD (low 14 nibbles) + 0xFE digits 5-3."""
        fe = self.cpu.ram[0xFE]
        hi = (fe[5] << 8) | (fe[4] << 4) | fe[3]
        return (hi << 56) | self._val(self.cpu.ram[0xFD])

    def wsize(self):
        d = self.cpu.ram[0x06]
        nibbles = (16 - d[11]) or 16
        mask = d[10]
        top = {1: 1, 3: 2, 7: 3, 15: 4}[mask]
        return (nibbles - 1) * 4 + top

    def complement(self):
        return self.cpu.ram[0x06][12]          # 0 unsigned, 1 1's, 2 2's

    def flags_cg(self):
        ann = self.annunciators()
        return (9 in ann, 6 in ann)            # (carry, out-of-range)

    def error(self):
        d = self.display()
        return d.strip() if "Error" in d else None

    def ram_dump(self, regs=None):
        cpu = self.cpu
        regs = regs or [a for a in range(1024) if cpu.ram_exists[a]]
        return {a: "".join(f"{d:x}" for d in reversed(cpu.ram[a])) for a in regs}

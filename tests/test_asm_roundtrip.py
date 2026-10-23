"""Assembler check: disassemble the 16C and 15C ROMs, re-assemble, compare.

    python tests/test_asm_roundtrip.py
"""
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from nut import disasm  # noqa: E402
from nut.asm import Assembler  # noqa: E402


def roundtrip(rom, lo, hi, data=()):
    lines, a = [], lo
    while a <= hi:
        if any(l <= a <= h for l, h in data):
            lines.append(f"dw ${rom[a]:03x}")
            a += 1
            continue
        n, t, tg, _ = disasm.decode(rom, a)
        if tg is not None:
            t = t % f"${tg:04x}"
        if a + n > hi + 1:
            lines.append(f"dw ${rom[a]:03x}")
            a += 1
            continue
        lines.append(t)
        a += n
    w = Assembler().assemble("\n".join(lines), lo)
    return sum(1 for x in range(lo, hi + 1) if w.get(x) != rom[x]), hi - lo + 1


def main():
    _, _, data = disasm.load_symbols(ROOT / "re" / "16c.sym")
    rom16, _ = disasm.load_rom([ROOT / "nonpareil" / "16c.rom"])
    rom15, _ = disasm.load_rom([ROOT / "nonpareil" / "15c.rom"])
    bad = total = 0
    for name, rom, lo, hi, d in [("16c", rom16, 0, 0x17FF, data),
                                 ("15c", rom15, 0, 0x17FF, ()),
                                 ("15c", rom15, 0x2000, 0x37FF, ())]:
        b, n = roundtrip(rom, lo, hi, d)
        bad, total = bad + b, total + n
        print(f"{name} {lo:04x}-{hi:04x}: {n - b}/{n} words identical")
    print("asm round trip " + ("all passed" if not bad else f"{bad} FAILED"))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

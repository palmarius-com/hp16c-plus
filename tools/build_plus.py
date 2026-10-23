"""Build the 16C+ object file.

Assembles plus/src/16cplus.asm, checks that every word it changes in the
original ROM range (0x0000-0x17FF) replaces the expected original words
(HOOKS below), that the second-ROM code stays inside 0x2000-0x37FF, and
writes build/16cplus.obj (Nonpareil object format) and a listing.

    python tools/build_plus.py
"""
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from nut.asm import Assembler, write_obj, checksum_word  # noqa: E402
from nut import disasm  # noqa: E402

SRC = ROOT / "plus" / "src" / "16cplus.asm"
OUT = ROOT / "build" / "16cplus.obj"
LST = ROOT / "build" / "16cplus.lst"

# hook site -> original words it replaces (safety check)
HOOKS = {
    0x087C: [0x04E, 0x21C],     # c=0 w ; pt= 2   (dispatcher)
    0x136F: [0x351, 0x018],     # ?nc gosub 06d4  (STATUS)
    0x0372: [0x130, 0x004],     # ldi $004        (memory reset)
    0x11ED: [0x2C4, 0x260],     # s=0 13 ; set hex (integer display formatter)
    0x0C84: [0x34C, 0x19D],     # ? s=1 12 ; ?nc golong 0b67 (word 1) (digit)
    0x1400: [0x28C, 0x101],     # ? s=1 7 ; ?nc golong 0c40 (word 1)  (BSP)
    0x08FA: [0x251, 0x00C],     # ?nc gosub 0394 CLR_ENTRY (RCL n)
    0x0914: [0x0A1, 0x044],     # ?nc gosub 1128 REG_WRITE (STO n)
    0x097F: [0x161, 0x05E],     # ?nc golong 1758 (FLOAT n, integer -> float)
    0x01F4: [0x04E, 0x270],     # c=0 w ; sel ram (MEM register count)
}
# one free word per chip makes its sum 1 mod 1023 again (the 16C's own
# self-test checks chip 1; NutEm/PC's 16C+ model checks the second chip)
CHECKSUM = {0x13FF: (0x0000, 0x17FF), 0x37FF: (0x2000, 0x37FF)}
FIXED = {"P_WSIZE": 0x2100, "P_FN": 0x2080}     # addresses used by lc


def build():
    a = Assembler()
    words = a.assemble(SRC.read_text(encoding="utf-8"))
    rom, _ = disasm.load_rom([ROOT / "nonpareil" / "16c.rom"])
    errors = []
    patched = sorted(x for x in words if x < 0x1800)
    covered = set()
    for site, orig in HOOKS.items():
        for i, w in enumerate(orig):
            covered.add(site + i)
            if rom[site + i] != w:
                errors.append(f"hook {site:04x}: ROM word {site + i:04x} is "
                              f"{rom[site + i]:03x}, expected {w:03x}")
    full = {x: rom[x] for x in range(0x1800)}
    full.update(words)
    for at, (lo, hi) in CHECKSUM.items():
        if at in words or full.get(at, 0):
            errors.append(f"checksum word {at:04x} is in use")
            continue
        words[at] = full[at] = checksum_word(full, lo, hi)
        covered.add(at)
    for x in patched:
        if x not in covered:
            errors.append(f"word {x:04x} patched without a HOOKS entry")
    for x in words:
        if 0x1800 <= x < 0x2000 or x >= 0x3800:
            errors.append(f"word {x:04x} outside the ROM chips")
    for name, addr in FIXED.items():
        if a.symbols.get(name) != addr:
            errors.append(f"{name} at {a.symbols.get(name, -1):04x}, "
                          f"expected {addr:04x}")
    return a, words, patched, errors


def main():
    a, words, patched, errors = build()
    if errors:
        print("\n".join(errors))
        return 1
    OUT.parent.mkdir(exist_ok=True)
    write_obj(words, OUT, header="HP-16C+ build; load on top of 16c.rom")
    LST.write_text("\n".join(
        f"{addr:04x}  {' '.join(f'{w:03x}' for w in ws):<8}  {src}"
        for addr, ws, src in a.listing) + "\n")
    used = sum(1 for x in words if x >= 0x2000)
    print(f"built {OUT.name}: {len(patched)} hook words, {used} words in "
          f"the 2nd ROM ({used * 100 // 6144}% of 6144)")
    return 0


if __name__ == "__main__":
    sys.exit(main())

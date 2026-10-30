"""Assemble a Nut source file into a patch file for the Voyager simulator.

A patch is a text file of "aaaa:www" lines (hex address : 10-bit word), the
format Nonpareil uses for ROMs. The simulator applies it on top of the
original ROM (ROM > Apply a patch). See csharp/docs/PATCHING.md.

    python tools/make_patch.py my.asm -o my.patch
    python tools/make_patch.py my.asm -o my.patch --rom nonpareil/16c.rom \
        --checksum 13ff:0000-17ff

--checksum ADDR:LO-HI sets the word at ADDR so that the ROM chip LO-HI
(original ROM plus your words) sums to 1 mod 1023, which the calculator's
self-test checks. ADDR must be a word you do not use otherwise. Repeat it
for each chip you changed.
"""
import argparse
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from nut.asm import AsmError, assemble_file, checksum_word, write_obj  # noqa: E402
from nut.disasm import load_rom  # noqa: E402


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("source", help="Nut assembler source (.asm)")
    ap.add_argument("-o", "--out", required=True, help="patch file to write")
    ap.add_argument("--rom", help="original ROM, needed for --checksum")
    ap.add_argument("--checksum", action="append", default=[], metavar="ADDR:LO-HI")
    args = ap.parse_args()
    try:
        words = dict(assemble_file(args.source).words)
    except AsmError as e:
        sys.exit(f"{args.source}: {e}")
    if args.checksum:
        if not args.rom:
            sys.exit("--checksum needs --rom (the original ROM the patch goes on)")
        rom, exists = load_rom([pathlib.Path(args.rom)])
        merged = {a: rom[a] for a in range(0x10000) if exists[a]}
        merged.update(words)
        for spec in args.checksum:
            addr_s, rng = spec.split(":")
            lo, hi = (int(x, 16) for x in rng.split("-"))
            addr = int(addr_s, 16)
            if addr in words:
                sys.exit(f"checksum word {addr:04x} is used by your code")
            merged.pop(addr, None)
            words[addr] = merged[addr] = checksum_word(merged, lo, hi)
            print(f"checksum word {addr:04x} = {words[addr]:03x} (chip {lo:04x}-{hi:04x})")
    write_obj(words, args.out, header=f"Patch from {pathlib.Path(args.source).name}\n"
                                      f"Format: aaaa:www (hex address : 10-bit word)")
    print(f"wrote {args.out}: {len(words)} words")


if __name__ == "__main__":
    main()

"""Differential test of the program conditionals: model vs stock ROM.

Each test function is put into a small program
    LBL A, <test>, GTO 1, 0, RTN, LBL 1, 1, RTN
and run with GSB A on random word size, complement mode and operands;
the display then shows 1 (true) or 0 (false).

    python tests/test_cond_vs_rom.py [-n 100] [--seed 1]
"""
import argparse
import pathlib
import random
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
from nut.hp16c import HP16C  # noqa: E402
from model.hp16c_model import Model16C, Error16C  # noqa: E402
from tests.test_model_vs_rom import COMP_KEY, WSIZES, special  # noqa: E402


def load_program(c, test):
    c.run("BSP P/R CLRPRGM")
    c.run(f"LBL A {test} GTO 1 0 RTN LBL 1 1 RTN")
    c.run("P/R")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=100)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--ops", nargs="*", default=list(Model16C.TESTS))
    args = ap.parse_args()
    rng = random.Random(args.seed)
    c = HP16C().start()
    t0 = time.time()
    total = bad = 0
    for op in args.ops:
        load_program(c, op)
        diffs = []
        for _ in range(args.n):
            w = rng.choice(WSIZES)
            comp = rng.choice([0, 1, 2])
            y, x = special(w, rng), special(w, rng)
            if rng.random() < 0.3:
                x = y                                   # equal operands
            if op == "B?":
                x = rng.randrange(min(w + 1, 1 << (w - 1) if comp else 1 << w)
                                  or 1)
            keys = ["BSP", "HEX", "0", "WSIZE"]
            if w != 64:
                keys += [f"{w:X}", "WSIZE"]
            keys += [COMP_KEY[comp], " ".join(f"{y:X}"), "ENTER",
                     " ".join(f"{x:X}"), "GSB", "A"]
            got = c.run(" ".join(keys)).split()
            got = got[0] if got else "?"
            md = Model16C(w=w, comp=comp, x=x, y=y)
            try:
                want = "1" if md.test(op) else "0"
            except Error16C as e:
                want = f"Error {e.n}"
                got = c.error() or got
            total += 1
            if got != want:
                diffs.append((w, comp, y, x, want, got))
        bad += len(diffs)
        print(f"{op:5} {args.n - len(diffs):3}/{args.n} ok")
        for w, comp, y, x, want, got in diffs[:4]:
            print(f"   w={w} comp={comp} y={y:x} x={x:x}: model {want} rom {got}")
    print(f"total {total - bad}/{total} ok, {time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

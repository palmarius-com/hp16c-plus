"""Differential test of float -> integer conversion: model vs ROM.

In float mode a random 10-digit mantissa and exponent are keyed in, then a
base key leaves float mode; compares Y (mantissa) and X (exponent) and the
word size 56.

    python tests/test_float2int_vs_rom.py [-n 200] [--seed 1]
"""
import argparse
import pathlib
import random
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
from nut.hp16c import HP16C  # noqa: E402
from model.hp16c_model import Model16C, from_float  # noqa: E402
from tests.test_model_vs_rom import COMP_KEY  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=200)
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()
    rng = random.Random(args.seed)
    c = HP16C().start()
    bad = []
    t0 = time.time()
    for _ in range(args.n):
        comp = rng.choice([0, 1, 2])
        neg = rng.random() < 0.4
        mant = rng.choice([10 ** 9, 10 ** 10 - 1, rng.randrange(10 ** 9, 10 ** 10)])
        exp = rng.choice([0, 1, -1, 9, 17, -17, 50, -50, 99, -99,
                          rng.randrange(-99, 100)])
        ms = str(mant)
        keys = (f"BSP HEX 0 WSIZE {COMP_KEY[comp]} FLOAT 9 "
                f"{ms[0]} . {' '.join(ms[1:])} EEX {' '.join(str(abs(exp)).zfill(2))}")
        if exp < 0:
            keys += " CHS"
        if neg:
            keys += " ENTER CHS"
        keys += " DEC"
        disp = c.run(keys)
        st = c.stack()
        m56 = (1 << 56) - 1
        # X is left in the entry-buffer form in M (notes section 10): read
        # it from the DEC display instead (|x| < 400 fits one window).
        xs = disp.split()[0]
        got = (st["y"] & m56, int(xs), c.wsize())
        y, x = from_float(neg, mant, exp, comp)
        want = (y, Model16C(w=56, comp=comp).signed(x), 56)
        if got != want:
            bad.append((comp, neg, mant, exp, want, got))
    for comp, neg, mant, exp, want, got in bad[:10]:
        print(f"comp={comp} {'-' if neg else ''}{mant}e{exp}: "
              f"model y={want[0]:x} x={want[1]}  rom y={got[0]:x} x={got[1]} w={got[2]}")
    print(f"float->int {args.n - len(bad)}/{args.n} ok, {time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

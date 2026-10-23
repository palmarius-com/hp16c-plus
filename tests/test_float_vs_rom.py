"""Differential test of integer -> float conversion (FLOAT n): model vs ROM.

Random word size, complement mode, mantissa y and exponent x (small,
large, edge values); compares the BCD image in M, flag 5, the word size
(56 afterwards) and the cleared stack.

    python tests/test_float_vs_rom.py [-n 300] [--seed 1]
"""
import argparse
import pathlib
import random
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
from nut.hp16c import HP16C  # noqa: E402
from model.hp16c_model import Model16C, to_float, float_to_m  # noqa: E402
from tests.test_model_vs_rom import COMP_KEY, WSIZES, special  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=300)
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()
    rng = random.Random(args.seed)
    bad = []
    t0 = time.time()
    c = HP16C().start()
    for _ in range(args.n):
        w = rng.choice([v for v in WSIZES if v >= 8])
        comp = rng.choice([0, 1, 2])
        md = Model16C(w=w, comp=comp)
        y = special(w, rng)
        xv = rng.choice([0, 1, -1, 3, -3, 10, -10, 63, -64, 200, 300, -300,
                         -330, 330, rng.randrange(-340, 340)])
        if comp == 0:
            xv = abs(xv)
        if not md.in_range(xv):
            xv = rng.randrange(0, 1 << (w - 1) if comp else 1 << w) % 100
        x = md.pattern(xv)
        c.run("BSP HEX 0 WSIZE" + (f" {w:X} WSIZE" if w != 64 else "")
              + f" {COMP_KEY[comp]} " + " ".join(f"{y:X}") + " ENTER "
              + " ".join(f"{x:X}") + " FLOAT 9")
        m = "".join(f"{d:x}" for d in reversed(c.cpu.m))
        g = c.flags_cg()[1]
        st = c.stack()
        neg, mant, exp, ovf = to_float(y, x, w, comp)
        want = (float_to_m(neg, mant, exp), ovf, 56, 0, 0, 0)
        got = (m, g, c.wsize(), st["y"], st["z"], st["t"])
        if got != want:
            bad.append((w, comp, y, xv, want, got))
        c.run("HEX")                       # back to integer mode
    for w, comp, y, xv, want, got in bad[:10]:
        print(f"w={w} comp={comp} y={y:x} x={xv}: model {want} rom {got}")
    print(f"float {args.n - len(bad)}/{args.n} ok, {time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

"""16C+ FLOAT n from more than 64 bits: plus mode vs model.

Random word size 65-128, complement mode, Y up to 128 bits (edge values,
negative), exponent X small, at the over/underflow limits, or beyond
2^56; FLOAT 9; compares the float in M (BCD image), flag 5, W = 56, the
cleared Y, Z, T, and that plus mode and every extension word are off.
The patch cuts Y to 63 bits with a sticky bit first; the model rounds
the exact y * 2^x, so a difference can only come from a near-tie (as the
original's 13-digit arithmetic, README "Known deviation").

    python tests/test_plus_float.py [-n 200] [--seed 1]
"""
import argparse
import pathlib
import random
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import tests.test_plus_funcs as T  # noqa: E402
from model.hp16c_model import Model16C, to_float, float_to_m  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=200)
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()
    rng = random.Random(args.seed)
    c = T.HP16C(plus=[T.OBJ]).start()
    bad = []
    t0 = time.time()
    for _ in range(args.n):
        w = rng.choice([65, 66, 100, 127, 128, rng.randrange(65, 129)])
        comp = rng.choice([0, 1, 2])
        md = Model16C(w=w, comp=comp)
        y = T.special(w, rng)
        xv = rng.choice([0, 1, -1, 10, -10, -64, -100, 200, 250, -300, -400,
                         330, -330, rng.randrange(-340, 340),
                         1 << 56, -(1 << 56), (1 << 60) + 3, -(1 << 70)])
        if comp == 0:
            xv = abs(xv)
        if not md.in_range(xv):
            xv = rng.randrange(-300, 300) if comp else rng.randrange(300)
        x = md.pattern(xv)
        c.run(f"BSP HEX 0 WSIZE {' '.join(f'{w:X}')} WSIZE {T.COMP_KEY[comp]}")
        T.put(c, "y", y)
        T.put(c, "x", x)
        c.run("FLOAT 9")
        m = "".join(f"{d:x}" for d in reversed(c.cpu.m))
        ram = c.cpu.ram
        ext = [T.from_digits(ram[a]) for a in range(0x10, 0x18)]
        st = c.stack()
        neg, mant, exp, ovf = to_float(y, x, w, comp)
        want = (float_to_m(neg, mant, exp), ovf, 56, 0, 0, 0, [0] * 8)
        got = (m, c.flags_cg()[1], c.wsize(), st["y"], st["z"], st["t"], ext)
        if got != want:
            bad.append((w, comp, y, xv, want, got))
        c.run("HEX")
    for w, comp, y, xv, want, got in bad[:8]:
        print(f"w={w} comp={comp} y={y:x} x={xv}:\n  model {want}\n  plus  {got}")
    print(f"plus float {args.n - len(bad)}/{args.n} ok, {time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

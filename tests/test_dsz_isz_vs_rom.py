"""DSZ / ISZ (68-bit I counting, skip on zero): model vs stock ROM.

Program: LBL A, DSZ|ISZ, GTO 1, 0, RTN, LBL 1, 1, RTN. Result 0 = the
next step (GTO 1) was skipped. Compares the skip and the new I.

    python tests/test_dsz_isz_vs_rom.py [-n 150] [--seed 1]
"""
import argparse
import pathlib
import random
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
from nut.hp16c import HP16C  # noqa: E402
from model.hp16c_model import Model16C  # noqa: E402
from tests.test_model_vs_rom import COMP_KEY, WSIZES, special  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=150)
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()
    rng = random.Random(args.seed)
    t0 = time.time()
    total = bad = 0
    c = HP16C().start()
    for op in ("DSZ", "ISZ"):
        c.run(f"BSP P/R CLRPRGM LBL A {op} GTO 1 0 RTN LBL 1 1 RTN P/R")
        for _ in range(args.n):
            w = rng.choice(WSIZES)
            comp = rng.choice([0, 1, 2])
            v = rng.choice([0, 1, 2, special(w, rng), (1 << w) - 1,
                            (1 << w) - 2])
            v &= (1 << w) - 1
            c.run("BSP HEX CF 3 0 WSIZE" + (f" {w:X} WSIZE" if w != 64 else "")
                  + f" {COMP_KEY[comp]} " + " ".join(f"{v:X}") + " STO I")
            md = Model16C(w=w, comp=comp, i=c.reg_i())
            skip = md.dsz() if op == "DSZ" else md.isz()
            r = c.run("GSB A").split()[0]
            got = (r == "0", c.reg_i())
            want = (skip, md.i)
            total += 1
            if got != want:
                bad += 1
                if bad <= 8:
                    print(f"{op} w={w} comp={comp} I0={v:x}: model {want} rom {got}")
    print(f"dsz/isz {total - bad}/{total} ok, {time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

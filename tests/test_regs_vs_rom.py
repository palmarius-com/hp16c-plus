"""Differential test of STO/RCL storage registers: model vs stock ROM.

Per case: random word size W1, store random values in a few random
registers (0-F, .0-.F), optionally change to W2, then recall registers
(written or not) and compare X (and Error 3 for missing registers).
The machine is cleared (CLRREG) before each case.

    python tests/test_regs_vs_rom.py [-n 200] [--seed 1]
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


def reg_keys(n):
    return f"{n:X}" if n < 16 else f". {n - 16:X}"


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
        w1 = rng.choice(WSIZES)
        comp = rng.choice([0, 1, 2])
        md = Model16C(w=w1, comp=comp)
        c.run("BSP HEX CLRREG 0 WSIZE" + (f" {w1:X} WSIZE" if w1 != 64 else "")
              + f" {COMP_KEY[comp]}")
        steps = []
        for _ in range(3):
            n, v = rng.randrange(32), special(w1, rng)
            steps.append(("sto", n, v))
        w2 = rng.choice([w1, w1, rng.choice(WSIZES)])
        rcls = [n for _, n, _ in steps] + [rng.randrange(32)]
        log = []
        for _, n, v in steps:
            got = c.run(" ".join(f"{v:X}") + f" STO {reg_keys(n)}")
            md.x = v
            try:
                md.sto(n)
                want = None
            except Error16C as e:
                want = f"Error {e.n}"
            if want or c.error():
                log.append((f"STO {n}", want, c.error()))
                c.run("BSP")
        if w2 != w1:
            c.run("0 WSIZE" + (f" {w2:X} WSIZE" if w2 != 64 else ""))
            md.w = w2
        for n in rcls:
            c.run(f"RCL {reg_keys(n)}")
            err = c.error()
            got = err or (c.stack()["x"] & md.mask)
            try:
                md.rcl(n)
                want = md.x
            except Error16C as e:
                want = f"Error {e.n}"
            if err:
                c.run("BSP")
            log.append((f"RCL {n}", want, got))
        diffs = [(op, w, g) for op, w, g in log if w != g]
        if diffs:
            bad.append((w1, w2, comp, steps, diffs))
    for w1, w2, comp, steps, diffs in bad[:8]:
        print(f"w1={w1} w2={w2} comp={comp} "
              + " ".join(f"STO{n}={v:x}" for _, n, v in steps))
        for op, want, got in diffs:
            print(f"   {op}: model {want if isinstance(want, str) else hex(want)}"
                  f" rom {got if isinstance(got, str) else hex(got)}")
    print(f"registers {args.n - len(bad)}/{args.n} ok, {time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

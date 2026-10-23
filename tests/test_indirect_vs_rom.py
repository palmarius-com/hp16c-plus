"""Differential test of indirect register access (i): model vs ROM.

Per case: CLRREG, random word size and complement mode, I from a small
signed number (or an edge value), STO (i) of a random X, then RCL (i),
X<>(i) and a direct RCL when the register number is below 32; compares
X, Y and errors (Error 3 for missing registers).

    python tests/test_indirect_vs_rom.py [-n 200] [--seed 1]
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
from tests.test_regs_vs_rom import reg_keys  # noqa: E402


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
        w = rng.choice(WSIZES)
        comp = rng.choice([0, 1, 2])
        md = Model16C(w=w, comp=comp)
        r = rng.choice([0, 1, 5, 31, 32, 100, 202, 203, 400])
        if rng.random() < 0.3 and comp:
            ip = md.pattern(-r) if comp == 2 else (~r & md.mask)   # -r
        else:
            ip = r & md.mask
        if rng.random() < 0.15:
            ip = special(w, rng)
        x1, x2 = special(w, rng), special(w, rng)
        c.run("BSP HEX CLRREG 0 WSIZE" + (f" {w:X} WSIZE" if w != 64 else "")
              + f" {COMP_KEY[comp]} " + " ".join(f"{ip:X}") + " STO I")
        md.i = c.reg_i()
        n = md.ind_reg()
        log = []

        def want_of(fn):
            try:
                return fn()
            except Error16C as e:
                return f"Error {e.n}"

        def got_of(keys):
            c.run(keys)
            err = c.error()
            if err:
                c.run("BSP")
                return err
            return c.stack()["x"] & md.mask

        # STO (i): only the error status is visible right away (X stays in
        # the entry buffer form after STO, see notes section 10)
        def sto():
            md.x = x1
            md.sto_ind()
            return "stored"
        want = want_of(sto)
        got = got_of(" ".join(f"{x1:X}") + " STO (I)")
        log.append(("STO (i)", want, got if isinstance(got, str) else "stored"))
        log.append(("RCL (i)", want_of(lambda: (md.rcl_ind(), md.x)[1]),
                    got_of("RCL (I)")))

        def xex():
            md.x = x2
            md.x_ex_ind()
            return md.x
        log.append(("X<>(i)", want_of(xex),
                    got_of(" ".join(f"{x2:X}") + " X<>(I)")))
        if n < 32:
            log.append((f"RCL {n}", want_of(lambda: (md.rcl(n), md.x)[1]),
                        got_of(f"RCL {reg_keys(n)}")))
        diffs = [(k, a, b) for k, a, b in log if a != b]
        if diffs:
            bad.append((w, comp, ip, diffs))
    for w, comp, ip, diffs in bad[:8]:
        print(f"w={w} comp={comp} I={ip:x}")
        for k, a, b in diffs:
            print(f"   {k}: model {a} rom {b}")
    print(f"indirect {args.n - len(bad)}/{args.n} ok, {time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

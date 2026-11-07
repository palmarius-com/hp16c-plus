"""Two bugs reported by Amphitryon (2026-11-06/07), found in the 16C+ after the 15C CE tests.

1. FLOAT n above 64 bits showed FLOAT 0: the plus path of P_FLOAT lost G (the digit n).
2. BSP in float mode gave garbage after the last digit, at every word size: the BSP hook sat on
   0x1400-0x1401 and the original float code jumps back to 0x1401 (the golong behind the test at
   0x1400); the hook also did not give the original its entry state (C, P, RAM select).

Checks (all must hold):
  - FLOAT n for n = 0..9 shows "0." + n zeros at word sizes 16, 64, 65, 85 (HEX 55), 100 and 128;
  - the display after typing 1 2 3 and pressing BSP five times is the same as on the stock 16C, for
    FLOAT 0..9 and for DEC, HEX, OCT and BIN entry, at the default word size.
Usage: python tests/test_plus_float_bsp.py
"""
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from nut.hp16c import HP16C  # noqa: E402

OBJ = ROOT / "build" / "16cplus.obj"


def plus():
    return HP16C(plus=[OBJ]).start()


def main():
    bad = 0
    n_checks = 0
    for ws in (16, 64, 65, 85, 100, 128):
        for n in range(10):
            c = plus()
            c.run("DEC " + " ".join(str(ws)) + " WSIZE")
            d = c.run(f"FLOAT {n}").strip()
            n_checks += 1
            if d != "0." + "0" * n:
                bad += 1
                print(f"FAIL FLOAT {n} at {ws} bits: {d!r}")
    # HEX 55 WSIZE = 85 bits (Amphitryon's case)
    c = plus()
    d = c.run("HEX 5 5 WSIZE FLOAT 5").strip()
    n_checks += 1
    if d != "0.00000":
        bad += 1
        print(f"FAIL HEX 55 WSIZE FLOAT 5: {d!r}")
    modes = [f"FLOAT {n}" for n in range(10)] + ["DEC", "HEX", "OCT", "BIN"]
    for m in modes:
        out = []
        for mk in (lambda: HP16C().start(), plus):
            c = mk()
            c.run(m + " 1 2 3")
            seq = [c.display().strip()]
            for _ in range(5):
                c.run("BSP")
                seq.append(c.display().strip())
            out.append(seq)
        n_checks += 1
        if out[0] != out[1]:
            bad += 1
            print(f"FAIL BSP after {m} 1 2 3: stock {out[0]} plus {out[1]}")
    print(f"float/BSP regression: {n_checks - bad}/{n_checks} ok")
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()

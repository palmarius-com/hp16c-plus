"""Differential test of the integer display: model.display() vs stock ROM.

Random word size, complement mode, base, flag 3 (leading zeros), window
and value (with edge values); compares the LCD text.

    python tests/test_display_vs_rom.py [-n 400] [--seed 1]
"""
import argparse
import pathlib
import random
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
from nut.hp16c import HP16C  # noqa: E402
from model.hp16c_model import display  # noqa: E402
from tests.test_model_vs_rom import COMP_KEY, WSIZES, special  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=400)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--show", type=int, default=10)
    args = ap.parse_args()
    rng = random.Random(args.seed)
    c = HP16C().start()
    bad = []
    t0 = time.time()
    for _ in range(args.n):
        w = rng.choice(WSIZES)
        comp = rng.choice([0, 1, 2])
        base = rng.choice(["HEX", "DEC", "OCT", "BIN"])
        lead0 = rng.random() < 0.5
        window = rng.choice([0, 0, 0, 1, 2, rng.randrange(8)])
        p = special(w, rng)
        keys = ["BSP", "HEX", "0", "WSIZE"]
        if w != 64:
            keys += [f"{w:X}", "WSIZE"]
        keys += [COMP_KEY[comp], "SF" if lead0 else "CF", "3"]
        keys += [" ".join(f"{p:X}"), "ENTER", base]
        if window:
            keys += ["WINDOW", str(window)]
        offset = 8 * window
        if rng.random() < 0.4:                       # scrolling with > <
            for _ in range(rng.randrange(1, 12)):
                k = rng.choice([">", ">", "<"])
                keys.append(k)
                offset = offset + 1 if k == ">" else max(0, offset - 1)
        got = c.run(" ".join(keys))
        want = display(p, w, comp, base, lead0, offset=offset)
        if got != want:
            bad.append((w, comp, base, lead0, window, p, want, got))
    for w, comp, base, lead0, window, p, want, got in bad[:args.show]:
        print(f"w={w} comp={comp} {base} lead0={int(lead0)} win={window} "
              f"x={p:x}: model {want!r} rom {got!r}")
    print(f"display {args.n - len(bad)}/{args.n} ok, {time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

"""MEM display and user flags (SF/CF/F?) vs stock ROM.

MEM: random word size and program length (digit steps); compares the
"P-n r-rrr" text. Flags: random SF/CF sequences on flags 0-5, then F? n in
a small program (1 = set), compared with the model's flag state.

    python tests/test_mem_flags_vs_rom.py [-n 60] [--seed 1]
"""
import argparse
import pathlib
import random
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
from nut.hp16c import HP16C  # noqa: E402
from model.hp16c_model import mem_display  # noqa: E402
from tests.test_model_vs_rom import WSIZES  # noqa: E402


def hold_mem(c):
    c.run("f")
    c.cpu.press(c.keys[47])
    c.cpu.run(20000)
    d = c.display()
    c.cpu.release()
    c.cpu.run(10 ** 6)
    return d


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=60)
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()
    rng = random.Random(args.seed)
    t0 = time.time()
    bad = []
    for _ in range(args.n):
        c = HP16C().start()
        w = rng.choice(WSIZES)
        steps = rng.choice([0, 1, 6, 7, 8, 13, 14, rng.randrange(60)])
        c.run("HEX 0 WSIZE" + (f" {w:X} WSIZE" if w != 64 else ""))
        if steps:
            c.run("P/R " + " ".join("1" for _ in range(steps)) + " P/R")
        got, want = hold_mem(c), mem_display(w, steps)
        if got != want:
            bad.append(("MEM", w, steps, want, got))
    c = HP16C().start()
    c.run("P/R LBL A F? 0 GTO 1 0 RTN LBL 1 1 RTN P/R")
    for _ in range(args.n):
        flags = {}
        keys = []
        for _ in range(4):
            f = rng.randrange(6)
            s = rng.random() < 0.5
            keys += ["SF" if s else "CF", str(f)]
            flags[f] = s
        c.run("BSP HEX " + " ".join(keys))
        f = rng.choice(list(flags))
        c.run("P/R GTO . 0 0 1 SST BSP F? " + str(f) + " P/R")
        got = str(int(c.run("GSB A").split()[0], 16))   # flag 3 may pad zeros
        want = "1" if flags[f] else "0"
        if got != want:
            bad.append(("F?", f, keys, want, got))
    for b in bad[:10]:
        print(b)
    print(f"mem/flags {2 * args.n - len(bad)}/{2 * args.n} ok, "
          f"{time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

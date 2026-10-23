"""16C+ display above 64 bits: plus mode vs the model.

Random full X (up to 128 bits, edge values), word size 65-128, flag 3,
WINDOW n and scrolling with < >; X is written into the machine state
(N, M, RAM 7, extension RAM) and the display compared with
model.display(). Bases HEX, OCT, BIN, DEC; all three complement modes.
Keys are pressed short or held long (a held key makes the ROM format the
display on another path); some cases also hold SHOW <base> and check the
display while it is held.

    python tests/test_plus_display.py [-n 300] [--seed 1]
"""
import argparse
import pathlib
import random
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from nut.hp16c import HP16C, tokens_to_keys  # noqa: E402
from nut.routine import to_digits, from_digits  # noqa: E402
from model.hp16c_model import display  # noqa: E402

OBJ = ROOT / "build" / "16cplus.obj"


def set_x(c, v):
    """Write a 128-bit X into the machine (bits 0-55 N and M, 56-63 RAM 7
    digits 1-0, 64-119 RAM 0x11, 120-127 RAM 0x16 digits 1-0)."""
    lo = v & ((1 << 56) - 1)
    c.cpu.n = to_digits(lo)
    c.cpu.m = to_digits(lo)
    r7 = from_digits(c.cpu.ram[7])
    c.cpu.ram[7] = to_digits((r7 & ~0xFF) | ((v >> 56) & 0xFF))
    c.cpu.ram[0x11] = to_digits((v >> 64) & ((1 << 56) - 1))
    r16 = from_digits(c.cpu.ram[0x16])
    c.cpu.ram[0x16] = to_digits((r16 & ~0xFF) | ((v >> 120) & 0xFF))


def held(c, name, cycles=200_000):
    """Display while the last key of name is held down (e.g. SHOWDEC)."""
    ks = tokens_to_keys(name)
    for k in ks[:-1]:
        c.key(k, hold_cycles=100)
    c.cpu.press(c.keys[ks[-1]])
    c.cpu.run(cycles)
    shown = c.display()
    c.cpu.release()
    c.cpu.run(5_000_000)
    return shown


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=300)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--base", choices=["HEX", "OCT", "BIN", "DEC"], action="append",
                    help="bases to test (default all)")
    args = ap.parse_args()
    rng = random.Random(args.seed)
    bases = args.base or ["HEX", "OCT", "BIN", "DEC"]
    bad = []
    t0 = time.time()
    for _ in range(args.n):
        # fresh machine per case: digit entry and BSP are not plus-aware yet
        c = HP16C(plus=[OBJ]).start()
        w = rng.choice([65, 66, 72, 96, 100, 120, 127, 128])
        lead0 = rng.random() < 0.4
        v = rng.choice([0, 1, (1 << w) - 1, 1 << (w - 1), rng.getrandbits(64),
                        rng.getrandbits(w), rng.getrandbits(rng.randrange(1, w + 1))])
        comp = rng.choice([0, 1, 2])
        mode = ["UNSGN", "1S", "2S"][comp]
        c.run(f"HEX 0 WSIZE {w:X} WSIZE {mode} {'SF' if lead0 else 'CF'} 3 0 ENTER")
        set_x(c, v)
        base = rng.choice(bases)
        keys, offset = [base], 0
        win = rng.choice([0, 0, 1, 2, 3, rng.randrange(8)])
        if win:
            keys += ["WINDOW", str(win)]
            offset = 8 * win
        for _ in range(rng.choice([0, 0, rng.randrange(1, 10)])):
            k = rng.choice([">", ">", "<"])
            keys.append(k)
            offset = offset + 1 if k == ">" else max(0, offset - 1)
        got = c.run(" ".join(keys), hold=rng.choice([100, 100, 200_000]))
        want = display(v, w, comp, base, lead0, offset=offset)
        if got != want:
            bad.append((base, comp, w, lead0, offset, v, want, got))
        elif offset == 0 and rng.random() < 0.3:
            sb = rng.choice(["HEX", "OCT", "BIN", "DEC"])
            got = held(c, "SHOW" + sb)
            want = display(v, w, comp, sb, lead0)
            if got != want:
                bad.append(("SHOW" + sb, comp, w, lead0, 0, v, want, got))
    for base, comp, w, lead0, offset, v, want, got in bad[:10]:
        print(f"{base} comp={comp} w={w} lead0={int(lead0)} off={offset} x={v:x}: model {want!r} plus {got!r}")
    print(f"plus display {args.n - len(bad)}/{args.n} ok, {time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

"""16C+ digit entry above 64 bits: plus mode vs a model.

Per case: fresh machine, base HEX, OCT, BIN or DEC, a complement mode,
word size 65-128, a random full stack, then either ENTER (stack lift
off) or x<>y (lift on), then random digit keys (leading zeros included,
some not valid in the base), enough to pass 64 bits. Expected (the stock
rules, checked on the stock ROM with --stock):
- a key that is not a digit of the base is ignored;
- X = X * base + d while the result fits in W bits, otherwise the digit
  is ignored; the first digit lifts the full stack if lift is on;
- BSP: X = X div base while 2 or more digits are typed, else X = 0, entry
  ends and the stack lift is disabled;
- the display shows at least the digits typed (leading zeros), except
  with flag 3 in HEX/OCT/BIN.
Checks X, Y, Z, T (all 128 bits) and the display.

    python tests/test_plus_entry.py [-n 200] [--seed 1] [--bsp] [--base B]
    python tests/test_plus_entry.py --stock     # the rules, W <= 64
"""
import argparse
import pathlib
import random
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from nut.hp16c import HP16C  # noqa: E402
from model.hp16c_model import display  # noqa: E402
from tests.test_plus_stage1 import full, poke_ext  # noqa: E402
from tests.test_plus_display import set_x  # noqa: E402

OBJ = ROOT / "build" / "16cplus.obj"
BASES = {"HEX": 16, "OCT": 8, "BIN": 2, "DEC": 10}
MODES = ["UNSGN", "1S", "2S"]


def set_stack(c, vals):
    """vals: x, y, z, t (full values); low parts via RAM, ext via poke."""
    from nut.routine import to_digits, from_digits
    x, y, z, t = vals
    set_x(c, x)
    r7 = from_digits(c.cpu.ram[7])
    for reg, sh, v in [(0, 3, y), (1, 5, z), (2, 7, t)]:
        c.cpu.ram[reg] = to_digits(v & ((1 << 56) - 1))
        r7 = (r7 & ~(0xFF << (4 * sh))) | (((v >> 56) & 0xFF) << (4 * sh))
    c.cpu.ram[7] = to_digits(r7)
    poke_ext(c, {"y": y >> 64, "z": z >> 64, "t": t >> 64})


def key_name(d):
    return d if d == "BSP" else f"{d:X}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=200)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--bsp", action="store_true", help="mix in BSP keys")
    ap.add_argument("--base", choices=list(BASES), action="append",
                    help="bases to test (default all)")
    ap.add_argument("--stock", action="store_true",
                    help="stock ROM, word sizes 1-64 (checks the model rules)")
    args = ap.parse_args()
    rng = random.Random(args.seed)
    bases = args.base or list(BASES)
    bad = []
    t0 = time.time()
    for _ in range(args.n):
        c = HP16C().start() if args.stock else HP16C(plus=[OBJ]).start()
        if args.stock:
            w = rng.choice([4, 8, 13, 16, 32, 40, 63, 64])
        else:
            w = rng.choice([65, 66, 68, 72, 100, 125, 127, 128])
        base = rng.choice(bases)
        b = BASES[base]
        comp = rng.choice([0, 1, 2])
        lead0 = rng.random() < 0.2
        c.run(f"HEX 0 WSIZE {w:X} WSIZE {MODES[comp]} "
              f"{'SF' if lead0 else 'CF'} 3 {base}")
        m = (1 << w) - 1
        st = [rng.getrandbits(w) for _ in range(4)]
        set_stack(c, st)
        lift = rng.random() < 0.5
        c.run("X<>Y" if lift else "ENTER")
        x, y, z, t = st
        if lift:
            x, y = y, x                       # after x<>y
        else:
            y, z, t = x, y, z                 # after ENTER (lift disabled)
        bits = b.bit_length() - 1 or 3.33
        nd = int(w / bits) + 4
        digits = [rng.randrange(b) for _ in range(rng.randrange(1, nd))]
        if rng.random() < 0.2:
            digits = [0] * rng.randrange(1, 4) + digits
        if b < 16 and rng.random() < 0.3:     # keys that are not digits
            for _ in range(rng.randrange(1, 4)):
                digits.insert(rng.randrange(len(digits) + 1),
                              rng.randrange(b, 16))
        if args.bsp:                          # mix in backspaces
            seq = []
            for d in digits:
                seq.append(d)
                if rng.random() < 0.25:
                    seq += ["BSP"] * rng.randrange(1, 4)
            digits = seq
        # model: entry state, digit count, stack lift
        v, count, entry = x, 0, False
        for d in digits:
            if d == "BSP":
                if entry and count > 1:
                    v, count = v // b, count - 1
                else:                         # clears X, entry ends,
                    v, count, entry = 0, 0, False
                    lift = False              #  stack lift disabled
                continue
            if d >= b:
                continue
            if not entry:
                if lift:
                    t, z, y = z, y, v
                v, count, entry = 0, 0, True
            if v * b + d <= m:
                v, count = v * b + d, count + 1
        c.run(" ".join(key_name(d) for d in digits),   # short or held keys
              hold=rng.choice([100, 100, 20_000]))
        got = [full(c, n) & m for n in ("x", "y", "z", "t")]
        want = [v, y & m, z & m, t & m]
        disp_want = display(v, w, comp, base, lead0,
                            typed=count if entry else 0)
        disp = c.display()
        if got != want or disp != disp_want:
            bad.append((base, comp, w, lead0, lift, digits,
                        [hex(g) for g in got], [hex(g) for g in want],
                        disp, disp_want))
    for base, comp, w, lead0, lift, digits, got, want, disp, dw in bad[:6]:
        print(f"{base} comp={comp} w={w} flag3={int(lead0)} lift={lift} "
              f"keys={' '.join(key_name(d) for d in digits)}")
        print(f"   got  {got} {disp!r}")
        print(f"   want {want} {dw!r}")
    what = "stock entry" if args.stock else "plus entry"
    print(f"{what} {args.n - len(bad)}/{args.n} ok, {time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

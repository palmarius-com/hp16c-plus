"""Float mode of the 16C+ against the stock 16C ROM in the simulator (differential, key by key).

Reference: the original HP-16C ROM running in our simulator (the 16C+ must be 100% compatible with
it, J-F Garnier's rule). After EVERY key the display text and the lit annunciators of both machines
must be equal. Three parts:
  1. edge cases written by hand: digit entry limits, '.', EEX and exponent digits, CHS during entry
     and in the exponent, BSP in every entry state (the bug Amphitryon found: BSP in float mode
     gave garbage after the last digit), BSP at zero, after ENTER, after an operation, CLx, stack
     keys, LSTx, STO/RCL, FLOAT n for n = 0..9, errors (overflow, 1/0, sqrt of negative), switching
     between float and the integer modes and back;
  2. the same with a plus-only prefix: WSIZE 65..128 and a small integer, then FLOAT n (FLOAT leaves
     plus mode, so the float behaviour afterwards must be the stock one; Amphitryon's other report:
     FLOAT 5 at word size 85 gave FLOAT 0);
  3. a seeded random walk over the float keys.
Usage: python tests/test_plus_floatmode.py [-n N] [--seed S]
"""
import argparse
import os
import pathlib
import random
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from nut.hp16c import HP16C  # noqa: E402

OBJ = pathlib.Path(os.environ.get("PLUS_OBJ", ROOT / "build" / "16cplus.obj"))   # PLUS_OBJ: test another build


def stock():
    return HP16C().start()


def plus():
    return HP16C(plus=[OBJ]).start()


def state(c):
    return (c.display().strip(), tuple(c.annunciators()))


def run_pair(prefix_stock, prefix_plus, tail, label, fails):
    a, b = stock(), plus()
    if prefix_stock:
        a.run(prefix_stock)
    if prefix_plus:
        b.run(prefix_plus)
    seen = []
    n = 0
    for tok in tail.split():
        a.run(tok)
        b.run(tok)
        seen.append(tok)
        n += 1
        sa, sb = state(a), state(b)
        if sa != sb:
            fails.append((label, " ".join(seen), sa, sb))
            return n
    return n


EDGE = [
    # digit entry
    "FLOAT 4 1 2 3", "FLOAT 4 1 2 3 4 5 6 7 8 9 0", "FLOAT 4 1 2 3 4 5 6 7 8 9 0 1 2 3", "FLOAT 4 . 5", "FLOAT 4 . . 5",
    "FLOAT 4 1 . 2 . 3", "FLOAT 4 0 0 0 1", "FLOAT 4 . 0 0 0 1", "FLOAT 4 1 2 . 3 4 5 6 7 8 9 0 1 2",
    "FLOAT 4 EEX", "FLOAT 4 EEX 5", "FLOAT 4 1 EEX 5", "FLOAT 4 1 EEX 5 0", "FLOAT 4 1 EEX 9 9", "FLOAT 4 1 EEX 1 0 0",
    "FLOAT 4 1 EEX 5 EEX", "FLOAT 4 1 EEX CHS 5", "FLOAT 4 1 EEX 5 CHS", "FLOAT 4 1 EEX 5 CHS CHS",
    "FLOAT 4 CHS 5", "FLOAT 4 5 CHS", "FLOAT 4 5 CHS CHS", "FLOAT 4 . CHS", "FLOAT 4 CHS", "FLOAT 4 CHS CHS",
    # BSP in every entry state
    "FLOAT 4 BSP", "FLOAT 4 BSP BSP", "FLOAT 4 1 BSP", "FLOAT 4 1 2 3 BSP BSP BSP BSP BSP",
    "FLOAT 4 1 . 2 BSP BSP BSP BSP", "FLOAT 4 . BSP", "FLOAT 4 . 5 BSP BSP", "FLOAT 4 1 2 3 4 5 6 7 8 9 0 BSP BSP BSP",
    "FLOAT 4 1 EEX BSP", "FLOAT 4 1 EEX 5 BSP", "FLOAT 4 1 EEX 5 BSP BSP", "FLOAT 4 1 EEX 5 BSP BSP BSP", "FLOAT 4 1 EEX 5 BSP BSP BSP BSP",
    "FLOAT 4 1 EEX 5 0 BSP", "FLOAT 4 1 EEX 5 0 BSP BSP BSP", "FLOAT 4 1 EEX CHS BSP", "FLOAT 4 1 EEX CHS 5 BSP BSP BSP",
    "FLOAT 4 5 CHS BSP", "FLOAT 4 5 CHS BSP BSP", "FLOAT 4 CHS BSP", "FLOAT 4 CHS 1 2 BSP BSP BSP BSP",
    "FLOAT 4 1 2 3 ENTER BSP", "FLOAT 4 1 2 3 ENTER BSP BSP", "FLOAT 4 1 ENTER 2 + BSP", "FLOAT 4 1 ENTER 2 + BSP 3",
    "FLOAT 4 1 ENTER 2 + BSP BSP", "FLOAT 4 5 CLX BSP", "FLOAT 4 5 CLX 3", "FLOAT 4 1 2 CLX BSP BSP",
    "FLOAT 4 1 2 3 ENTER 4 5 6 BSP BSP BSP BSP", "FLOAT 4 1 ENTER 2 ENTER 3 ENTER 4 BSP BSP BSP",
    "FLOAT 4 1 2 BSP 3 4 BSP BSP 5", "FLOAT 4 1 . 2 BSP 3", "FLOAT 4 1 EEX 5 BSP 3", "FLOAT 4 1 EEX 2 BSP 3 4",
    "FLOAT 0 1 2 3 BSP BSP BSP BSP", "FLOAT 9 1 2 3 BSP BSP BSP BSP", "FLOAT 1 1 2 3 BSP BSP BSP BSP",
    "FLOAT 5 1 . 5 BSP BSP BSP", "FLOAT 7 9 9 9 BSP BSP BSP BSP", "FLOAT 3 1 2 BSP BSP BSP BSP BSP BSP",
    # arithmetic, stack, LSTx
    "FLOAT 4 1 ENTER 3 /", "FLOAT 4 2 SQRT", "FLOAT 4 3 ENTER 4 *", "FLOAT 4 3 ENTER 4 -", "FLOAT 4 3 ENTER 4 +",
    "FLOAT 4 1 ENTER 3 / ENTER 3 *", "FLOAT 4 2 1/X", "FLOAT 4 0 1/X", "FLOAT 4 CHS 4 SQRT", "FLOAT 4 0 SQRT",
    "FLOAT 4 1 EEX 9 9 ENTER 1 0 *", "FLOAT 4 1 EEX 9 9 ENTER 1 0 * BSP", "FLOAT 4 1 EEX CHS 9 9 ENTER 1 0 /",
    "FLOAT 4 5 ENTER 0 /", "FLOAT 4 5 ENTER 0 / 3", "FLOAT 4 5 ENTER 0 / BSP", "FLOAT 4 5 ENTER 0 / CLX",
    "FLOAT 4 1 ENTER 2 ENTER 3 RDN", "FLOAT 4 1 ENTER 2 ENTER 3 RUP", "FLOAT 4 1 ENTER 2 X<>Y", "FLOAT 4 1 ENTER 2 + LSTX",
    "FLOAT 4 1 ENTER 2 + LSTX BSP", "FLOAT 4 7 ENTER 2 / LSTX LSTX", "FLOAT 4 5 ABS", "FLOAT 4 5 CHS ABS", "FLOAT 4 5 CHS ABS BSP",
    "FLOAT 4 5 STO 1 2 RCL 1", "FLOAT 4 5 STO 1 CLX RCL 1 BSP", "FLOAT 4 1 2 STO 5 BSP RCL 5", "FLOAT 4 1 STO 1 2 STO 2 RCL 1 RCL 2 +",
    "FLOAT 4 1 . 5 ENTER 2 . 5 +", "FLOAT 4 1 . 5 ENTER 2 . 5 + BSP",
    "FLOAT 4 9 9 9 9 9 9 9 9 9 9 ENTER 1 +", "FLOAT 4 1 EEX 5 ENTER 1 +", "FLOAT 4 1 EEX 5 ENTER 1 + BSP",
    # FLOAT n
    "FLOAT 0", "FLOAT 1", "FLOAT 2", "FLOAT 3", "FLOAT 4", "FLOAT 5", "FLOAT 6", "FLOAT 7", "FLOAT 8", "FLOAT 9",
    "FLOAT 4 FLOAT 2", "FLOAT 4 1 2 3 FLOAT 2", "FLOAT 4 1 2 3 ENTER FLOAT 2", "FLOAT 4 1 ENTER 3 / FLOAT 9",
    "FLOAT 9 1 ENTER 3 / FLOAT 0", "FLOAT 2 1 ENTER 3 / FLOAT 6 BSP", "FLOAT 4 FLOAT 4 FLOAT 4",
    # float <-> integer modes
    "DEC 5 FLOAT 3", "DEC 255 FLOAT 3", "HEX F F FLOAT 2", "DEC 1 2 3 FLOAT 4 DEC", "DEC 1 2 3 FLOAT 4 HEX", "FLOAT 4 1 . 5 DEC",
    "FLOAT 4 1 . 5 HEX 1 2", "FLOAT 4 1 . 5 DEC BSP", "FLOAT 4 5 ENTER 3 / DEC", "FLOAT 4 5 ENTER 3 / OCT BSP", "FLOAT 4 BIN",
    "FLOAT 4 DEC 1 2 3 BSP BSP BSP BSP", "FLOAT 4 HEX F BSP BSP", "DEC 1 2 3 FLOAT 3 BSP",
    "DEC 5 ENTER 3 FLOAT 2", "DEC 5 ENTER 3 FLOAT 2 +", "DEC 1 6 WSIZE FLOAT 4", "DEC 1 EEX 5 FLOAT 3",
]
# keys of the random walk (float mode); WSIZE prefixes are plus-only
KEYS = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9", ".", "EEX", "CHS", "BSP", "ENTER", "+", "-", "*", "/", "CLX",
        "X<>Y", "RDN", "RUP", "LSTX", "SQRT", "1/X", "ABS"]
WEIGHT = [6] * 10 + [3, 3, 3, 8, 3, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1]
assert len(WEIGHT) == len(KEYS)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=250, help="random sequences")
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()
    fails = []
    keys = 0
    for seq in EDGE:
        keys += run_pair("", "", seq, "edge", fails)
    print(f"edge cases: {len(EDGE)} sequences, {keys} keys compared, differences {len(fails)}")
    f1 = len(fails)
    # plus-only prefix: a word size above 64 and a small integer, then the same float tail
    pre = 0
    for ws in (65, 72, 85, 100, 128):
        for val in ("5", "1 0 0"):
            for tail in ("FLOAT 3", "FLOAT 0", "FLOAT 5 1 2 BSP BSP BSP", "FLOAT 4 1 ENTER 3 / BSP", "FLOAT 9 2 SQRT", "FLOAT 2 1 EEX 5 BSP BSP"):
                pre += 1
                digits = " ".join(str(ws))
                run_pair(f"DEC {val}", f"DEC {digits} WSIZE {val}", tail, f"plus prefix WSIZE {ws} value {val}", fails)
    print(f"plus-only prefix (WSIZE {{65,72,85,100,128}} then FLOAT n): {pre} sequences, differences {len(fails) - f1}")
    f2 = len(fails)
    rnd = random.Random(args.seed)
    tot = 0
    for i in range(args.n):
        n = rnd.randint(2, 16)
        tail = " ".join(rnd.choices(KEYS, WEIGHT, k=n))
        d = rnd.randint(0, 9)
        tot += run_pair("", "", f"FLOAT {d} {tail}", f"random #{i}", fails)
    print(f"random walk: {args.n} sequences, {tot} keys compared, differences {len(fails) - f2}")
    for label, seq, sa, sb in fails[:30]:
        print(f"DIFF [{label}] after: {seq}\n     stock {sa}\n     plus  {sb}")
    if len(fails) > 30:
        print(f"... and {len(fails) - 30} more")
    print("float mode:", "all equal to the stock 16C" if not fails else f"{len(fails)} differences")
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()

"""Replay stored test cases (tests/cases/*.jsonl) on the emulator.

Each case is played on one machine, without reset in between (cases set
their own state). Compares every expected field that the emulator can
observe. --shuffle plays the cases in random order, which shows they do
not depend on each other.

    python tests/replay_cases.py [files...] [--shuffle] [--rom path]

--rom selects another ROM image (e.g. a patched 16C+ later); the default
is the stock 16C.
"""
import argparse
import json
import pathlib
import random
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from nut.hp16c import HP16C  # noqa: E402


def observe(c, keys_expected, w):
    got = {}
    for k in keys_expected:
        if k in ("display", "display_float"):
            got[k] = c.display()
        elif k == "error":
            got[k] = c.error()
        elif k == "result":
            got[k] = c.display().split()[0]
        elif k == "skipped":
            got[k] = c.display().split()[0] == "0"
        elif k == "m":
            got[k] = "".join(f"{d:x}" for d in reversed(c.cpu.m))
        elif k in ("c", "g"):
            got[k] = c.flags_cg()[0 if k == "c" else 1]
        elif k == "i":
            got[k] = f"{c.reg_i():017x}"
        elif k in ("x", "y", "z", "t", "lastx"):
            m = (1 << w) - 1
            got[k] = f"{c.stack()[k] & m:0{(w + 3) // 4}x}"
    return got


def intended_change(case):
    """Reason if the 16C+ is meant to differ from the stock 16C here."""
    if case.get("op") == "WSIZE" and "inputs" in case:
        w, x = case["w"], int(case["inputs"]["x"], 16)
        comp = {"unsigned": 0, "1's": 1, "2's": 2}[case["comp"]]
        if comp and x >> (w - 1):          # negative: |x|
            x = ((1 << w) - x) if comp == 2 else ((1 << w) - 1 - x)
        if 65 <= x <= 128:
            return "WSIZE 65-128 accepted (stock: Error 2)"
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("files", nargs="*")
    ap.add_argument("--shuffle", action="store_true")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--rom", help="ROM file instead of nonpareil/16c.rom")
    ap.add_argument("--plus", nargs="*", default=None,
                    help="16C+ machine (15C-192 chip map) with these object "
                         "files on top of the 16C ROM (none = chip map only)")
    ap.add_argument("--show", type=int, default=10)
    args = ap.parse_args()
    files = [pathlib.Path(f) for f in args.files] or \
        sorted((ROOT / "tests" / "cases").glob("*.jsonl"))
    cases = []
    for f in files:
        cases += [json.loads(line) for line in f.read_text().splitlines() if line]
    if args.shuffle:
        random.Random(args.seed).shuffle(cases)
    kw = {"rom_files": [pathlib.Path(args.rom).resolve()]} if args.rom else {}
    if args.plus is not None:
        kw["plus"] = [pathlib.Path(f).resolve() for f in args.plus]
    c = HP16C(**kw).start()
    t0 = time.time()
    bad = []
    changed = []
    for case in cases:
        if args.plus is not None and intended_change(case):
            c.run(" ".join(case["keys"]))
            changed.append(case["id"])
            continue
        c.run(" ".join(case["keys"]))
        exp = case["expect"]
        got = observe(c, exp, case.get("w", 64))
        diff = {k: (exp[k], got.get(k)) for k in exp if exp[k] != got.get(k)}
        if diff:
            bad.append((case["id"], diff))
    for cid, diff in bad[:args.show]:
        print(cid, diff)
    if changed:
        print(f"{len(changed)} cases skipped as intended 16C+ changes: "
              + " ".join(changed[:5]))
    print(f"replay {len(cases) - len(bad)}/{len(cases)} ok "
          f"({len(files)} files{', shuffled' if args.shuffle else ''}), "
          f"{time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

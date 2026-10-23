"""Stock HP-16C ROM in the Python emulator: handbook-level checks.

Run:  python -I tests/test_stock16c.py
Needs the ROMs (tools/fetch_nonpareil.py).
"""
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
from nut.hp16c import HP16C  # noqa: E402

CASES = [
    # (script from a fresh machine, expected display)
    ("", "        0 h"),
    ("DEC 25 ENTER 7 *", "      175 d"),
    ("FFFF ENTER 1 +", "        0 h"),               # 16-bit wrap
    ("0 WSIZE DEC 1234567890 ENTER *", " 19052100 .d"),
    ("20 WSIZE 2S DEC 1 CHS", "       -1 d"),        # 32 bits
    ("8 WSIZE UNSGN DEC 200 ENTER 100 +", "       44 d"),
]


def status(c):
    """Hold f STATUS and read the display."""
    c.run("f")
    c.cpu.press(c.keys[48])
    c.cpu.run(20000)
    text = c.display()
    c.cpu.release()
    c.cpu.run(10**6)
    return text


def main():
    fails = 0
    for script, want in CASES:
        got = HP16C().start().run(script) if script else HP16C().start().display()
        ok = got == want
        fails += not ok
        print(f"{'ok  ' if ok else 'FAIL'} {script!r:40} {got!r} (want {want!r})")
    st = status(HP16C().start())
    ok = st == "  2-16-0000"
    fails += not ok
    print(f"{'ok  ' if ok else 'FAIL'} STATUS at power-on {st!r}")
    print("FAILED" if fails else "all passed")
    return 1 if fails else 0


if __name__ == "__main__":
    sys.exit(main())

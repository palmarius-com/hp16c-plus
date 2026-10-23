"""Return stack depth check for the 16C+, measured.

The Nut return stack has 4 levels; a fifth call silently drops the
oldest return address. This runs the 16C+ tests in-process with the
emulator's push and pop counted: at every entry point of the new code
the count starts at 0, and the deepest nesting reached (new code and the
original routines it calls) is compared with the levels free there:

- function handlers (P_FUNC table): 4, they leave through 0x008B; also
  those that pass to an original routine: RCL (i) and STO (i) end in
  RCL_N/STO_N (0x008B, 0x0090), CLRREG at 0x0091, GSB I and GTO I in
  the program pointer code (GSB I measured fine in test_plus_regs.py),
  none returns to 0x0089 (the measured 4 levels push it out);
- P_WSIZE, P_STATUS, P_MEM: 3 (0x0089 or the caller's return must
  survive);
- P_FORMAT: 2 (SHOW calls it with two returns pending);
- P_MEMRESET: 4 (it makes no call and leaves the stack as it was; the
  depth seen is the original memory reset's own after the jump back);
- other hooks: 4.

An entry's count ends at the main loop or the error display. Measured,
so it covers the paths the tests run (they reach every error and undo
path). A static walk of the machine code gave false alarms: it cannot
tell `a=a-1 x ; ?nc go L` (no borrow possible) from a real branch.

--slack also prints how many `c=0 w ; ldi $010 ; sel ram` places have
a free level left (for replacing them by a call).

    python tools/depth16c.py [-n 6] [--slack]
"""
import argparse
import contextlib
import io
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from nut.cpu import NutCPU  # noqa: E402
from nut import disasm  # noqa: E402
from tools.build_plus import build  # noqa: E402

ENDS = {0x0089, 0x008B, 0x008D, 0x0090, 0x0091, 0x0094, 0x0098, 0x105E,
        0x105F, 0x0687}              # main loop entries, error display
PASS3 = set()                    # see above: no pass-through needs 0x0089
FREE = {"P_WSIZE": 3, "P_STATUS": 3, "P_MEM": 3, "P_FORMAT": 2,
        "P_DIGIT": 4, "P_BSP": 4, "P_RCL": 4, "P_STO": 4, "P_FLOAT": 4,
        "P_MEMRESET": 4, "P_FUNC": 4}


def table_handlers(words, sym):
    """golong targets of the P_FUNC table: entries (word 1, golong) right
    after the gosub FT_TAB, ended by a 0 word."""
    img = [0] * 0x4000
    for a, w in words.items():
        img[a] = w
    a = sym["P_FUNC"]
    while True:
        ln, mn, tgt, kind = disasm.decode(img, a)
        if "gosub" in mn and tgt == sym["FT_TAB"]:
            break
        a += ln
    t, out = a + 2, []
    while img[t] != 0:
        out.append(disasm.decode(img, t + 1)[2])
        t += 3
    return out


class Meter:
    def __init__(self, roots):
        self.roots = roots              # addr -> (name, free)
        self.cur, self.depth = None, 0
        self.max = {}                   # name -> deepest
        self.at = {}                    # pc -> (deepest there, free)

    def install(self):
        meter = self
        push, pop, cycle = NutCPU._push, NutCPU._pop, NutCPU.cycle

        def _push(cpu, a):
            if meter.cur:
                meter.depth += 1
                n = meter.cur[0]
                meter.max[n] = max(meter.max.get(n, 0), meter.depth)
            return push(cpu, a)

        def _pop(cpu):
            if meter.cur:
                meter.depth -= 1
                if meter.depth < 0:
                    meter.cur = None
            return pop(cpu)

        def cycle_(cpu):
            pc = cpu.pc
            if pc in meter.roots:
                meter.cur, meter.depth = meter.roots[pc], 0
            elif pc in ENDS:
                meter.cur = None
            if meter.cur:
                d, f = meter.at.get(pc, (0, 9))
                meter.at[pc] = (max(d, meter.depth), min(f, meter.cur[1]))
            return cycle(cpu)
        NutCPU._push, NutCPU._pop, NutCPU.cycle = _push, _pop, cycle_


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=6, help="cases per function")
    ap.add_argument("--slack", action="store_true")
    args = ap.parse_args()
    a, words, _, errors = build()
    if errors:
        sys.exit("\n".join(errors))
    sym = a.symbols
    inv = {}
    for k, v in sorted(sym.items(), key=lambda kv: kv[1]):
        inv.setdefault(v, k)
    roots = {sym[n]: (n, f) for n, f in FREE.items() if n in sym}
    for h in table_handlers(words, sym):
        name = inv.get(h, hex(h))
        roots[h] = (name, 3 if name in PASS3 else 4)
    meter = Meter(roots)
    meter.install()
    import tests.test_plus_funcs as F
    import tests.test_plus_regs as R
    import tests.test_plus_float as FL
    import tests.test_plus_display as D
    import tests.test_plus_entry as E
    import tests.test_plus_stage1 as S
    argv = sys.argv
    for mod, extra in ((S, []), (F, ["-n", str(args.n), "--seq", "10"]),
                       (R, ["-n", str(args.n)]), (FL, ["-n", "40"]),
                       (D, ["-n", "40"]), (E, ["-n", "40", "--bsp"])):
        sys.argv = [mod.__file__] + extra
        with contextlib.redirect_stdout(io.StringIO()):
            mod.main()
    sys.argv = argv
    bad = 0
    for addr, (name, free) in sorted(roots.items(), key=lambda kv: kv[1]):
        d = meter.max.get(name)
        if d is None:
            continue
        flag = "OVER" if d > free else "ok  "
        bad += d > free
        print(f"{flag} {name:10} deepest {d}, free {free}")
    unseen = [n for n, f in roots.values() if n not in meter.max]
    print(f"{len(meter.max)} entry points measured, {bad} over; not reached "
          f"or no calls: {', '.join(sorted(unseen)) or '-'}")
    if args.slack:
        ok = tot = 0
        for x, w, src in a.listing:
            if "ldi $010" in src and x >= 0x2000:
                tot += 1
                d, f = meter.at.get(x, (None, None))
                ok += d is not None and d + 1 <= f
        print(f"`ldi $010` places: {tot}, with a free level: {ok}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

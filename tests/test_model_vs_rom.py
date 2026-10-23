"""Differential test: reference model (model/) vs stock 16C ROM.

For each random case: set word size, complement mode, flags 4/5 and the
stack on the emulated 16C by keystrokes (HEX mode), read the resulting
state back, start the model from that state, run the same function on
both and compare X, Y, Z, T, LSTx, carry, out-of-range and errors.

    python tests/test_model_vs_rom.py [-n 40] [--seed 1] [--ops + - ...]
"""
import argparse
import pathlib
import random
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
from nut.hp16c import HP16C  # noqa: E402
from model.hp16c_model import Model16C, Error16C  # noqa: E402

COMP_KEY = {0: "UNSGN", 1: "1S", 2: "2S"}
WSIZES = [1, 2, 3, 4, 5, 7, 8, 9, 12, 15, 16, 17, 24, 31, 32, 33, 48, 55,
          56, 57, 63, 64]


def special(w, rng):
    m = (1 << w) - 1
    s = 1 << (w - 1)
    pool = [0, 1, 2, 3, m, m - 1, s, s - 1, s + 1, rng.randrange(1 << w)]
    v = rng.choice(pool) if rng.random() < 0.5 else rng.randrange(1 << w)
    return v & m


def operands(op, w, comp, rng):
    """t, z, y, x patterns suited to the function."""
    t, z, y, x = (special(w, rng) for _ in range(4))
    if op in ("RLN", "RRN", "RLCN", "RRCN", "MASKL", "MASKR", "SB", "CB"):
        n = rng.choice([0, 1, w - 1, w, w + 1, rng.randrange(w + 1)])
        if n >= (1 << w) or (comp and n >= (1 << (w - 1))):
            n = rng.randrange(1 << (w - 1)) if w > 1 else 0
        x = n
    if op == "WSIZE":
        x = rng.choice([0, 1, 4, 8, 16, 32, 63, 64, 65, rng.randrange(66)])
        if x >= (1 << w) or (comp and x >= (1 << (w - 1))):
            x = rng.randrange(1 << (w - 1)) if w > 1 else 0
    return t, z, y, x


def setup_script(w, comp, carry, out, t, z, y, x, i=None):
    keys = ["BSP", "HEX", "0", "WSIZE"]
    if w != 64:
        keys += [f"{w:X}", "WSIZE"]
    keys += [COMP_KEY[comp], "SF" if carry else "CF", "4",
             "SF" if out else "CF", "5"]
    # one token per hex digit: "CB" would otherwise be the CB key
    hx = lambda v: " ".join(f"{v:X}")
    if i is not None:
        keys += [hx(i), "STO", "I"]
    keys += [hx(t), "ENTER", hx(z), "ENTER", hx(y), "ENTER", hx(x)]
    return " ".join(keys)


def read_model(c):
    st = c.stack()
    carry, out = c.flags_cg()
    w = c.wsize()
    m = (1 << w) - 1
    return Model16C(w=w, comp=c.complement(), x=st["x"] & m, y=st["y"] & m,
                    z=st["z"] & m, t=st["t"] & m, lastx=st["lastx"] & m,
                    carry=carry, out=out, i=c.reg_i())


def state(md):
    return (md.x, md.y, md.z, md.t, md.lastx, md.carry, md.out, md.i)


# Keys that do not end digit entry: finish entry with ENTER first, so that
# M holds the real X before and after the op.
NON_TERMINATING = {"UNSGN", "1S", "2S"}


def run_case(c, op, w, comp, carry, out, vals, i=None):
    script = setup_script(w, comp, carry, out, *vals, i=i)
    if op in NON_TERMINATING:
        script += " ENTER"
    c.run(script)
    pre = read_model(c)
    t, z, y, x = vals
    if op in NON_TERMINATING:
        want_stack = (z, y, x, x)
    else:
        # X is still in digit entry: M holds the entry buffer, not the
        # value. The op ends entry; use
        # the typed X.
        pre.x = x
        want_stack = (t, z, y, x)
    if (pre.w, pre.comp, pre.carry, pre.out) != (w, comp, carry, out) or \
            (pre.t, pre.z, pre.y, pre.x) != want_stack:
        return "setup", pre, None, None
    model = Model16C(**{k: getattr(pre, k) for k in
                        ("w", "comp", "x", "y", "z", "t", "lastx",
                         "carry", "out", "i")})
    try:
        model.do(op)
        want = state(model)
    except Error16C as e:
        want = f"Error {e.n}"
    c.run(op)
    err = c.error()
    if err:
        got = err
    else:
        got = state(read_model(c))
    return ("ok" if got == want else "diff"), pre, want, got


def fmt(s, w):
    if isinstance(s, str) or s is None:
        return str(s)
    d = (w + 3) // 4
    x, y, z, t, l, c, g, i = s
    return (f"x={x:0{d}x} y={y:0{d}x} z={z:0{d}x} t={t:0{d}x} "
            f"lx={l:0{d}x} C={int(c)} G={int(g)} I={i:017x}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=40, help="cases per function")
    ap.add_argument("--seed", type=int, default=1)
    # (i) ops depend on register contents: tests/test_indirect_vs_rom.py
    ap.add_argument("--ops", nargs="*",
                    default=[o for o in Model16C.OPS if "(I)" not in o]
                    + list(Model16C.COMP_OPS))
    ap.add_argument("--show", type=int, default=3, help="diffs shown per op")
    args = ap.parse_args()
    rng = random.Random(args.seed)
    c = HP16C().start()
    total = bad = 0
    t0 = time.time()
    for op in args.ops:
        diffs = []
        for _ in range(args.n):
            w = rng.choice(WSIZES)
            comp = rng.choice([0, 1, 2])
            carry, out = rng.random() < 0.5, rng.random() < 0.5
            vals = operands(op, w, comp, rng)
            i = special(w, rng) if rng.random() < 0.5 else None
            status, pre, want, got = run_case(c, op, w, comp, carry, out,
                                              vals, i)
            total += 1
            if status != "ok":
                bad += 1
                diffs.append((status, w, comp, pre, want, got))
        print(f"{op:6} {args.n - len(diffs):3}/{args.n} ok")
        for status, w, comp, pre, want, got in diffs[:args.show]:
            print(f"   {status} w={w} comp={comp} pre: {fmt(state(pre), w)}")
            print(f"        model: {fmt(want, w)}")
            print(f"        rom:   {fmt(got, w)}")
    print(f"total {total - bad}/{total} ok, {time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

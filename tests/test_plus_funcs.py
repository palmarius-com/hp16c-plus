"""16C+ functions above 64 bits: plus mode vs the model.

Random cases at word sizes 65-128, all three complement modes, both
flags: the full 128-bit stack (X, Y, Z, T, LSTx) and flags 4/5 are
written into the machine state (N, M, RAM 0-3, RAM 7, RAM 6 digit 7,
extension RAM 0x11-0x16), the function key is pressed, and X, Y, Z, T,
LSTx, carry, G (or the error and the unchanged state) are compared with
Model16C.

    python tests/test_plus_funcs.py [-n 40] [--seed 1] [--ops + - ...]
"""
import argparse
import pathlib
import random
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from nut.hp16c import HP16C  # noqa: E402
from nut.routine import from_digits, to_digits  # noqa: E402
from model.hp16c_model import Model16C, Error16C  # noqa: E402

OBJ = ROOT / "build" / "16cplus.obj"
COMP_KEY = {0: "UNSGN", 1: "1S", 2: "2S"}
OPS = ["+", "-", "AND", "OR", "XOR", "NOT", "SL", "SR", "ASR", "RL", "RR",
       "RLC", "RRC", "RLN", "RRN", "RLCN", "RRCN", "LJ", "#B", "MASKL",
       "MASKR", "SB", "CB", "CHS", "ABS", "*", "/", "RMD", "DBLX", "DBL/",
       "DBLR", "STO I", "RCL I", "X<>I", "RCL (I)", "STO (I)", "X<>(I)",
       "SQRT"]
I_OPS = {"STO I", "RCL I", "X<>I", "RCL (I)", "STO (I)", "X<>(I)"}
COUNT_OPS = {"RLN", "RRN", "RLCN", "RRCN", "MASKL", "MASKR", "SB", "CB"}
NAMES = ("x", "y", "z", "t", "lastx")
IDX = {n: i for i, n in enumerate(NAMES)}
HI7 = {"x": 0, "y": 3, "z": 5, "t": 7, "lastx": 9}     # RAM 7 digit
M56 = (1 << 56) - 1


def special(w, rng):
    m = (1 << w) - 1
    s = 1 << (w - 1)
    pool = [0, 1, 2, 3, m, m - 1, s, s - 1, s + 1, M56, 1 << 56,
            (1 << 64) - 1, 1 << 64, (1 << 120) - 1, 1 << 120,
            rng.getrandbits(64), rng.getrandbits(w)]
    v = rng.choice(pool) if rng.random() < 0.5 else rng.getrandbits(w)
    return v & m


def count_operand(w, comp, rng):
    n = rng.choice([0, 1, 2, w - 1, w, w + 1, w + 2, rng.randrange(w + 2),
                    rng.randrange(w + 2)])
    m = (1 << w) - 1
    if comp and rng.random() < 0.3:             # a negative count
        return (~n & m) if comp == 1 else (-n) & m
    if rng.random() < 0.05:                     # far too large
        return rng.getrandbits(w)
    return n


DIV_OPS = {"/", "RMD", "DBL/", "DBLR"}


def div_operands(op, w, comp, rng, vals):
    """Divisors 0, -0, +-1 and small; for DBL/ and DBLR mostly dividends
    whose quotient fits (high part below the divisor), sometimes not."""
    m = (1 << w) - 1
    x, y, z, t, l = vals
    r = rng.random()
    if r < 0.05:
        x = 0
    elif r < 0.1:
        x = m                                    # -0 (1's), -1 (2's)
    elif r < 0.15:
        x = 1
    elif r < 0.3:
        x = rng.getrandbits(rng.randrange(1, 40))
    if op in ("DBL/", "DBLR") and rng.random() < 0.75 and x:
        neg = comp and x >> (w - 1)
        mag = ((-x) & m if comp == 2 else ~x & m) if neg else x
        if mag > 1:
            y = rng.randrange(mag)                # |quotient| < 2^W
            if comp and rng.random() < 0.5:       # a negative dividend
                d = (y << w) | z
                d = (-d if comp == 2 else ~d) & ((1 << 2 * w) - 1)
                y, z = d >> w, d & m
    return [x, y, z, t, l]


def edge_cases():
    """(op, w, comp, [x, y, z, t, lastx]) at the range limits of * / and
    the DBL functions, in 2's complement unless noted."""
    out = []
    for w in (65, 100, 127, 128):
        m = (1 << w) - 1
        mn = 1 << (w - 1)                          # -2^(W-1) in 2's
        neg = lambda v: (-v) & m
        out += [
            ("*", w, 2, [2, neg(mn >> 1), 0, 0, 0]),     # = -2^(W-1), in range
            ("*", w, 2, [neg(2), mn >> 1, 0, 0, 0]),
            ("*", w, 2, [2, mn >> 1, 0, 0, 0]),          # +2^(W-1): out
            ("*", w, 2, [neg(1), mn, 0, 0, 0]),          # -min: out
            ("*", w, 1, [2, ~(mn >> 1) & m, 0, 0, 0]),   # 1's: out
            ("*", w, 0, [m, m, 0, 0, 0]),                # unsigned max^2
            ("DBLX", w, 2, [neg(1), mn, 5, 6, 7]),
            ("DBLX", w, 1, [0, m, 5, 6, 7]),             # 0 * -0 = -0 (2W)
            ("/", w, 2, [neg(1), mn, 3, 4, 5]),          # min / -1: G, 0
            ("/", w, 2, [1, mn, 3, 4, 5]),
            ("RMD", w, 2, [neg(3), mn, 3, 4, 5]),
            ("/", w, 1, [m, 5, 3, 4, 5]),                # / -0: Error 0
            ("/", w, 0, [1, m, 3, 4, 5]),
        ]
        # DBL: dividend Y:Z = v * q (+ r), divisor X = v
        for v, q, r in ((3, mn, 0), (3, mn, 2), (5, mn - 1, 4), (1, m, 0),
                        (m, m, m - 1), (2, mn, 1)):
            d = v * q + r
            hi, lo = d >> w, d & m
            out.append(("DBLR", w, 0, [v, hi, lo, 9, 8]))
            out.append(("DBL/", w, 0, [v, hi, lo, 9, 8]))
            nd = (-d) & ((1 << 2 * w) - 1)
            out.append(("DBL/", w, 2, [v, nd >> w, nd & m, 9, 8]))
            out.append(("DBLR", w, 2, [neg(v), nd >> w, nd & m, 9, 8]))
        out.append(("DBLR", w, 0, [1, 1, 0, 9, 8]))      # q = 2^W: Error 0
        h = (1 << (w // 2)) - 1
        for v, comp in ((m, 0), (1 << (w - 2), 0), (h * h, 0), (h * h - 1, 0),
                        (h * h + 2 * h, 0), (1, 2), (2, 2), (3, 1), (4, 2),
                        (mn - 1, 2), (neg(4), 2), (m, 1), (0, 1)):
            out.append(("SQRT", w, comp, [v, 1, 2, 3, 4]))
        out.append(("DBL/", w, 1, [3, m, m, 9, 8]))      # 1's -0 / 3
    return out


def i_operands(w, comp, rng):
    """I (max(68, W) bits) and a random register pool. I mostly a register
    number (also negative), sometimes large or random."""
    iw = max(68, w)
    mi = (1 << iw) - 1
    r = rng.random()
    if r < 0.6:
        n = rng.randrange(0, 26)
        if comp and rng.random() < 0.3:
            n = (-n) & mi if comp == 2 else ~n & mi
        i = n
    elif r < 0.75:
        i = rng.choice([1 << 12, 1 << 67, 1 << 68, mi, mi - 1, 1 << (iw - 1)])
    else:
        i = rng.getrandbits(iw)
    pool = [rng.randrange(16) for _ in range(POOL)]
    return i & mi, pool


def put(c, name, v):
    """Write a 128-bit value into the machine slots of a stack register."""
    cpu = c.cpu
    lo = v & M56
    if name == "x":
        cpu.n = to_digits(lo)
        cpu.m = to_digits(lo)
    else:
        cpu.ram[IDX[name] - 1] = to_digits(lo)
    h = HI7[name]
    r7 = from_digits(cpu.ram[7])
    r7 = (r7 & ~(0xFF << (4 * h))) | (((v >> 56) & 0xFF) << (4 * h))
    cpu.ram[7] = to_digits(r7)
    cpu.ram[0x11 + IDX[name]] = to_digits((v >> 64) & M56)
    sh = 8 * IDX[name]
    r16 = from_digits(cpu.ram[0x16])
    r16 = (r16 & ~(0xFF << sh)) | (((v >> 120) & 0xFF) << sh)
    cpu.ram[0x16] = to_digits(r16)


def get(c, name):
    cpu = c.cpu
    lo = from_digits(cpu.n if name == "x" else cpu.ram[IDX[name] - 1])
    h = HI7[name]
    b1 = (from_digits(cpu.ram[7]) >> (4 * h)) & 0xFF
    ext = from_digits(cpu.ram[0x11 + IDX[name]])
    b2 = (from_digits(cpu.ram[0x16]) >> (8 * IDX[name])) & 0xFF
    return lo | (b1 << 56) | (ext << 64) | (b2 << 120)


POOL = 406                      # nibbles, RAM 0xE0 digit 0 up (no program)


def put_i(c, v):
    """I: bits 0-55 RAM 0xFD, 56-63 0xFE digits 4-3, 64-67 0xFE digit 5
    (mirror), 64-119 RAM 0x17, 120-127 RAM 0x16 digits 11-10."""
    cpu = c.cpu
    cpu.ram[0xFD] = to_digits(v & M56)
    fe = cpu.ram[0xFE]
    fe[3], fe[4], fe[5] = (v >> 56) & 0xF, (v >> 60) & 0xF, (v >> 64) & 0xF
    cpu.ram[0x17] = to_digits((v >> 64) & M56)
    r16 = cpu.ram[0x16]
    r16[10], r16[11] = (v >> 120) & 0xF, (v >> 124) & 0xF


def get_i(c):
    cpu = c.cpu
    fe, r16 = cpu.ram[0xFE], cpu.ram[0x16]
    ext = from_digits(cpu.ram[0x17])
    if fe[5] != ext & 0xF:
        return "0xFE digit 5 out of sync"
    return (from_digits(cpu.ram[0xFD]) | (fe[3] << 56) | (fe[4] << 60)
            | (ext << 64) | (r16[10] << 120) | (r16[11] << 124))


def put_pool(c, nibs):
    for j, v in enumerate(nibs):
        c.cpu.ram[0xE0 + j // 14][j % 14] = v


def get_pool(c):
    return [c.cpu.ram[0xE0 + j // 14][j % 14] for j in range(POOL)]


def set_flags(c, carry, out):
    d = c.cpu.ram[6]
    d[7] = (d[7] & ~3) | int(carry) | (int(out) << 1)


def snapshot(c):
    carry, out = c.flags_cg()
    return tuple(get(c, n) for n in NAMES) + (carry, out)


def run_case(c, op, w, comp, carry, out, vals, i=0, pool=None):
    """Stack, flags, I and the register pool in; compare all of them."""
    pool = pool or [0] * POOL
    c.run(f"BSP HEX 0 WSIZE {' '.join(f'{w:X}')} WSIZE {COMP_KEY[comp]}")
    for n, v in zip(NAMES, vals):
        put(c, n, v)
    put_i(c, i)
    put_pool(c, pool)
    set_flags(c, carry, out)
    model = Model16C(w=w, comp=comp, carry=carry, out=out, i=i,
                     **dict(zip(NAMES, vals)))
    model.mem = dict(enumerate(pool))
    try:
        model.do(op)
        want = (tuple(getattr(model, n) for n in NAMES)
                + (model.carry, model.out, model.i,
                   tuple(model.mem.get(j, 0) for j in range(POOL))))
    except Error16C as e:
        want = (f"Error {e.n}",) + tuple(vals) + (i, tuple(pool))
    c.run(op)
    err = c.error()
    if err:
        got = ((err,) + tuple(get(c, n) for n in NAMES)
               + (get_i(c), tuple(get_pool(c))))
    else:
        got = snapshot(c) + (get_i(c), tuple(get_pool(c)))
    return got == want, want, got


def sequences(c, rng, n):
    """Paths the poked cases miss: a function pressed during digit entry,
    the stack lift after it, LSTX, and functions in a running program.
    -> (passed, failed names)."""
    hx = lambda v: " ".join(f"{v:X}")
    fails, count = [], 0
    for k in range(n):
        w = rng.choice([65, 100, 127, 128])
        comp = rng.choice([0, 1, 2])
        op = rng.choice(["+", "-", "AND", "XOR", "SL", "RLC", "NOT", "#B",
                         "*", "/", "RMD", "DBLX"])
        x, y, z, t = (rng.getrandbits(w) | (1 << rng.randrange(64, w))
                      for _ in range(4))
        if op in ("/", "RMD"):
            x = rng.getrandbits(rng.randrange(1, w)) | 1
        # Y, Z, T poked one level down, then x typed (lifts the stack)
        # and <op> pressed while x is still in digit entry
        c.run(f"BSP HEX 0 WSIZE {hx(w)} WSIZE {COMP_KEY[comp]} CF 4 CF 5")
        for n_, v in (("x", y), ("y", z), ("z", t), ("t", 0), ("lastx", 0)):
            put(c, n_, v)
        c.run(f"{hx(x)} {op}")
        md = Model16C(w=w, comp=comp, x=x, y=y, z=z, t=t)
        md.do(op)
        want = tuple(getattr(md, n) for n in NAMES) + (md.carry, md.out)
        count += 1
        if snapshot(c)[:7] != want:
            fails.append(f"typed {op} w={w} comp={comp}")
        c.run("7")                              # lifts the whole stack
        want2 = (7, md.x, md.y, md.z, md.lastx)
        count += 1
        if tuple(get(c, n) for n in NAMES) != want2:
            fails.append(f"lift after {op} w={w}")
        c.run("RDN LSTX")                       # X = the operand again
        count += 1
        if get(c, "x") != md.lastx:
            fails.append(f"LSTX after {op} w={w}")
    # program: LBL A, op1, op2, op3, RTN, run with GSB A
    for k in range(n):
        w = rng.choice([65, 100, 127, 128])
        comp = rng.choice([0, 1, 2])
        ops = [rng.choice(["+", "-", "OR", "RL", "RRC", "ASR", "CHS",
                           "MASKR", "LJ", "*", "/", "DBLX", "DBLR"])
               for _ in range(3)]
        c.run("BSP HEX 0 WSIZE P/R CLRPRGM LBL A " + " ".join(ops)
              + " RTN P/R")
        c.run(f"{hx(w)} WSIZE {COMP_KEY[comp]} CF 4 CF 5")
        vals = [special(w, rng) for _ in NAMES]
        for n_, v in zip(NAMES, vals):
            put(c, n_, v)
        if "MASKR" in ops:
            put(c, "x", rng.randrange(w))
            vals[0] = get(c, "x")
        md = Model16C(w=w, comp=comp, **dict(zip(NAMES, vals)))
        try:
            for o in ops:
                md.do(o)
            want = tuple(getattr(md, n) for n in NAMES) + (md.carry, md.out)
        except Error16C as e:
            want = f"Error {e.n}"
        c.run("GSB A")
        got = c.error() or snapshot(c)
        count += 1
        if got != want:
            fails.append(f"program {ops} w={w} comp={comp}")
    return count - len(fails), fails


def fmt(s, w):
    d = (w + 3) // 4
    out = []
    for x in s:
        if isinstance(x, tuple):                 # the register pool
            x = "pool:" + "".join(f"{v:x}" for v in x[:3 * d]).rstrip("0")
        out.append(x if isinstance(x, str) else
                   (str(int(x)) if isinstance(x, bool) else f"{x:0{d}x}"))
    return " ".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=40, help="cases per function")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--ops", nargs="*", default=OPS)
    ap.add_argument("--show", type=int, default=3)
    ap.add_argument("--seq", type=int, default=20,
                    help="typed-entry and program sequences (0: none)")
    args = ap.parse_args()
    rng = random.Random(args.seed)
    c = HP16C(plus=[OBJ]).start()
    total = bad = 0
    t0 = time.time()
    for op in args.ops:
        diffs = []
        for _ in range(args.n):
            w = rng.choice([65, 66, 72, 100, 119, 120, 121, 127, 128,
                            rng.randrange(65, 129)])
            comp = rng.choice([0, 1, 2])
            carry, out = rng.random() < 0.5, rng.random() < 0.5
            vals = [special(w, rng) for _ in NAMES]
            if op in COUNT_OPS:
                vals[0] = count_operand(w, comp, rng)
            elif op in DIV_OPS:
                vals = div_operands(op, w, comp, rng, vals)
            i, pool = 0, None
            if op in I_OPS:
                i, pool = i_operands(w, comp, rng)
            ok, want, got = run_case(c, op, w, comp, carry, out, vals,
                                     i, pool)
            total += 1
            if not ok:
                bad += 1
                diffs.append((w, comp, carry, out, vals, want, got))
        print(f"{op:6} {args.n - len(diffs):3}/{args.n} ok")
        for w, comp, carry, out, vals, want, got in diffs[:args.show]:
            print(f"   w={w} comp={comp} C={int(carry)} G={int(out)} "
                  f"x y z t l: {fmt(vals, w)}")
            print(f"     model: {fmt(want, w)}")
            print(f"     plus:  {fmt(got, w)}")
    if set(args.ops) & {"*", "/", "RMD", "DBLX", "DBL/", "DBLR", "SQRT"}:
        cases = [e for e in edge_cases() if e[0] in args.ops]
        fails = []
        for op, w, comp, vals in cases:
            ok, want, got = run_case(c, op, w, comp, False, False, vals)
            if not ok:
                fails.append((op, w, comp, vals, want, got))
        print(f"edges  {len(cases) - len(fails)}/{len(cases)} ok")
        for op, w, comp, vals, want, got in fails[:args.show * 2]:
            print(f"   {op} w={w} comp={comp} x y z t l: {fmt(vals, w)}")
            print(f"     model: {fmt(want, w)}")
            print(f"     plus:  {fmt(got, w)}")
        total += len(cases)
        bad += len(fails)
    if args.seq:
        ok, fails = sequences(c, rng, args.seq)
        print(f"sequences {ok}/{ok + len(fails)} ok")
        for f in fails[:args.show * 3]:
            print("   FAIL", f)
        total += ok + len(fails)
        bad += len(fails)
    print(f"total {total - bad}/{total} ok, {time.time() - t0:.0f} s")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())

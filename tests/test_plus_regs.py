"""16C+ stage 5 paths the poked cases of test_plus_funcs.py do not reach:

- STO n / RCL n from the keyboard (registers 0-F and .0-.F), with and
  without a program in memory (the pool shrinks by whole 7-byte blocks),
  up to the Error 3 limit;
- DSZ / ISZ in a program (LBL A, DSZ|ISZ, GTO 1, 0, RTN, LBL 1, 1, RTN:
  0 = the next step was skipped), I in max(68, W) bits;
- GSB I to a label, Error 4 for a large |I|;
- CLRREG (I's extension too), WSIZE cutting I to max(68, W) bits.

    python tests/test_plus_regs.py [-n 40] [--seed 1]
"""
import argparse
import pathlib
import random
import sys
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
import tests.test_plus_funcs as T  # noqa: E402
from model.hp16c_model import Model16C, Error16C, mem_display  # noqa: E402

hx = lambda v: " ".join(f"{v:X}")
REG_KEY = lambda r: f"{r:X}" if r < 16 else f". {r - 16:X}"
FAILS = []


def check(name, got, want):
    if got != want:
        FAILS.append(name)
        if len(FAILS) <= 12:
            print(f"FAIL {name}\n   want {want}\n   got  {got}")


def setup(c, w, comp, steps=0):
    c.run("BSP P/R CLRPRGM " + " ".join(["1"] * steps) + " P/R")
    c.run(f"HEX 0 WSIZE {hx(w)} WSIZE {T.COMP_KEY[comp]} CF 4 CF 5")


def regs(c, rng, n):
    for _ in range(n):
        w = rng.choice([65, 68, 72, 100, 127, 128, rng.randrange(65, 129)])
        comp = rng.choice([0, 1, 2])
        steps = rng.choice([0, 0, 1, 8, 30])
        setup(c, w, comp, steps)
        k = -(-w // 4)
        pool_n = 2 * (203 - 7 * (-(-steps // 7)))
        nregs = pool_n // k
        r = rng.choice([0, 1, nregs - 1, nregs, rng.randrange(min(32, nregs + 2))])
        r = min(r, 31)
        pool = [rng.randrange(16) for _ in range(T.POOL)]
        T.put_pool(c, pool)
        x = rng.getrandbits(w)
        T.put(c, "x", x)
        md = Model16C(w=w, comp=comp, x=x)
        md.MEM_NIBBLES = pool_n
        md.mem = dict(enumerate(pool))
        try:
            md.sto(r)
            want = ("ok", tuple(md.mem[j] for j in range(pool_n)))
        except Error16C as e:
            want = (f"Error {e.n}", tuple(pool[:pool_n]))
        d = c.run(f"STO {REG_KEY(r)}")
        got = (c.error() or "ok", tuple(T.get_pool(c)[:pool_n]))
        check(f"STO {r} w={w} steps={steps} (regs {nregs})", got, want)
        if want[0] != "ok":
            continue
        c.run("BSP 5 ENTER 6")                  # Y = 5, X = 6; RCL lifts
        md.x, md.y = 6, 5
        md.rcl(r)
        c.run(f"RCL {REG_KEY(r)}")
        check(f"RCL {r} w={w}", [T.get(c, n) for n in ("x", "y", "z")],
              [md.x, md.y, md.z])


def dsz_isz(c, rng, n):
    for op in ("DSZ", "ISZ"):
        for _ in range(n):
            w = rng.choice([65, 66, 67, 68, 69, 100, 128])
            comp = rng.choice([0, 1, 2])
            iw = max(68, w)
            mi = (1 << iw) - 1
            i = rng.choice([0, 1, 2, mi, mi - 1, 1 << (iw - 1),
                            (1 << (iw - 1)) - 1, rng.getrandbits(iw)])
            c.run(f"BSP P/R CLRPRGM LBL A {op} GTO 1 0 RTN LBL 1 1 RTN P/R")
            c.run(f"HEX 0 WSIZE {hx(w)} WSIZE {T.COMP_KEY[comp]}")
            T.put_i(c, i)
            md = Model16C(w=w, comp=comp, i=i)
            skip = md.dsz() if op == "DSZ" else md.isz()
            r = c.run("GSB A").split()[0]
            check(f"{op} w={w} comp={comp} I={i:x}", (r == "0", T.get_i(c)),
                  (skip, md.i))


def gsb_i(c, rng, n):
    for _ in range(n):
        w = rng.choice([65, 100, 128])
        comp = rng.choice([0, 1, 2])
        lab = rng.randrange(16)
        c.run(f"BSP P/R CLRPRGM LBL {lab:X} 7 RTN P/R")
        c.run(f"HEX 0 WSIZE {hx(w)} WSIZE {T.COMP_KEY[comp]}")
        big = rng.random() < 0.3
        i = (1 << rng.randrange(13, max(68, w))) if big else lab
        T.put_i(c, i)
        gto = rng.random() < 0.5                # GTO I, then R/S runs it
        d = c.run("1 GTO I" + ("" if big else " R/S") if gto else "1 GSB I")
        got = c.error() or d.split()[0]
        check(f"{'GTO' if gto else 'GSB'} I w={w} I={i:x}", got,
              "Error 4" if big else "7")
        c.run("BSP")


TESTS = ("X=Y", "X#Y", "X>Y", "X<=Y", "X<0", "X>0", "X=0", "X#0", "B?")


def conds(c, rng, n):
    """Conditionals in LBL A, test, GTO 1, 0, RTN, LBL 1, 1, RTN: 1 = true.
    Y and Z afterwards hold the X and Y the test saw (unchanged)."""
    for name in TESTS:
        c.run(f"BSP P/R CLRPRGM LBL A {name} GTO 1 0 RTN LBL 1 1 RTN P/R")
        for _ in range(n):
            w = rng.choice([65, 66, 100, 127, 128, rng.randrange(65, 129)])
            comp = rng.choice([0, 1, 2])
            m = (1 << w) - 1
            pool = [0, m, 1, m - 1, 1 << (w - 1), (1 << (w - 1)) - 1,
                    rng.getrandbits(w), rng.getrandbits(w)]
            x, y = rng.choice(pool), rng.choice(pool)
            if rng.random() < 0.2:
                y = x
            if name == "B?":
                x = rng.choice([0, 1, 63, 64, 65, w - 1, w, rng.randrange(w)])
            c.run(f"HEX 0 WSIZE {hx(w)} WSIZE {T.COMP_KEY[comp]}")
            T.put(c, "x", x)
            T.put(c, "y", y)
            md = Model16C(w=w, comp=comp, x=x, y=y)
            try:
                want = ("1" if md.test(name) else "0", x, y)
            except Error16C as e:
                want = (f"Error {e.n}", x, y)
            d = c.run("GSB A")
            if c.error():
                got = (c.error(), T.get(c, "x"), T.get(c, "y"))
                c.run("BSP")
            else:
                got = (d.split()[0], T.get(c, "y"), T.get(c, "z"))
            check(f"{name} w={w} comp={comp} x={x:x} y={y:x}", got, want)


def mem(c, rng, n):
    """MEM (held f 0): registers of ceil(W/4) nibbles in the free pool."""
    for _ in range(n):
        w = rng.choice([65, 68, 69, 100, 124, 125, 128, rng.randrange(65, 129)])
        steps = rng.choice([0, 1, 6, 7, 8, 14, 15, rng.randrange(60)])
        setup(c, w, 2, steps)
        c.run("f")
        c.cpu.press(c.keys[47])
        c.cpu.run(20000)
        got = c.display()
        c.cpu.release()
        c.cpu.run(10 ** 6)
        check(f"MEM w={w} steps={steps}", got, mem_display(w, steps))


def clrreg_wsize(c, rng):
    for w in (65, 100, 128):
        setup(c, w, 2)
        T.put_pool(c, [rng.randrange(16) for _ in range(T.POOL)])
        T.put_i(c, rng.getrandbits(max(68, w)))
        c.run("CLRREG")
        check(f"CLRREG w={w}", (T.get_i(c), sum(T.get_pool(c))), (0, 0))
    for w0, w1 in ((128, 100), (128, 66), (100, 128), (128, 16), (16, 128),
                   (68, 120)):
        setup(c, w0, 0)
        i = rng.getrandbits(max(68, w0))
        if w0 <= 64:
            i &= (1 << 68) - 1
        T.put_i(c, i)
        c.run(f"{hx(w1)} WSIZE")
        want = i & ((1 << max(68, w1)) - 1)
        if w1 <= 64:                            # plus mode off: 68 bits,
            ram = c.cpu.ram                     # extension cleared
            got = (c.reg_i(), T.from_digits(ram[0x17]), ram[0x16][10],
                   ram[0x16][11])
            want = (want, 0, 0, 0)
        else:
            got = T.get_i(c)
        check(f"WSIZE {w0} -> {w1} I", got, want)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("-n", type=int, default=40)
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()
    rng = random.Random(args.seed)
    t0 = time.time()
    c = T.HP16C(plus=[T.OBJ]).start()
    total = 0
    for f, k in ((regs, args.n), (dsz_isz, args.n), (gsb_i, args.n // 2),
                 (conds, args.n), (mem, args.n)):
        before = len(FAILS)
        f(c, rng, k)
        print(f"{f.__name__}: {len(FAILS) - before} failed")
    clrreg_wsize(c, rng)
    print(f"plus regs: {'all passed' if not FAILS else f'{len(FAILS)} FAILED'}, "
          f"{time.time() - t0:.0f} s")
    return 1 if FAILS else 0


if __name__ == "__main__":
    sys.exit(main())

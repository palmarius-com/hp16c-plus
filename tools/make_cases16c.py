"""Generate the stored HP-16C test cases: tests/cases/*.jsonl.

Every case is a key sequence that works from any starting state (it sets
word size, complement mode, flags, stack and I itself) plus the expected
result. The expectation comes from the reference model (model/) and is
checked on the stock ROM in the emulator before the case is written; a
case where model and ROM disagree is reported and not written.

    python tools/make_cases16c.py [--seed 2026] [--scale 1.0]

Format: see README.md, "Stored test cases".
"""
import argparse
import json
import pathlib
import random
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from nut.hp16c import HP16C, tokens_to_keys  # noqa: E402
from model.hp16c_model import (Model16C, Error16C, display, to_float,  # noqa
                               float_to_m, from_float, mem_display)
from tests.test_model_vs_rom import (COMP_KEY, WSIZES, NON_TERMINATING,  # noqa
                                     operands, special, state)

OUT = ROOT / "tests" / "cases"
COMP_NAME = {0: "unsigned", 1: "1's", 2: "2's"}


def hx(v):
    return " ".join(f"{v:X}")


def base_setup(w, comp, carry=False, out=False):
    keys = "BSP CF 3 HEX 0 WSIZE" + (f" {w:X} WSIZE" if w != 64 else "")
    keys += f" {COMP_KEY[comp]} {'SF' if carry else 'CF'} 4"
    keys += f" {'SF' if out else 'CF'} 5"
    return keys


def observe(c, w):
    """Display plus internal state (masked to w) after the case."""
    d = c.display()
    err = c.error()
    if err:
        return {"display": d, "error": err}
    st = c.stack()
    m = (1 << w) - 1
    carry, out = c.flags_cg()
    dig = (w + 3) // 4
    return {"display": d,
            **{k: f"{st[k] & m:0{dig}x}" for k in ("x", "y", "z", "t", "lastx")},
            "i": f"{c.reg_i():017x}", "c": carry, "g": out}


class Writer:
    def __init__(self, area, seed):
        self.area, self.seed, self.rows, self.bad = area, seed, [], 0

    def add(self, keys, expect, ok, **info):
        if not ok:
            self.bad += 1
            return
        n = len(self.rows) + 1
        self.rows.append({
            "id": f"{self.area}-{n:04d}", "area": self.area, "seed": self.seed,
            **info, "keys": keys.split(), "keycodes": tokens_to_keys(keys),
            "expect": expect})

    def save(self):
        OUT.mkdir(parents=True, exist_ok=True)
        p = OUT / f"{self.area}.jsonl"
        with p.open("w", encoding="utf-8") as f:
            for r in self.rows:
                f.write(json.dumps(r, separators=(",", ":")) + "\n")
        print(f"{self.area:12} {len(self.rows):5} cases"
              + (f", {self.bad} rejected (model != ROM)" if self.bad else ""))
        return len(self.rows)


# ------------------------------------------------------------------ areas
def gen_functions(c, rng, n, wr):
    ops = [o for o in Model16C.OPS if "(I)" not in o] + list(Model16C.COMP_OPS)
    for op in ops:
        for _ in range(n):
            w = rng.choice(WSIZES)
            comp = rng.choice([0, 1, 2])
            carry, out = rng.random() < 0.5, rng.random() < 0.5
            t, z, y, x = operands(op, w, comp, rng)
            i = special(w, rng)
            keys = (base_setup(w, comp, carry, out) + f" {hx(i)} STO I "
                    f"{hx(t)} ENTER {hx(z)} ENTER {hx(y)} ENTER {hx(x)}")
            if op in NON_TERMINATING:
                keys += " ENTER"
                t, z, y = z, y, x
            md = Model16C(w=w, comp=comp, x=x, y=y, z=z, t=t, carry=carry,
                          out=out)
            c.run(keys)
            md.lastx = c.stack()["lastx"] & md.mask    # from the setup
            md.i = c.reg_i()
            c.run(op)
            got = observe(c, w)
            try:
                md.do(op)
                want = dict(zip(("x", "y", "z", "t", "lastx", "c", "g", "i"),
                                state(md)))
                ok = "error" not in got and all(
                    int(got[k], 16) == want[k] for k in
                    ("x", "y", "z", "t", "lastx", "i")) and \
                    (got["c"], got["g"]) == (want["c"], want["g"])
            except Error16C as e:
                ok = got.get("error") == f"Error {e.n}"
            d = (w + 3) // 4
            wr.add(keys + " " + op, got, ok, op=op, w=w,
                   comp=COMP_NAME[comp],
                   inputs={"x": f"{x:0{d}x}", "y": f"{y:0{d}x}",
                           "z": f"{z:0{d}x}", "t": f"{t:0{d}x}",
                           "c": carry, "g": out})


def gen_display(c, rng, n, wr):
    for _ in range(n):
        w = rng.choice(WSIZES)
        comp = rng.choice([0, 1, 2])
        base = rng.choice(["HEX", "DEC", "OCT", "BIN"])
        lead0 = rng.random() < 0.5
        window = rng.choice([0, 0, 1, 2, rng.randrange(8)])
        p = special(w, rng)
        keys = base_setup(w, comp) + f" {'SF' if lead0 else 'CF'} 3"
        keys += f" {hx(p)} ENTER {base}"
        offset = 8 * window
        if window:
            keys += f" WINDOW {window}"
        if rng.random() < 0.4:
            for _ in range(rng.randrange(1, 10)):
                k = rng.choice([">", ">", "<"])
                keys += " " + k
                offset = offset + 1 if k == ">" else max(0, offset - 1)
        got = c.run(keys)
        want = display(p, w, comp, base, lead0, offset=offset)
        wr.add(keys, {"display": got}, got == want, w=w, comp=COMP_NAME[comp],
               base=base)


PROGRAM = "BSP P/R CLRPRGM LBL A {} GTO 1 0 RTN LBL 1 1 RTN P/R"


def gen_cond(c, rng, n, wr):
    for op in Model16C.TESTS:
        for _ in range(n):
            w = rng.choice(WSIZES)
            comp = rng.choice([0, 1, 2])
            y, x = special(w, rng), special(w, rng)
            if rng.random() < 0.3:
                x = y
            if op == "B?":
                x = rng.randrange(min(w, 1 << (w - 1) if comp else 1 << w) or 1)
            keys = (PROGRAM.format(op) + " " + base_setup(w, comp)
                    + f" {hx(y)} ENTER {hx(x)} GSB A")
            got = c.run(keys)
            md = Model16C(w=w, comp=comp, x=x, y=y)
            want = "1" if md.test(op) else "0"
            wr.add(keys, {"display": got, "result": got.split()[0]},
                   got.split()[0] == want, op=op, w=w, comp=COMP_NAME[comp])


def gen_registers(c, rng, n, wr):
    from tests.test_regs_vs_rom import reg_keys
    for _ in range(n):
        w1 = rng.choice(WSIZES)
        comp = rng.choice([0, 1, 2])
        md = Model16C(w=w1, comp=comp)
        # empty program memory: the register space depends on it
        keys = "BSP P/R CLRPRGM P/R " + base_setup(w1, comp) + " CLRREG"
        for _ in range(3):
            r, v = rng.randrange(32), special(w1, rng)
            keys += f" {hx(v)} STO {reg_keys(r)}"
            md.x = v
            try:
                md.sto(r)
            except Error16C:
                keys += " BSP"
        w2 = rng.choice([w1, w1, rng.choice(WSIZES)])
        if w2 != w1:
            keys += " 0 WSIZE" + (f" {w2:X} WSIZE" if w2 != 64 else "")
            md.w = w2
        r = rng.randrange(32)
        keys += f" RCL {reg_keys(r)}"
        c.run(keys)
        got = observe(c, w2)
        try:
            md.rcl(r)
            ok = "error" not in got and int(got["x"], 16) == md.x
        except Error16C as e:
            ok = got.get("error") == f"Error {e.n}"
        wr.add(keys, {k: got[k] for k in ("display", "x", "error") if k in got},
               ok, w=w2, comp=COMP_NAME[comp])


def gen_float(c, rng, n, wr):
    for _ in range(n):
        w = rng.choice([v for v in WSIZES if v >= 8])
        comp = rng.choice([0, 1, 2])
        md = Model16C(w=w, comp=comp)
        y = special(w, rng)
        xv = rng.choice([0, 1, -1, 10, -10, 63, -64, 300, -330,
                         rng.randrange(-340, 340)])
        xv = abs(xv) if comp == 0 else xv
        if not md.in_range(xv):
            xv = 7
        x = md.pattern(xv)
        keys = base_setup(w, comp) + f" {hx(y)} ENTER {hx(x)} FLOAT 9"
        got = c.run(keys)
        m = "".join(f"{d:x}" for d in reversed(c.cpu.m))
        neg, mant, exp, ovf = to_float(y, x, w, comp)
        ok = m == float_to_m(neg, mant, exp) and c.flags_cg()[1] == ovf
        wr.add(keys, {"display_float": got, "m": m, "g": ovf}, ok,
               w=w, comp=COMP_NAME[comp])
        c.run("HEX")


def gen_dsz_isz(c, rng, n, wr):
    for op in ("DSZ", "ISZ"):
        for _ in range(n):
            w = rng.choice(WSIZES)
            comp = rng.choice([0, 1, 2])
            v = rng.choice([0, 1, 2, special(w, rng), (1 << w) - 1]) & ((1 << w) - 1)
            keys = (PROGRAM.format(op) + " " + base_setup(w, comp)
                    + f" {hx(v)} STO I GSB A")
            c.run(keys.rsplit(" GSB A", 1)[0])
            md = Model16C(w=w, comp=comp, i=c.reg_i())
            skip = md.dsz() if op == "DSZ" else md.isz()
            got = c.run("GSB A")
            ok = (got.split()[0] == "0") == skip and c.reg_i() == md.i
            wr.add(keys, {"display": got, "skipped": skip, "i": f"{md.i:017x}"},
                   ok, op=op, w=w, comp=COMP_NAME[comp])


AREAS = [("functions", gen_functions, 20), ("display", gen_display, 300),
         ("conditionals", gen_cond, 20), ("registers", gen_registers, 120),
         ("float", gen_float, 100), ("dsz_isz", gen_dsz_isz, 40)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seed", type=int, default=2026)
    ap.add_argument("--scale", type=float, default=1.0)
    args = ap.parse_args()
    total = 0
    for area, fn, n in AREAS:
        rng = random.Random(f"{args.seed}-{area}")
        c = HP16C().start()
        wr = Writer(area, args.seed)
        fn(c, rng, max(1, int(n * args.scale)), wr)
        total += wr.save()
    print(f"total {total} cases in {OUT}")


if __name__ == "__main__":
    main()

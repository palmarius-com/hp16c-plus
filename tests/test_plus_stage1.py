"""16C+ stage 1 checks: plus mode on/off via WSIZE, extension RAM.

Runs the 16C+ build (build/16cplus.obj, built by tools/build_plus.py) on
the 16C+ machine and checks CTRL, the extension words and the original
state after WSIZE sequences. White-box: reads RAM 0x10-0x17 directly.

    python tests/test_plus_stage1.py
"""
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))
from nut.hp16c import HP16C  # noqa: E402
from nut.routine import from_digits, to_digits  # noqa: E402

OBJ = ROOT / "build" / "16cplus.obj"
FAILS = []


def machine():
    return HP16C(plus=[OBJ]).start()


def ram(c, a):
    return from_digits(c.cpu.ram[a])


def ctrl(c):
    v = ram(c, 0x10)
    return (v >> 8) & 0xF, v & 0xFF         # plus flag, W


def check(name, got, want):
    ok = got == want
    if not ok:
        FAILS.append(name)
    print(f"{'ok  ' if ok else 'FAIL'} {name}: {got!r}" + ("" if ok else f" (want {want!r})"))


def main():
    # A: enter plus mode from W=16
    c = machine()
    c.run("HEX 1 0 WSIZE 2S 1 2 3 ENTER 4 5 6 ENTER 8 0 WSIZE")
    check("A plus on, W=0x80", ctrl(c), (1, 0x80))
    check("A RAM6 word size field = 64 (0, f)", (c.cpu.ram[6][11], c.cpu.ram[6][10]), (0, 0xF))
    st = c.stack()
    check("A stack dropped: X = 456", st["x"] & 0xFFFF, 0x456)
    check("A LSTx = 80", st["lastx"] & 0xFF, 0x80)
    check("A display", c.display(), "      456 h")
    check("A extension zero", [ram(c, a) for a in range(0x11, 0x17)], [0] * 6)

    # B: every integer function runs in plus mode now; a real Error 2:
    # MASKL with a count above W (129 at W = 128)
    check("B MASKL 129 in plus mode", c.run("8 1 MASKL").strip(), "Error 2")
    c.run("BSP")

    # C: change W inside plus mode
    c.run("4 1 WSIZE")
    check("C W=0x41 stays plus", ctrl(c), (1, 0x41))

    # D: back to 64
    c.run("4 0 WSIZE")
    check("D W=64: plus off", ctrl(c), (0, 0))
    check("D stock word size 64", c.wsize(), 64)

    # E: plus -> 16
    c.run("8 0 WSIZE 1 0 WSIZE")
    check("E plus -> W=16", (ctrl(c), c.wsize()), ((0, 0), 16))

    # F: extension words drop with the stack; LSTx.ext <- X.ext
    c = machine()
    c.run("HEX 0 WSIZE 7 F WSIZE")                       # plus on, W=0x7F
    for a, v in [(0x11, 0), (0x12, 0x222), (0x13, 0x333), (0x14, 0x444),
                 (0x15, 0x555), (0x16, 0x0000_0000_0000)]:
        c.cpu.ram[a] = to_digits(v)
    c.cpu.ram[0x16] = to_digits(0x55_44_33_22_00)        # L T Z Y X bytes
    c.run("7 0 WSIZE")                                    # X.ext is 0: valid
    check("F W=0x70", ctrl(c), (1, 0x70))
    # typing "7 0" lifts the whole stack (stage 1c), then WSIZE drops it:
    # X 0, Y 222, Z 333, T 333 (Z lifted into T), LSTx = the typed 0x70
    check("F ext after lift + drop (X,Y,Z,T,L)",
          [hex(ram(c, a)) for a in range(0x11, 0x16)],
          [hex(v) for v in (0, 0x222, 0x333, 0x333, 0)])
    # W = 0x70: k = 48 bits, so bits 120-127 are cut to 0 (stage 1b)
    check("F high bytes after drop, cut to W", hex(ram(c, 0x16) & 0xFF_FF_FF_FF_FF),
          hex(0))

    # G: WSIZE 129 -> Error 2
    c = machine()
    check("G WSIZE 129", c.run("HEX 0 WSIZE 8 1 WSIZE").strip(), "Error 2")

    # H: plus mode, X with nonzero extension -> Error 2 (stage 3: |X|)
    c = machine()
    c.run("HEX 0 WSIZE 8 0 WSIZE 5")
    c.cpu.ram[0x11] = to_digits(1)
    check("H X.ext <> 0", c.run("WSIZE").strip(), "Error 2")

    stage1b()
    selftest()
    print("stage 1:", "all passed" if not FAILS else f"{len(FAILS)} FAILED")
    return 1 if FAILS else 0


M128 = (1 << 128) - 1


def full(c, name):
    """128-bit value of a stack register: low 64 bits + extension."""
    reg = {"x": 0x11, "y": 0x12, "z": 0x13, "t": 0x14, "lastx": 0x15}[name]
    sh = {"x": 0, "y": 2, "z": 4, "t": 6, "lastx": 8}[name]
    lo = c.reg64(name) & ((1 << 64) - 1)
    hi8 = (ram(c, 0x16) >> (4 * sh)) & 0xFF
    return lo | (ram(c, reg) << 64) | (hi8 << 120)


def poke_ext(c, vals):
    """vals: name -> 64-bit extension (bits 64-127) for x, y, z, t, lastx."""
    r16 = ram(c, 0x16)
    for name, v in vals.items():
        reg = {"x": 0x11, "y": 0x12, "z": 0x13, "t": 0x14, "lastx": 0x15}[name]
        sh = {"x": 0, "y": 2, "z": 4, "t": 6, "lastx": 8}[name]
        c.cpu.ram[reg] = to_digits(v & ((1 << 56) - 1))
        r16 = (r16 & ~(0xFF << (4 * sh))) | (((v >> 56) & 0xFF) << (4 * sh))
    c.cpu.ram[0x16] = to_digits(r16)


def plus_with_stack(x, y, z):
    """Plus mode W=128 with low parts X=x, Y=y, Z=Z, T=z, LSTx=0x80."""
    c = machine()
    hx = lambda v: " ".join(f"{v:X}")
    c.run(f"HEX 0 WSIZE {hx(z)} ENTER {hx(y)} ENTER {hx(x)} ENTER 8 0 WSIZE")
    return c


def stage1b():
    # truncation of the extension to W; LSTx keeps its bits
    c = machine()
    c.run("HEX 0 WSIZE 8 0 WSIZE")
    ones = (1 << 64) - 1
    poke_ext(c, {"x": 0, "y": ones, "z": ones, "t": ones, "lastx": ones})
    c.run("4 8 WSIZE")                                   # W = 72
    # "4 8" lifts X (ext 0) into Y; WSIZE drops it back: X ext 0
    check("1b trunc W=72: Y,Z,T ext 8 bits, L kept",
          [hex(full(c, n) >> 64) for n in ("x", "y", "z", "t", "lastx")],
          [hex(v) for v in (0, 0xFF, 0xFF, 0xFF, 0)])

    # STATUS
    c = machine()
    c.run("HEX 0 WSIZE 8 0 WSIZE 1S SF 1")
    c.run("f")
    c.cpu.press(c.keys[48])
    c.cpu.run(30000)
    d = c.display()
    c.cpu.release()
    c.cpu.run(10 ** 6)
    check("1b STATUS W=128 1's flag 1", d, " 1-128-0010")
    c.run("4 1 WSIZE 2S CF 1 f")
    c.cpu.press(c.keys[48])
    c.cpu.run(30000)
    check("1b STATUS W=65", c.display(), " 2-065-0000")
    c.cpu.release()
    c.cpu.run(10 ** 6)

    # stack keys on full 128-bit values vs a model
    import random
    rng = random.Random(5)
    for key in ["ENTER", "X<>Y", "RDN", "RUP", "CLX", "LSTX", "ENTER LSTX"]:
        lo = [rng.getrandbits(64) for _ in range(3)]
        ext = {n: rng.getrandbits(64) for n in ("x", "y", "z", "t", "lastx")}
        c = plus_with_stack(*lo)
        poke_ext(c, ext)
        st = {n: full(c, n) for n in ("x", "y", "z", "t", "lastx")}
        x, y, z, t, l = (st[n] for n in ("x", "y", "z", "t", "lastx"))
        lift = True                                       # after WSIZE
        for k in key.split():
            if k == "ENTER":
                x, y, z, t = x, x, y, z
                lift = False
            elif k == "X<>Y":
                x, y = y, x
            elif k == "RDN":
                x, y, z, t = y, z, t, x
            elif k == "RUP":
                x, y, z, t = t, x, y, z
            elif k == "CLX":
                x = 0
            elif k == "LSTX":
                if lift:
                    x, y, z, t = x, x, y, z
                x = l
            c.run(k)
        got = [full(c, n) for n in ("x", "y", "z", "t", "lastx")]
        check(f"1b stack key {key}", [hex(v) for v in got],
              [hex(v) for v in (x, y, z, t, l)])

    # memory reset clears the extension RAM
    c = machine()
    c.run("HEX 0 WSIZE 8 0 WSIZE")
    poke_ext(c, {"x": 5, "y": 6})
    c.cpu.ram[7] = to_digits(0)                       # break the signature
    c.power_on()
    c.key(41)
    check("1b memory reset clears 0x10-0x17",
          [ram(c, a) for a in range(0x10, 0x18)], [0] * 8)


def selftest():
    """Both chips sum to 1 mod 1023 (compensation words 0x13FF, 0x37FF),
    and the 16C self-test (ON held, *) passes on the 16C+ machine."""
    c = machine()
    rom = c.cpu.rom
    check("chip sums mod 1023", (sum(rom[0:0x1800]) % 1023,
                                 sum(rom[0x2000:0x3800]) % 1023), (1, 1))
    c.run("1")
    c.key(41)                                     # off
    c.cpu.run(200000)
    c.cpu.press(c.keys[41])
    c.cpu.run(20000)
    c.cpu.press(c.keys[20])                       # * while ON is held
    c.cpu.run(20000)
    c.cpu.release()
    c.cpu.run(3_000_000)
    check("self-test", c.display(), "-8,8,8,8,8,8,8,8,8,8,")


if __name__ == "__main__":
    sys.exit(main())

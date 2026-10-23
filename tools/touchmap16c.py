"""Which ROM instructions touch the 64-bit state?

Runs a broad keystroke workload on the stock 16C and records every
executed instruction that reads or writes RAM 0x00-0x07 (Y, Z, T, LSTx,
display buffers, mode/word size, stack high bytes) or the CPU register M
(X). Prints a summary per kind and per function-routine region.

    python tools/touchmap16c.py [-o build/touchmap16c.txt]
"""
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
from nut.hp16c import HP16C  # noqa: E402
from nut import disasm  # noqa: E402
from tools.trace16c import FUNCS  # noqa: E402

WORKLOAD = [
    "0 WSIZE HEX 1234567 ENTER 89ABCDE",
    *FUNCS,
    "DEC 123 ENTER 45 * OCT BIN HEX",
    "SF 3 BIN CF 3 HEX WINDOW 1 WINDOW 0",
    "1 0 WSIZE 1S 7 CHS 2S 5 CHS UNSGN",
    "8 WSIZE 1 2 3 STO 4 RCL 4 STO . 1 RCL . 1",
    "0 WSIZE 2S 1 2 3 4 ENTER DBLX DBL/ ENTER 7 DBLR",
    "FLOAT 4 HEX",
    "1 2 3 BSP BSP 4 ENTER CLX LSTX X<>Y RDN RUP",
]

KIND = {
    "c=reg": "read", "c=data": "read", "reg=c": "write", "data=c": "write",
}


def reg_of(cpu, op):
    """RAM register an instruction about to execute will access, or None."""
    lo, arg = op & 0x3C, op >> 6
    if op & 3:
        return None
    if lo == 0x38:      # c=reg n / c=data
        return (cpu.ram_addr & ~0xF) | arg if arg else cpu.ram_addr
    if lo == 0x28:      # reg=c n
        return (cpu.ram_addr & ~0xF) | arg
    if op == 0x2F0:     # data=c
        return cpu.ram_addr
    return None


def main():
    out = None
    if "-o" in sys.argv:
        out = pathlib.Path(sys.argv[sys.argv.index("-o") + 1])
    c = HP16C().start()
    rom = c.cpu.rom
    sites = {}   # pc -> set of tags

    def hook(cpu, addr, op):
        if cpu.inst_state != 0:
            return
        tag = None
        r = reg_of(cpu, op)
        if r is not None and r <= 0x07:
            kind = "w" if (op & 0x3C) == 0x28 or op == 0x2F0 else "r"
            tag = f"{kind}{r:x}"
        elif op in (0x158,):
            tag = "wM"
        elif op in (0x198,):
            tag = "rM"
        elif op in (0x1D8,):
            tag = "xM"
        if tag:
            sites.setdefault(addr, set()).add(tag)

    c.cpu.trace = hook
    for script in WORKLOAD:
        c.run(script)
        c.run("BSP")
    c.cpu.trace = None

    by_tag = {}
    for a, tags in sites.items():
        for t in tags:
            by_tag.setdefault(t, []).append(a)
    names = {"r6": "read mode/word size (RAM 6)", "w6": "write mode (RAM 6)",
             "r7": "read stack high bytes (RAM 7)",
             "w7": "write stack high bytes (RAM 7)",
             "rM": "read X (c=m)", "wM": "write X (m=c)",
             "xM": "exchange X (c<>m)"}
    lines = []
    for t in sorted(by_tag):
        addrs = sorted(by_tag[t])
        label = names.get(t, f"{'read' if t[0] == 'r' else 'write'} RAM {t[1]}")
        lines.append(f"{t:3} {label:32} {len(addrs):3} sites: "
                     + " ".join(f"{a:04x}" for a in addrs))
    lines.append(f"total distinct instruction sites: {len(sites)}")
    text = "\n".join(lines) + "\n"
    if out:
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(text)
    print(text)


if __name__ == "__main__":
    main()

"""Find the ROM code each 16C function runs.

For every function: set up a fresh machine, enter operands, then record
the executed addresses (in order) while the function key runs. Code that
only this function runs, compared with all others, is its private code;
the first private address executed is the entry candidate.

    python tools/trace16c.py [-o build/trace16c.txt]
"""
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
from nut.hp16c import HP16C, tokens_to_keys  # noqa: E402

SETUP = "0 WSIZE HEX 1234567 ENTER 89ABCDE"   # Y, X for binary functions
FUNCS = [
    "+", "-", "*", "/", "RMD", "DBLX", "DBL/", "DBLR", "CHS", "ABS",
    "SQRT", "NOT", "AND", "OR", "XOR", "SL", "SR", "ASR", "RL", "RR",
    "RLC", "RRC", "RLN", "RRN", "RLCN", "RRCN", "LJ", "MASKL", "MASKR",
    "#B", "SB", "CB", "B?", "1S", "2S", "UNSGN", "WSIZE", "X<>Y", "RDN",
    "RUP", "ENTER", "LSTX", "CLX", "X<>I", "I", "(I)", "X<>(I)",
    "STO 1", "RCL 1", "HEX", "DEC", "OCT", "BIN", "SHOWDEC", "FLOAT 2",
    "X=Y", "X>Y", "X<0", "SF 3", "CF 3", "F? 3", "1/X",
]


def trace(func):
    c = HP16C().start()
    c.run(SETUP)
    order = []
    seen = set()

    def hook(cpu, addr, op):
        if addr not in seen:
            seen.add(addr)
            order.append(addr)

    c.cpu.trace = hook
    for code in tokens_to_keys(func):
        c.key(code)
    c.cpu.trace = None
    return order, c.display()


def ranges(addrs):
    out, start, prev = [], None, None
    for a in sorted(addrs):
        if start is None:
            start = prev = a
        elif a == prev + 1:
            prev = a
        else:
            out.append((start, prev))
            start = prev = a
    if start is not None:
        out.append((start, prev))
    return out


def main():
    out = None
    if "-o" in sys.argv:
        out = pathlib.Path(sys.argv[sys.argv.index("-o") + 1])
    data = {f: trace(f) for f in FUNCS}
    counts = {}
    for f, (order, _) in data.items():
        for a in set(order):
            counts[a] = counts.get(a, 0) + 1
    lines = [f"setup: {SETUP}", ""]
    for f, (order, disp) in data.items():
        private = [a for a in order if counts[a] == 1]
        entry = f"{private[0]:04x}" if private else "-"
        rs = ranges(private)
        rtxt = " ".join(f"{a:04x}-{b:04x}" if a != b else f"{a:04x}" for a, b in rs[:12])
        lines.append(f"{f:8} disp={disp!r:16} exec={len(order):5} "
                     f"private={len(private):4} entry={entry}  {rtxt}")
    text = "\n".join(lines) + "\n"
    if out:
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(text)
    print(text)


if __name__ == "__main__":
    main()

"""Call tree of one key on the stock 16C.

Sets up a machine with a setup script, then records every subroutine call
(gosub that is taken), every taken long jump and every return while the
last key of the script runs, and prints them as an indented tree with
our labels (re/16c.sym). Repeated identical lines are folded.

    python tools/calltree16c.py "0 WSIZE HEX 12" "3"      # setup, key
    python tools/calltree16c.py "" "STO 4" --max 400
"""
import argparse
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
from nut.hp16c import HP16C, tokens_to_keys  # noqa: E402
from nut import disasm  # noqa: E402

HERE = pathlib.Path(__file__).resolve().parent.parent


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("setup")
    ap.add_argument("keys")
    ap.add_argument("--max", type=int, default=300, help="max lines")
    ap.add_argument("--jumps", action="store_true", help="also long jumps")
    args = ap.parse_args()
    labels, _, _ = disasm.load_symbols(HERE / "re" / "16c.sym")
    name = lambda a: labels.get(a, f"L{a:04x}")
    c = HP16C().start()
    if args.setup:
        c.run(args.setup)
    events = []
    st = {"prev": None}

    def hook(cpu, addr, op):
        prev = st["prev"]
        st["prev"] = None
        if prev is not None:
            p_addr, kind = prev
            if kind == "call" and addr != p_addr + 2:
                events.append(("call", addr))
            elif kind == "jump" and addr != p_addr + 2:
                events.append(("jump", addr))
            elif kind == "ret" and addr != p_addr + 1:
                events.append(("ret", addr))
        if cpu.inst_state != 0:
            return
        if op & 3 == 1:
            st["prev"] = (addr, "jump" if cpu.rom[addr + 1] & 2 else "call")
        elif op in (0x3E0, 0x360, 0x3A0):
            st["prev"] = (addr, "ret")

    c.cpu.trace = hook
    for code in tokens_to_keys(args.keys):
        c.key(code)
    c.cpu.trace = None
    lines, last, rep = [], None, 0
    depth = 0
    for kind, a in events:
        if kind == "ret":
            depth = max(0, depth - 1)
            continue
        if kind == "jump" and not args.jumps:
            continue
        line = "  " * depth + (name(a) if kind == "call" else f"-> {name(a)}")
        if kind == "call":
            depth += 1
        if line == last:
            rep += 1
            lines[-1] = f"{line}   (x{rep + 1})"
            continue
        lines.append(line)
        last, rep = line, 0
    for line in lines[:args.max]:
        print(line)
    print(f"display: {c.display()!r}")


if __name__ == "__main__":
    main()

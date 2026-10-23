"""Decode the HP-16C function dispatch table (0x0400..) with names.

The dispatcher at 0x0833 turns a function code >= 0xA1 into a table
address 0x0400 + 2*(code - 0xA1), reads two words with cxisa and jumps to
0x0C00 + word1 (return address 0x0089 pushed first). Names come from
tracing which table entry each function key reads.

    python tools/fntable16c.py [--md]
"""
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent))
from nut.hp16c import HP16C, tokens_to_keys  # noqa: E402
from tools.trace16c import FUNCS, SETUP  # noqa: E402

EXTRA = ["SST", "BST", "R/S", "GSB 1", "GTO 1", "RTN", "LBL 1", "PSE",
         "DSZ", "ISZ", "CLRREG", "CLRPRGM", "CLRPREFIX", "MEM", "STATUS",
         "WINDOW 1", "<", ">", "EEX", "X<=Y", "X>0", "X#Y", "X#0", "X=0",
         "BSP", "SHOWHEX", "SHOWOCT", "SHOWBIN", "LSTX", "RUP"]


def table_reads(func):
    """Table addresses read by cxisa while func runs."""
    c = HP16C().start()
    c.run(SETUP)
    reads = []

    def hook(cpu, addr, op):
        if cpu.inst_state == 2 and 0x0400 <= addr < 0x0500:
            reads.append(addr)

    c.cpu.trace = hook
    try:
        for code in tokens_to_keys(func):
            c.key(code)
    finally:
        c.cpu.trace = None
    return reads


def main():
    c = HP16C()
    rom = c.cpu.rom
    names = {}
    for f in FUNCS + EXTRA:
        for a in table_reads(f):
            if a % 2 == 0:
                names.setdefault(a, [])
                if f not in names[a]:
                    names[a].append(f)
    md = "--md" in sys.argv
    if md:
        print("| Code | Table | Word 1 | Word 2 | Routine | Function |")
        print("|---|---|---|---|---|---|")
    for i in range(0x60):
        a = 0x0400 + 2 * i
        w1, w2 = rom[a], rom[a + 1]
        code = 0xA1 + i
        name = ", ".join(names.get(a, []))
        if md:
            print(f"| {code:02X} | {a:04x} | {w1:03x} | {w2:03x} | "
                  f"{0x0C00 + w1:04x} | {name} |")
        else:
            print(f"code {code:02x}  tab {a:04x}  w1 {w1:03x} w2 {w2:03x}"
                  f"  -> {0x0C00 + w1:04x}  {name}")


if __name__ == "__main__":
    main()

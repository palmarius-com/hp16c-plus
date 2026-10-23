"""Nut disassembler (Nonpareil mnemonics, src/dis_nut.c) with symbols.

Listing = ROM words + our symbol file. The symbol file is ours and goes
into git; listings contain HP code and stay out of git (build/).

Symbol file format, one per line:
    1234 LABEL            ; comment
    1234 -                ; comment only
    # full-line comment
"""
import pathlib
import re
import sys

TMAP = [3, 4, 5, 10, 8, 6, 11, -1, 2, 9, 7, 13, 1, 12, 0, -1]

OP00 = {0x000: "nop", 0x040: "wrrom", 0x100: "enbank 1", 0x140: "enbank 3",
        0x180: "enbank 2", 0x1C0: "enbank 4"}
OP18 = {1: "g=c", 2: "c=g", 3: "c<>g", 5: "m=c", 6: "c=m", 7: "c<>m",
        9: "f=sb", 10: "sb=f", 11: "f<>sb", 13: "st=c", 14: "c=st",
        15: "c<>st"}
OP20 = ["pop stk", "powoff", "sel p", "sel q", "? p=q", "lld", "clr abc",
        "goto c", "c=keys", "set hex", "set dec", "disp off", "disp toggle",
        "?c rtn", "?nc rtn", "rtn"]
OP30 = ["disp blink", "n=c", "c=n", "c<>n", "ldi", "stk=c", "c=stk",
        "wptog", "goto keys", "sel ram", "clr regs", "data=c", "cxisa",
        "c=c|a", "c=c&a", "sel pfad"]
ARITH = ["a=0", "b=0", "c=0", "ab ex", "b=a", "ac ex", "c=b", "bc ex",
         "a=c", "a=a+b", "a=a+c", "a=a+1", "a=a-b", "a=a-1", "a=a-c",
         "c=c+c", "c=a+c", "c=c+1", "c=a-c", "c=c-1", "c=-c", "c=-c-1",
         "? b#0", "? c#0", "? a<c", "? a<b", "? a#0", "? a#c", "a sr",
         "b sr", "c sr", "a sl"]
FIELD = ["p", "x", "wp", "w", "pq", "xs", "m", "s"]

# flow kinds
NEXT, JUMP, CJUMP, CALL, CCALL, RET, CRET, STOP, INDIRECT = range(9)


def decode(rom, addr):
    """-> (length, mnemonic, target or None, flow kind)."""
    op = rom[addr]
    t = op & 3
    if t == 3:
        off = (op >> 3) & 0x3F
        if op & 0x200:
            off -= 64
        target = (addr + off) & 0xFFFF
        cond = "?c " if op & 4 else "?nc"
        return 1, f"{cond} goto %s", target, CJUMP
    if t == 1:
        op2 = rom[(addr + 1) & 0xFFFF]
        target = (op >> 2) | ((op2 & 0x3FC) << 6)
        call = not (op2 & 2)
        cond = "?c " if op2 & 1 else "?nc"
        if call:
            return 2, f"{cond} gosub %s", target, CCALL
        return 2, f"{cond} golong %s", target, CJUMP
    if t == 2:
        return 1, f"{ARITH[op >> 5]:<8}{FIELD[(op >> 2) & 7]}", None, NEXT
    lo, arg = op & 0x3C, op >> 6
    if lo in (0x04, 0x08, 0x0C, 0x14, 0x1C, 0x2C, 0x3C) and TMAP[arg] < 0             and op not in (0x3C4, 0x3C8, 0x3CC, 0x3D4, 0x3DC, 0x3FC):
        return 1, f"con ${op:03x}", None, NEXT     # undefined slot
    if lo == 0x00:
        return 1, OP00.get(op, f"con ${op:03x}"), None, NEXT
    if lo == 0x04:
        return 1, "clr s" if op == 0x3C4 else f"s=0 {TMAP[arg]}", None, NEXT
    if lo == 0x08:
        return 1, "clr kb" if op == 0x3C8 else f"s=1 {TMAP[arg]}", None, NEXT
    if lo == 0x0C:
        return 1, "? kb" if op == 0x3CC else f"? s=1 {TMAP[arg]}", None, NEXT
    if lo == 0x10:
        return 1, f"lc {arg:x}", None, NEXT
    if lo == 0x14:
        return 1, "dec pt" if op == 0x3D4 else f"? pt= {TMAP[arg]}", None, NEXT
    if lo == 0x18:
        return 1, OP18.get(arg, f"con ${op:03x}"), None, NEXT
    if lo == 0x1C:
        return 1, "inc pt" if op == 0x3DC else f"pt= {TMAP[arg]}", None, NEXT
    if lo == 0x20:
        m = OP20[arg]
        kind = {"rtn": RET, "?c rtn": CRET, "?nc rtn": CRET,
                "goto c": INDIRECT, "powoff": STOP}.get(m, NEXT)
        return 1, m, None, kind
    if lo == 0x24:
        return 1, f"selpf {arg}", None, NEXT
    if lo == 0x28:
        return 1, f"reg=c {arg}", None, NEXT
    if lo == 0x2C:
        return 1, f"? ext {TMAP[arg]}", None, NEXT
    if lo == 0x30:
        if op == 0x130:
            op2 = rom[(addr + 1) & 0xFFFF]
            return 2, f"ldi ${op2:03x}", None, NEXT
        m = OP30[arg]
        return 1, m, None, INDIRECT if m == "goto keys" else NEXT
    if lo == 0x38:
        return 1, "c=data" if arg == 0 else f"c=reg {arg}", None, NEXT
    if lo == 0x3C:
        return 1, "disp comp" if op == 0x3FC else f"rcr {TMAP[arg]}", None, NEXT
    return 1, f"con ${op:03x}", None, NEXT


def load_rom(paths):
    rom = [0] * 0x10000
    exists = [False] * 0x10000
    for p in paths:
        for line in pathlib.Path(p).read_text().splitlines():
            line = line.split('#')[0].strip()
            if len(line) == 8 and line[4] == ':':
                a = int(line[:4], 16)
                rom[a] = int(line[5:], 16)
                exists[a] = True
    return rom, exists


def load_symbols(path):
    """-> labels, comments, data ranges ("@data lo-hi" inside a comment)."""
    labels, comments = {}, {}
    data = []
    p = pathlib.Path(path)
    if not p.exists():
        return labels, comments, data
    for line in p.read_text(encoding="utf-8").splitlines():
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        m = re.match(r"\s*([0-9a-fA-F]{4})\s+(\S+)?\s*(?:;\s*(.*))?$", line)
        if not m:
            continue
        a = int(m.group(1), 16)
        if m.group(2) and m.group(2) != "-":
            labels[a] = m.group(2)
        if m.group(3):
            comments[a] = m.group(3)
            d = re.search(r"@data ([0-9a-fA-F]{4})-([0-9a-fA-F]{4})", m.group(3))
            if d:
                data.append((int(d.group(1), 16), int(d.group(2), 16)))
    return labels, comments, data


def trace_code(rom, exists, entries):
    """Static flow trace: set of instruction start addresses reached."""
    seen = set()
    work = list(entries)
    calls, jumps = {}, {}
    while work:
        a = work.pop()
        while exists[a] and a not in seen:
            seen.add(a)
            n, _, target, kind = decode(rom, a)
            if target is not None:
                (calls if kind == CCALL else jumps).setdefault(target, set()).add(a)
                if exists[target]:
                    work.append(target)
            if kind in (RET, STOP, INDIRECT):
                break
            if kind == CJUMP and rom[a] & 3 == 1 and not (rom[a + 1] & 1):
                # "?nc golong" right after an instruction that clears carry
                # is unconditional in practice; we still fall through.
                pass
            a = (a + n) & 0xFFFF
    return seen, calls, jumps


def listing(rom, exists, ranges, labels, comments, entries=(0,), data=()):
    seen, calls, jumps = trace_code(rom, exists, entries)
    in_data = lambda x: any(lo <= x <= hi for lo, hi in data)
    out = []
    for lo, hi in ranges:
        a = lo
        while a <= hi:
            if a in labels or a in calls or a in jumps:
                name = labels.get(a, f"L{a:04x}")
                refs = sorted(calls.get(a, set()) | jumps.get(a, set()))
                xr = " ".join(f"{r:04x}" for r in refs[:8])
                more = "..." if len(refs) > 8 else ""
                out.append(f"\n{name}:{'':<6} ; refs {xr}{more}")
            if in_data(a):
                n, text, target = 1, f"dw ${rom[a]:03x}", None
            else:
                n, text, target, kind = decode(rom, a)
            if target is not None:
                text = text % labels.get(target, f"L{target:04x}")
            words = " ".join(f"{rom[a + i]:03x}" for i in range(n))
            mark = " " if a in seen else "?"
            com = comments.get(a, "")
            out.append(f"{a:04x}{mark} {words:<8} {text:<24}"
                       + (f" ; {com}" if com else ""))
            a += n
    return "\n".join(out) + "\n"


def main(argv):
    import argparse
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("rom", nargs="+")
    ap.add_argument("--sym")
    ap.add_argument("--range", action="append", default=[],
                    help="hex range lo-hi, default: all ROM")
    ap.add_argument("--entry", action="append", default=[])
    ap.add_argument("-o", "--out")
    args = ap.parse_args(argv)
    rom, exists = load_rom(args.rom)
    labels, comments, data = (load_symbols(args.sym) if args.sym
                              else ({}, {}, []))
    ranges = [tuple(int(x, 16) for x in r.split("-")) for r in args.range]
    if not ranges:
        used = [a for a in range(0x10000) if exists[a]]
        ranges = [(used[0], used[-1])]
    entries = [int(e, 16) for e in args.entry] or [0]
    entries += list(labels)
    entries = [e for e in entries
               if not any(lo <= e <= hi for lo, hi in data)]
    text = listing(rom, exists, ranges, labels, comments, entries, data)
    if args.out:
        pathlib.Path(args.out).write_text(text, encoding="utf-8")
    else:
        sys.stdout.write(text)


if __name__ == "__main__":
    main(sys.argv[1:])

"""Nut assembler, mnemonics as printed by nut/disasm.py.

Source format, one statement per line:
    label:                  ; label (also "label: instruction")
    org $2000               ; set address
    name equ $123           ; constant
    dw $3ff                 ; raw word
    ?nc goto LABEL          ; short branch, target within -64..+63
    ?nc golong LABEL        ; long jump, 2 words
    ?nc gosub LABEL         ; long call, 2 words
    a=a+b   w               ; arithmetic: op and field
    ldi $123 / lc 7 / rcr 3 / c=reg 6 / pt= 12 / ...
Numbers: $hex, 0x hex or decimal. Comments start with ";".

Output: dict address -> 10-bit word; write_obj() writes "aaaa:www" lines
(the Nonpareil object format, loadable by nut.voyager.Voyager).
"""
import pathlib
import re

from .disasm import TMAP, OP00, OP18, OP20, OP30, ARITH, FIELD

ITMAP = {v: i for i, v in enumerate(TMAP) if v >= 0}   # value -> opcode bits


class AsmError(Exception):
    pass


def _num(tok, symbols=None):
    tok = tok.strip()
    if symbols and tok in symbols:
        return symbols[tok]
    if tok.startswith("$"):
        return int(tok[1:], 16)
    if tok.lower().startswith("0x"):
        return int(tok, 16)
    if re.fullmatch(r"-?\d+", tok):
        return int(tok)
    raise AsmError(f"bad number {tok!r}")


def _build_simple():
    """Mnemonic (normalised) -> opcode for one-word, operand-less ops."""
    t = {}
    for op, m in OP00.items():
        t[m] = op
    for k, m in OP18.items():
        t[m] = 0x018 | (k << 6)
    for k, m in enumerate(OP20):
        t[m] = 0x020 | (k << 6)
    for k, m in enumerate(OP30):
        if m != "ldi":
            t[m] = 0x030 | (k << 6)
    t.update({"clr s": 0x3C4, "clr kb": 0x3C8, "? kb": 0x3CC, "dec pt": 0x3D4,
              "inc pt": 0x3DC, "c=data": 0x038, "disp comp": 0x3FC})
    return {_norm(k): v for k, v in t.items()}


def _norm(s):
    return re.sub(r"\s+", " ", s.strip().lower())


SIMPLE = _build_simple()
ARITH_IDX = {_norm(m): i for i, m in enumerate(ARITH)}
FIELD_IDX = {f: i for i, f in enumerate(FIELD)}
PARAM = [  # (regex, function(groups) -> opcode); decimal operands
    (r"selpf (\d+)", lambda n: 0x024 | (n << 6)),
    (r"reg=c (\d+)", lambda n: 0x028 | (n << 6)),
    (r"c=reg (\d+)", lambda n: 0x038 | (n << 6)),
    (r"s=0 (\d+)", lambda n: 0x004 | (ITMAP[n] << 6)),
    (r"s=1 (\d+)", lambda n: 0x008 | (ITMAP[n] << 6)),
    (r"\? s=1 (\d+)", lambda n: 0x00C | (ITMAP[n] << 6)),
    (r"\? pt= (\d+)", lambda n: 0x014 | (ITMAP[n] << 6)),
    (r"pt= (\d+)", lambda n: 0x01C | (ITMAP[n] << 6)),
    (r"\? ext (\d+)", lambda n: 0x02C | (ITMAP[n] << 6)),
    (r"rcr (\d+)", lambda n: 0x03C | (ITMAP[n] << 6)),
]
PARAM_HEX = [
    (r"lc ([0-9a-f])", lambda n: 0x010 | (n << 6)),
    (r"con \$([0-9a-f]{1,3})", lambda n: n),
]


class Assembler:
    def __init__(self):
        self.words = {}
        self.symbols = {}
        self.listing = []

    # -------------------------------------------------------------- parse
    @staticmethod
    def _split(line):
        line = line.split(";", 1)[0].rstrip()
        label = None
        m = re.match(r"\s*([A-Za-z_][\w.]*):\s*(.*)$", line)
        if m:
            label, line = m.group(1), m.group(2)
        return label, line.strip()

    GO = re.compile(r"\?(n?c) go (\S+)$")      # auto short/long branch

    def _size(self, stmt, n=None):
        s = _norm(stmt)
        if not s or s.startswith("org") or " equ " in f" {s} ":
            return 0
        if re.match(r"\?(n?c) (golong|gosub)\b", s) or s.startswith("ldi"):
            return 2
        if self.GO.match(s):
            return 2 if n in self.long_go else 1
        return 1

    def _pass1(self, lines, origin):
        addr = origin
        seen = set()
        for n, line in enumerate(lines, 1):
            label, stmt = self._split(line)
            s = _norm(stmt)
            if re.match(r"^[a-z_][\w.]* equ ", s, re.I):
                name, val = re.split(r"\s+equ\s+", stmt.strip(), flags=re.I)
                self.symbols[name.strip()] = _num(val, self.symbols)
                continue
            if label:
                if label in seen:
                    raise AsmError(f"line {n}: label {label} defined twice")
                seen.add(label)
                self.symbols[label] = addr
            if s.startswith("org "):
                addr = _num(stmt.split(None, 1)[1], self.symbols)
                continue
            addr += self._size(stmt, n)

    def assemble(self, text, origin=0):
        """"?c go L" / "?nc go L" become goto (1 word) when the target is
        in reach, else golong (2 words); sizes are relaxed until stable."""
        lines = text.splitlines()
        self.long_go = set()
        while True:
            self._pass1(lines, origin)
            grow, addr = set(), origin
            for n, line in enumerate(lines, 1):
                _, stmt = self._split(line)
                s = _norm(stmt)
                if s.startswith("org "):
                    addr = _num(stmt.split(None, 1)[1], self.symbols)
                    continue
                m = self.GO.match(s)
                if m and n not in self.long_go:
                    off = self._target(stmt.split()[-1]) - addr
                    if not -64 <= off <= 63:
                        grow.add(n)
                addr += self._size(stmt, n)
            if not grow:
                break
            self.long_go |= grow
        # pass 2: code
        self.words, self.listing = {}, []
        addr = origin
        for n, line in enumerate(lines, 1):
            label, stmt = self._split(line)
            s = _norm(stmt)
            if not s or re.match(r"^[a-z_][\w.]* equ ", s, re.I):
                continue
            if s.startswith("org "):
                addr = _num(stmt.split(None, 1)[1], self.symbols)
                continue
            m = self.GO.match(s)
            if m:
                kind = "golong" if n in self.long_go else "goto"
                stmt = f"?{m.group(1)} {kind} {stmt.split()[-1]}"
            try:
                ws = self._encode(stmt, addr)
            except (AsmError, KeyError, ValueError) as e:
                raise AsmError(f"line {n}: {line.strip()}: {e}") from None
            for i, w in enumerate(ws):
                if addr + i in self.words:
                    raise AsmError(f"line {n}: address {addr + i:04x} used twice")
                self.words[addr + i] = w
            self.listing.append((addr, ws, line.rstrip()))
            addr += len(ws)
        return self.words

    def _target(self, tok):
        return _num(tok, self.symbols)

    def _encode(self, stmt, addr):
        s = _norm(stmt)
        m = re.match(r"\?(n?c) +(goto|golong|gosub) +(\S+)$", s)
        if m:
            cond = 1 if m.group(1) == "c" else 0
            tgt = self._target(stmt.split()[-1])
            if m.group(2) == "goto":
                off = tgt - addr
                if not -64 <= off <= 63:
                    raise AsmError(f"short branch out of range ({off})")
                return [((off & 0x7F) << 3) | (cond << 2) | 3]
            jump = 2 if m.group(2) == "golong" else 0
            return [((tgt & 0xFF) << 2) | 1,
                    (((tgt >> 8) & 0xFF) << 2) | jump | cond]
        if s.startswith("ldi "):
            v = _num(stmt.split(None, 1)[1], self.symbols)
            return [0x130, v & 0x3FF]
        if s.startswith("dw "):
            return [_num(stmt.split(None, 1)[1], self.symbols) & 0x3FF]
        if s in SIMPLE:
            return [SIMPLE[s]]
        m = re.match(r"(\S+(?: \S+)?) +(p|x|wp|w|pq|xs|m|s)$", s)
        if m and m.group(1) in ARITH_IDX:
            return [(ARITH_IDX[m.group(1)] << 5) | (FIELD_IDX[m.group(2)] << 2)
                    | 2]
        for rx, fn in PARAM:
            m = re.fullmatch(rx, s)
            if m:
                return [fn(int(m.group(1)))]
        for rx, fn in PARAM_HEX:
            m = re.fullmatch(rx, s)
            if m:
                return [fn(int(m.group(1), 16))]
        raise AsmError("unknown instruction")


def checksum_word(words, lo, hi, want=1):
    """Value for one free word so that sum(words lo..hi) % 1023 == want."""
    total = sum(words.get(a, 0) for a in range(lo, hi + 1)) % 1023
    return (want - total) % 1023


def write_obj(words, path, header=""):
    lines = [f"# {h}" for h in header.splitlines()] if header else []
    lines += [f"{a:04x}:{w:03x}" for a, w in sorted(words.items())]
    pathlib.Path(path).write_text("\n".join(lines) + "\n")


def assemble_file(path, origin=0):
    a = Assembler()
    a.assemble(pathlib.Path(path).read_text(encoding="utf-8"), origin)
    return a

"""HP-16C integer reference model, any word size.

Python big integers, so it works unchanged for WSIZE > 64: this is the
oracle for the 16C+. For WSIZE <= 64 it is checked against the stock ROM
running in our emulator (tests/test_model_vs_rom.py); every rule here is
either from the handbook or was corrected until the ROM agreed. Rules
found by testing are marked "ROM:".

Values are raw bit patterns (0 <= p < 2**w). Complement mode: 0 unsigned,
1 one's complement, 2 two's complement (same codes as RAM 0x06 digit 12).
"""
import math


class Error16C(Exception):
    """Error n as shown on the display ("Error 0", "Error 2")."""

    def __init__(self, n):
        super().__init__(f"Error {n}")
        self.n = n


class Model16C:
    I_BITS = 68      # stock 16C: I is always 68 bits

    def __init__(self, w=16, comp=2, x=0, y=0, z=0, t=0, lastx=0,
                 carry=False, out=False, i=0):
        self.w, self.comp = w, comp
        self.x, self.y, self.z, self.t, self.lastx = x, y, z, t, lastx
        self.carry, self.out = carry, out
        self.i = i
        self.mem = {}       # storage registers: nibble index -> nibble

    # ------------------------------------------------------------ numbers
    @property
    def mask(self):
        return (1 << self.w) - 1

    @property
    def sign_bit(self):
        return 1 << (self.w - 1)

    def signed(self, p):
        """Bit pattern -> numeric value in the current complement mode."""
        p &= self.mask
        if self.comp == 0 or not p & self.sign_bit:
            return p
        if self.comp == 2:
            return p - (1 << self.w)
        return -(~p & self.mask)            # one's complement (-0 = all ones)

    def pattern(self, v):
        """Numeric value -> bit pattern (low w bits, no range check)."""
        if self.comp == 1 and v < 0:
            return ~(-v) & self.mask
        return v & self.mask

    def in_range(self, v):
        if self.comp == 0:
            return 0 <= v <= self.mask
        if self.comp == 2:
            return -(1 << (self.w - 1)) <= v < (1 << (self.w - 1))
        return -((1 << (self.w - 1)) - 1) <= v < (1 << (self.w - 1))

    def fit(self, v):
        """Out-of-range result: low bits, and for 1's/2's the sign bit of
        the full answer (handbook p. 40, for * and /)."""
        p = self.pattern(v)
        if self.comp and v < 0:
            p |= self.sign_bit
        elif self.comp:
            p &= ~self.sign_bit
        return p

    # ------------------------------------------------------------- stack
    def drop(self, result):
        self.lastx = self.x
        self.x, self.y, self.z = result, self.z, self.t

    def unary(self, result):
        self.lastx = self.x
        self.x = result

    # -------------------------------------------------------- arithmetic
    def add(self):
        x, y, m = self.x, self.y, self.mask
        s = x + y
        carry = s > m
        r = s & m
        if self.comp == 1 and carry:
            r = (r + 1) & m
        v = self.signed(y) + self.signed(x)
        self.carry = carry
        self.out = not self.in_range(v) if self.comp else carry
        self.drop(r)

    def sub(self):
        x, y, m = self.x, self.y, self.mask
        borrow = y < x
        r = (y - x) & m
        if self.comp == 1 and borrow:
            r = (r - 1) & m
        v = self.signed(y) - self.signed(x)
        self.carry = borrow
        self.out = not self.in_range(v) if self.comp else borrow
        self.drop(r)

    def neg(self, p):
        """ROM: the sign of an operand is its sign bit, so in 1's
        complement -0 (all ones) counts as negative."""
        return self.comp != 0 and bool(p & self.sign_bit)

    def mul(self):
        v = self.signed(self.y) * self.signed(self.x)
        self.out = not self.in_range(v)
        neg = self.neg(self.y) != self.neg(self.x)
        self.drop(self._signed_pattern(v, neg) if not self.out
                  else self.fit(v))

    def _divmod(self, a, b):
        if b == 0:
            raise Error16C(0)
        q = abs(a) // abs(b)
        if (a < 0) != (b < 0):
            q = -q
        r = abs(a) % abs(b)
        return q, (-r if a < 0 else r)

    def _signed_pattern(self, v, negative):
        """ROM: 1's complement results are formed from the magnitude and
        complemented when negative, so a zero result can come out as -0."""
        if self.comp == 1 and negative:
            return ~abs(v) & self.mask
        return self.pattern(v)

    def div(self):
        a, b = self.signed(self.y), self.signed(self.x)
        q, r = self._divmod(a, b)
        self.carry = r != 0
        self.out = not self.in_range(q)
        neg = self.neg(self.y) != self.neg(self.x)
        self.drop(self._signed_pattern(q, neg) if self.in_range(q)
                  else self.fit(q))

    def rmd(self):
        # ROM: RMD leaves the carry flag alone.
        a = self.signed(self.y)
        q, r = self._divmod(a, self.signed(self.x))
        self.drop(self._signed_pattern(r, self.neg(self.y)))

    def sqrt(self):
        v = self.signed(self.x)
        if v < 0:
            raise Error16C(0)
        r = math.isqrt(v)
        self.carry = r * r != v
        self.unary(self._signed_pattern(r, self.neg(self.x)))  # sqrt(-0) = -0

    def chs(self):
        v = self.signed(self.x)
        if self.comp == 2 and self.x == self.sign_bit:
            self.out = True
            self.unary(self.x)
            return
        if self.comp == 0:
            self.out = self.x != 0
            self.unary((-self.x) & self.mask)
            return
        self.out = False
        if self.comp == 1:
            self.unary(~self.x & self.mask)     # ROM: plain inversion, 0 -> -0
            return
        self.unary(self.pattern(-v))

    def abs_(self):
        v = self.signed(self.x)
        if self.comp == 2 and self.x == self.sign_bit:
            self.out = True
            self.unary(self.x)
            return
        self.out = False
        self.unary(self.pattern(abs(v)))

    # ----------------------------------------------------------- logical
    def not_(self):
        self.unary(~self.x & self.mask)

    def and_(self):
        self.drop(self.x & self.y)

    def or_(self):
        self.drop(self.x | self.y)

    def xor(self):
        self.drop(self.x ^ self.y)

    # ---------------------------------------------------- shift / rotate
    def sl(self):
        self.carry = bool(self.x & self.sign_bit)
        self.unary((self.x << 1) & self.mask)

    def sr(self):
        self.carry = bool(self.x & 1)
        self.unary(self.x >> 1)

    def asr(self):
        self.carry = bool(self.x & 1)
        r = self.x >> 1
        if self.comp and self.x & self.sign_bit:
            r |= self.sign_bit
        self.unary(r)

    def _rl(self, p, through):
        top = bool(p & self.sign_bit)
        p = (p << 1) & self.mask
        if through:
            p |= int(self.carry)
        else:
            p |= int(top)
        self.carry = top
        return p

    def _rr(self, p, through):
        low = bool(p & 1)
        p >>= 1
        if (self.carry if through else low):
            p |= self.sign_bit
        self.carry = low
        return p

    def rl(self):
        self.unary(self._rl(self.x, False))

    def rr(self):
        self.unary(self._rr(self.x, False))

    def rlc(self):
        self.unary(self._rl(self.x, True))

    def rrc(self):
        self.unary(self._rr(self.x, True))

    def _count(self, limit):
        """|X| as a count; Error 2 above limit."""
        n = abs(self.signed(self.x))
        if n > limit:
            raise Error16C(2)
        return n

    def _rot_n(self, left, through):
        # ROM: through the carry the ring is w+1 bits, so n may be w+1
        n = self._count(self.w + 1 if through else self.w)
        p = self.y
        for _ in range(n):
            p = self._rl(p, through) if left else self._rr(p, through)
        self.drop(p)

    def rln(self):
        self._rot_n(True, False)

    def rrn(self):
        self._rot_n(False, False)

    def rlcn(self):
        self._rot_n(True, True)

    def rrcn(self):
        self._rot_n(False, True)

    def lj(self):
        p, n = self.x, 0
        if p:
            while not p & self.sign_bit:
                p = (p << 1) & self.mask
                n += 1
        self.lastx = self.x
        self.t, self.z, self.y, self.x = self.z, self.y, p, n

    def maskl(self):
        n = self._count(self.w)
        self.unary((self.mask << (self.w - n)) & self.mask if n else 0)

    def maskr(self):
        n = self._count(self.w)
        self.unary((1 << n) - 1)

    def nbits(self):
        self.unary(bin(self.x).count("1"))

    def _bit(self):
        n = self._count(self.w - 1)
        return 1 << n

    def sb(self):
        b = self._bit()
        self.drop(self.y | b)

    def cb(self):
        b = self._bit()
        self.drop(self.y & ~b)

    # ------------------------------------------------------------ double
    def dblmul(self):
        v = self.signed(self.y) * self.signed(self.x)
        m2 = (1 << (2 * self.w)) - 1
        p = v & m2
        if self.comp == 1 and self.neg(self.y) != self.neg(self.x):
            p = ~abs(v) & m2                     # may be a 2w-bit -0
        self.lastx = self.x
        self.out = False
        self.y, self.x = p & self.mask, p >> self.w

    def _dbl_dividend(self):
        """-> (value, negative) of the 2w-bit dividend Y:Z."""
        p = (self.y << self.w) | self.z
        w2 = 2 * self.w
        if self.comp and p >> (w2 - 1):
            return p - (1 << w2) + (1 if self.comp == 1 else 0), True
        return p, False

    # ROM: DBLR fails only if |q| >= 2**64, at any W <= 64 (handbook p. 54:
    # "If the quotient exceeds 64 bits, Error 0"). 16C+: 2**max(64, W)
    # above 64 bits.
    DBLR_QUOTIENT_BITS = 64

    def _dbl(self, want_rem):
        a, neg_a = self._dbl_dividend()
        b = self.signed(self.x)
        q, r = self._divmod(a, b)
        if want_rem:
            if abs(q) >> max(self.DBLR_QUOTIENT_BITS, self.w):
                raise Error16C(0)
            # ROM: DBLR leaves both flags alone
        else:
            if not self.in_range(q):
                raise Error16C(0)
            self.carry = r != 0
            self.out = False
        res = (self._signed_pattern(r, neg_a) if want_rem
               else self._signed_pattern(q, neg_a != self.neg(self.x)))
        self.lastx = self.x
        self.x, self.y, self.z = res, self.t, self.t

    def dbldiv(self):
        self._dbl(False)

    def dblr(self):
        self._dbl(True)

    # ------------------------------------------------------------ modes
    # -------------------------------------------------- storage registers
    MEM_NIBBLES = 406   # stock 16C, empty program: 203 bytes

    def reg_nibbles(self):
        return -(-self.w // 4)

    def _reg_slice(self, n):
        k = self.reg_nibbles()
        if n < 0 or (n + 1) * k > self.MEM_NIBBLES:
            raise Error16C(3)
        return n * k, k

    def sto(self, n):
        """Registers are one nibble array from RAM 0xE0 digit 0 upward;
        register n = nibbles n*k .. n*k+k-1, k = ceil(W/4) (ROM)."""
        start, k = self._reg_slice(n)
        v = self.x
        for j in range(k):
            self.mem[start + j] = (v >> (4 * j)) & 0xF

    def rcl(self, n):
        start, k = self._reg_slice(n)
        v = 0
        for j in range(k):
            v |= self.mem.get(start + j, 0) << (4 * j)
        self.lift(v & self.mask)

    # ---------------------------------------------------- index register
    @property
    def iw(self):
        """Width of I: 68 bits on the 16C; max(68, W) on the 16C+ (design
        memo), so X<>I X<>I is lossless at any word size."""
        return max(self.I_BITS, self.w)

    def extend_i(self, p):
        """X -> I: numerically equal pattern of iw bits (handbook p. 68).
        ROM: sign extension in 1's and 2's complement, zeros in unsigned."""
        if self.neg(p):
            return p | (((1 << self.iw) - 1) ^ self.mask)
        return p

    def lift(self, v):
        self.t, self.z, self.y, self.x = self.z, self.y, self.x, v

    def sto_i(self):
        self.i = self.extend_i(self.x)

    def rcl_i(self):
        """RCL I: truncated to the word size, low bits kept; stack lifts."""
        self.lift(self.i & self.mask)

    def ind_reg(self):
        """(i): register number = |I| taken as a 68-bit number in the
        current complement mode (handbook p. 69 footnote)."""
        i, top = self.i, 1 << (self.iw - 1)
        if self.comp and i & top:
            mi = (1 << self.iw) - 1
            return (mi & -i) if self.comp == 2 else (~i & mi)
        return i

    def rcl_ind(self):
        self.rcl(self.ind_reg())

    def sto_ind(self):
        self.sto(self.ind_reg())

    def x_ex_ind(self):
        n = self.ind_reg()
        start, k = self._reg_slice(n)
        v = 0
        for j in range(k):
            v |= self.mem.get(start + j, 0) << (4 * j)
        self.sto(n)
        self.x = v & self.mask

    def _step_i(self, d):
        """DSZ/ISZ: I +- 1 in 68-bit arithmetic of the current complement
        mode (1's: end-around carry/borrow); returns True if the next
        program step is skipped (result zero; -0 counts as zero). ROM."""
        m68 = (1 << self.iw) - 1
        v = self.i + d
        if self.comp == 1 and v > m68:
            v += 1
        elif self.comp == 1 and v < 0:
            v -= 1
        self.i = v & m68
        return self.i == 0 or (self.comp == 1 and self.i == m68)

    def dsz(self):
        return self._step_i(-1)

    def isz(self):
        return self._step_i(+1)

    def x_ex_i(self):
        self.x, self.i = self.i & self.mask, self.extend_i(self.x)

    MAX_WSIZE = 64   # stock 16C; the 16C+ raises this

    def wsize(self):
        """WSIZE: |X| is the new word size (0 = 64); the stack drops (LSTx =
        X), then X, Y, Z, T are truncated. Growing pads with zeros, so the
        sign bit is not preserved (handbook p. 32; confirmed in the ROM)."""
        n = abs(self.signed(self.x))
        n = 64 if n == 0 else n
        if n > self.MAX_WSIZE:
            raise Error16C(2)
        self.drop(self.y)
        self.w = n
        m = self.mask
        self.x, self.y, self.z, self.t = (v & m for v in
                                          (self.x, self.y, self.z, self.t))
        self.i &= (1 << self.iw) - 1     # 16C+: I keeps max(68, W) bits

    def set_comp(self, comp):
        """1S, 2S, UNSGN: mode only; bits and flags unchanged (ROM)."""
        self.comp = comp

    OPS = {
        "+": "add", "-": "sub", "*": "mul", "/": "div", "RMD": "rmd",
        "SQRT": "sqrt", "CHS": "chs", "ABS": "abs_", "NOT": "not_",
        "AND": "and_", "OR": "or_", "XOR": "xor", "SL": "sl", "SR": "sr",
        "ASR": "asr", "RL": "rl", "RR": "rr", "RLC": "rlc", "RRC": "rrc",
        "RLN": "rln", "RRN": "rrn", "RLCN": "rlcn", "RRCN": "rrcn",
        "LJ": "lj", "MASKL": "maskl", "MASKR": "maskr", "#B": "nbits",
        "SB": "sb", "CB": "cb", "DBLX": "dblmul", "DBL/": "dbldiv",
        "DBLR": "dblr", "WSIZE": "wsize",
        "STO I": "sto_i", "RCL I": "rcl_i", "X<>I": "x_ex_i",
        "RCL (I)": "rcl_ind", "STO (I)": "sto_ind", "X<>(I)": "x_ex_ind",
    }
    COMP_OPS = {"UNSGN": 0, "1S": 1, "2S": 2}

    # ------------------------------------------------------- conditionals
    TESTS = ("X=Y", "X#Y", "X>Y", "X<=Y", "X<0", "X>0", "X=0", "X#0", "B?")

    def test(self, name):
        """Program conditional: True = execute the next step. Compares
        numeric values in the current complement mode (first draft)."""
        x, y = self.signed(self.x), self.signed(self.y)
        if name == "B?":
            return bool(self.y >> self._count(self.w - 1) & 1)
        return {"X=Y": x == y, "X#Y": x != y, "X>Y": x > y, "X<=Y": x <= y,
                "X<0": x < 0, "X>0": x > 0, "X=0": x == 0,
                "X#0": x != 0}[name]

    def do(self, name):
        if name in self.COMP_OPS:
            self.set_comp(self.COMP_OPS[name])
        else:
            getattr(self, self.OPS[name])()


# ------------------------------------------------------------------ display
BASES = {"HEX": (16, "h", 4), "DEC": (10, "d", None), "OCT": (8, "o", 3),
         "BIN": (2, "b", 1)}
DIGITS = "0123456789AbCdEF"          # as the 7-segment LCD shows them


def display(p, w, comp, base, lead0=False, window=0, offset=None, typed=0):
    """X as the integer-mode LCD shows it, in the text form of
    nut.voyager.Voyager.display(): sign position, 8 digit positions,
    blank, base letter; '.' after a position = its decimal point segment.

    Rules (handbook pp. 31-36, confirmed in the ROM): windows of 8 digits
    from the right; '.' left of the base letter = more digits in higher
    windows, right of it = a window above 0 is shown. DEC is signed in 1's
    and 2's complement (-0 shows as "-0"); the minus sign goes right before
    the digits if there is room, else into the sign position, and is shown
    in every window. Flag 3 (lead0) pads HEX/OCT/BIN to the word size.
    offset: digits scrolled with > (each +1) and < (-1, not below 0);
    WINDOW n sets offset 8n. No upper limit seen (still counting at 70).
    typed: during digit entry, the number of digits typed; typed leading
    zeros show (except with flag 3 in HEX/OCT/BIN, which pads to W), but
    they do not count for the '.' left of the base letter.
    """
    if offset is None:
        offset = 8 * window
    radix, letter, bits = BASES[base]
    md = Model16C(w=w, comp=comp)
    p &= md.mask
    neg = base == "DEC" and md.neg(p)
    if base == "DEC":
        v = abs(md.signed(p))
        digits = str(v)
    else:
        v, digits = p, ""
        while v:
            digits = DIGITS[v % radix] + digits
            v //= radix
        digits = digits or "0"
        if lead0:
            digits = digits.rjust(-(-w // bits), "0")
    more_left = len(digits) > offset + 8
    if typed and (base == "DEC" or not lead0):
        digits = digits.rjust(typed, "0")
    lo = len(digits) - offset
    chunk = digits[max(0, lo - 8):max(0, lo)]
    sign = " "
    if neg:
        if len(chunk) < 8:
            chunk = "-" + chunk
        else:
            sign = "-"
    return (sign + chunk.rjust(8) + " " + ("." if more_left else "")
            + letter + ("." if offset else ""))


# ------------------------------------------------------- float conversion
def to_float(y, x, w, comp):
    """FLOAT from integer mode: X <- y * 2**x as a 10-digit decimal float
    (handbook p. 56-57). Returns (negative, mantissa, exponent, overflow);
    mantissa is the 10-digit integer m with value m * 10**(exponent - 9).
    Overflow: 9.999999999E99 with flag 5; values below 1E-99 become 0.

    Known deviation: this rounds the exact
    value once. The ROM computes 2**|x| as a float and multiplies or
    divides with its 13-digit internal arithmetic (0x0D1C, 0x0D5B), which
    can differ in the last digit on near-ties (digits 11-13 = 499/500):
    about 1 in 3 of those, about 0.1% of random values.
    """
    from fractions import Fraction
    md = Model16C(w=w, comp=comp)
    yv, xv = md.signed(y), md.signed(x)
    neg = yv < 0
    if yv == 0 or xv < -2000:                    # |y| < 2^128: below 1E-99
        return False, 0, 0, False
    if xv > 2000:                                # beyond 9.999999999E99
        return neg, 9999999999, 99, True
    v = Fraction(abs(yv)) * (Fraction(2) ** xv)
    e = len(str(v.numerator // v.denominator)) - 1 if v >= 1 else \
        -len(str(v.denominator // v.numerator))
    while v >= Fraction(10) ** (e + 1):
        e += 1
    while v < Fraction(10) ** e:
        e -= 1
    m = v / Fraction(10) ** (e - 9)              # 1e9 <= m < 1e10
    mi = int(m)
    if m - mi >= Fraction(1, 2):                 # round half up (magnitude)
        mi += 1
    if mi == 10 ** 10:
        mi, e = 10 ** 9, e + 1
    if e > 99:
        return neg, 9999999999, 99, True
    if e < -99:
        return False, 0, 0, False
    return neg, mi, e, False


def float_to_m(neg, mant, exp):
    """BCD register image as the ROM keeps it in M (14 digits, digit 13
    first): sign 0/9, 10 mantissa digits, 3-digit exponent (1000 + e if
    e < 0)."""
    return ("9" if neg else "0") + f"{mant:010d}" + f"{exp % 1000:03d}"


def from_float(neg, mant, exp, comp):
    """Leaving float mode: X = v becomes Y = y, X = x with v = y * 2**x and
    2**31 <= |y| < 2**32 (y = 0 for v = 0); word size 56 (handbook p. 59).
    Returns (y_pattern, x_pattern) at w = 56. Unsigned mode: magnitudes.
    First draft: y rounded half up."""
    from fractions import Fraction
    md = Model16C(w=56, comp=comp)
    v = Fraction(mant) * Fraction(10) ** (exp - 9)
    if v == 0:
        return 0, 0
    x = v.numerator.bit_length() - v.denominator.bit_length() - 32
    while v / Fraction(2) ** x >= 2 ** 32:
        x += 1
    while v / Fraction(2) ** x < 2 ** 31:
        x -= 1
    q = v / Fraction(2) ** x
    y = int(q)
    if q - y >= Fraction(1, 2):
        y += 1
    if y == 2 ** 32:
        y, x = 2 ** 31, x + 1
    if comp == 0:
        return y, abs(x)
    return md.pattern(-y if neg else y), md.pattern(x)


def mem_display(w, steps):
    """MEM (f 0) in run mode: "P-n r-rrr". Program steps take whole
    7-byte blocks from the top of the 203-byte pool; n = steps left in
    the last block, r = registers of ceil(W/4) nibbles that fit in the
    rest (ROM)."""
    blocks = -(-steps // 7)
    p = 7 * blocks - steps
    k = -(-w // 4)
    r = (203 - 7 * blocks) * 2 // k
    return f" P-{p}  r-{r:03d}"

"""Nut CPU, cycle level, headless.

Behaviour follows Eric Smith's Nonpareil (src/proc_nut.c, digit_ops.c),
reimplemented in Python for test harness use. Registers are lists of 14
digits, index 0 = least significant digit, like Nonpareil.
"""

W = 14          # digits per register
EXPSIZE = 3     # x field: digits 0..2
STACK_DEPTH = 4
MAX_RAM = 1024

# map from high opcode bits to register index (status bit, pointer value)
TMAP = [3, 4, 5, 10, 8, 6, 11, -1, 2, 9, 7, 13, 1, 12, 0, -1]

# keyboard scanner states
KB_IDLE, KB_PRESSED, KB_RELEASED, KB_WAIT_CHK, KB_WAIT_CYC = range(5)
KBD_RELEASE_DEBOUNCE_CYCLES = 32

# instruction states
INST_NORMAL, INST_LONG_BRANCH, INST_CXISA, INST_LDI, INST_SELPF = range(5)


def _digit_table():
    """t[base10][sub][carry][x][y] -> (digit, carry_out)."""
    t = {}
    for base in (16, 10):
        for sub in (0, 1):
            for cin in (0, 1):
                for x in range(16):
                    for y in range(16):
                        c = cin ^ sub
                        yy = y ^ 0xF if sub else y
                        s = x + yy + c
                        c = 1 if s > 0xF else 0
                        if base == 10:
                            if sub:
                                if not c:
                                    s += 10
                            else:
                                c |= 1 if s > 9 else 0
                                if c:
                                    s += 6
                        c ^= sub
                        t[(base == 10, sub, cin, x, y)] = (s & 0xF, c)
    return t


_ADDSUB = _digit_table()


class NutCPU:
    def __init__(self):
        self.rom = [0] * 0x10000
        self.rom_exists = [False] * 0x10000
        self.ram = [[0] * W for _ in range(MAX_RAM)]
        self.ram_exists = [False] * MAX_RAM
        self.ram_read_fn = {}    # addr -> fn(addr) -> list of digits
        self.ram_write_fn = {}   # addr -> fn(addr, digits)
        self.ext_flag = [0] * 14
        self.unknown_ops = {}    # opcode -> count (ops Nonpareil treats as bad)
        self.on_sleep = []       # callbacks run at powoff
        self.extra_ops = {}      # opcode -> fn(cpu, opcode), e.g. display ops
        self.cycles = 0
        self.trace = None        # callable(cpu, addr, opcode) or None
        self.coverage = None     # set of executed addresses, or None
        self.reset()

    # ----------------------------------------------------------------- setup
    def reset(self):
        self.a = [0] * W
        self.b = [0] * W
        self.c = [0] * W
        self.m = [0] * W
        self.n = [0] * W
        self.g = [0, 0]
        self.p = 0
        self.q = 0
        self.q_sel = False
        self.fo = 0
        self.decimal = False
        self.carry = 1
        self.prev_carry = 0
        self.s = [0] * 14
        self.pc = 0
        self.prev_pc = 0
        self.stack = [0] * STACK_DEPTH
        self.inst_state = INST_NORMAL
        self.first_word = 0
        self.cxisa_addr = 0
        self.ram_addr = 0
        self.pf_addr = 0
        self.awake = True
        self.key_down = False
        self.kb_state = KB_IDLE
        self.kb_counter = 0
        self.key_buf = 0
        self.display_enable = False

    def load_rom_lines(self, lines):
        for line in lines:
            line = line.split('#')[0].strip()
            if len(line) == 8 and line[4] == ':':
                addr = int(line[:4], 16)
                self.rom[addr] = int(line[5:], 16)
                self.rom_exists[addr] = True

    def create_ram(self, base, size):
        for i in range(base, base + size):
            self.ram_exists[i] = True

    # --------------------------------------------------------------- helpers
    def _pt(self):
        return self.q if self.q_sel else self.p

    def _set_pt(self, v):
        if self.q_sel:
            self.q = v
        else:
            self.p = v

    def _field(self, field):
        if field == 0:
            pt = self._pt()
            return pt, pt
        if field == 1:
            return 0, EXPSIZE - 1
        if field == 2:
            return 0, self._pt()
        if field == 3:
            return 0, W - 1
        if field == 4:
            first, last = self.p, self.q
            if first > last:
                last = W - 1
            return first, last
        if field == 5:
            return EXPSIZE - 1, EXPSIZE - 1
        if field == 6:
            return EXPSIZE, W - 2
        return W - 1, W - 1

    def _add(self, dest, s1, s2, first, last):
        dec = self.decimal
        c = self.carry
        for i in range(first, last + 1):
            d, c = _ADDSUB[(dec, 0, c, s1[i], s2[i] if s2 is not None else 0)]
            dest[i] = d
        self.carry = c

    def _sub(self, dest, s1, s2, first, last):
        dec = self.decimal
        c = self.carry
        for i in range(first, last + 1):
            x = s1[i] if s1 is not None else 0
            y = s2[i] if s2 is not None else 0
            d, c = _ADDSUB[(dec, 1, c, x, y)]
            if dest is not None:
                dest[i] = d
        self.carry = c

    def _push(self, a):
        self.stack = [a] + self.stack[:STACK_DEPTH - 1]

    def _pop(self):
        r = self.stack[0]
        self.stack = self.stack[1:] + [0]
        return r

    def _c_addr(self):
        c = self.c
        return (c[6] << 12) | (c[5] << 8) | (c[4] << 4) | c[3]

    # ------------------------------------------------------------- execution
    def cycle(self):
        """One instruction word (Nonpareil nut_execute_cycle)."""
        self._kbd_scanner_cycle()
        if not self.awake:
            return False
        if self.inst_state == INST_CXISA:
            self.prev_pc = self.cxisa_addr
        else:
            self.prev_pc = self.pc
        op = self.rom[self.prev_pc]
        if self.coverage is not None:
            self.coverage.add(self.prev_pc)
        if self.trace:
            self.trace(self, self.prev_pc, op)
        self.prev_carry = self.carry
        self.carry = 0
        st = self.inst_state
        if st == INST_NORMAL:
            self.pc = (self.pc + 1) & 0xFFFF
            self._execute(op)
        elif st == INST_LONG_BRANCH:
            self.pc = (self.pc + 1) & 0xFFFF
            self._long_branch_2(op)
        elif st == INST_CXISA:
            self.c[2], self.c[1], self.c[0] = op >> 8, (op >> 4) & 0xF, op & 0xF
            self.inst_state = INST_NORMAL
        elif st == INST_LDI:
            self.pc = (self.pc + 1) & 0xFFFF
            self.c[2], self.c[1], self.c[0] = op >> 8, (op >> 4) & 0xF, op & 0xF
            self.inst_state = INST_NORMAL
        else:  # selpf: no smart peripherals on Voyagers
            self.pc = (self.pc + 1) & 0xFFFF
            if op & 1:
                self.inst_state = INST_NORMAL
        self.cycles += 1
        return True

    def run(self, max_cycles):
        """Run until asleep or max_cycles; returns cycles used."""
        n = 0
        while self.awake and n < max_cycles:
            self.cycle()
            n += 1
        return n

    def _execute(self, op):
        t = op & 3
        if t == 2:
            self._arith(op)
        elif t == 3:
            off = (op >> 3) & 0x3F
            if op & 0x200:
                off -= 64
            if ((op >> 2) & 1) == self.prev_carry:
                self.pc = (self.pc + off - 1) & 0xFFFF
        elif t == 1:
            self.inst_state = INST_LONG_BRANCH
            self.first_word = op
            self.carry = self.prev_carry
        else:
            self._misc(op)

    def _long_branch_2(self, op):
        self.inst_state = INST_NORMAL
        target = (self.first_word >> 2) | ((op & 0x3FC) << 6)
        if (op & 1) == self.prev_carry:
            if op & 2:
                self.pc = target
            else:
                self._push(self.pc)
                self.pc = target
                if self.rom[self.pc] == 0:
                    self.pc = self._pop()

    def _arith(self, op):
        o = op >> 5
        first, last = self._field((op >> 2) & 7)
        a, b, c = self.a, self.b, self.c
        if o == 0x00:
            a[first:last + 1] = [0] * (last - first + 1)
        elif o == 0x01:
            b[first:last + 1] = [0] * (last - first + 1)
        elif o == 0x02:
            c[first:last + 1] = [0] * (last - first + 1)
        elif o == 0x03:
            a[first:last + 1], b[first:last + 1] = b[first:last + 1], a[first:last + 1]
        elif o == 0x04:
            b[first:last + 1] = a[first:last + 1]
        elif o == 0x05:
            a[first:last + 1], c[first:last + 1] = c[first:last + 1], a[first:last + 1]
        elif o == 0x06:
            c[first:last + 1] = b[first:last + 1]
        elif o == 0x07:
            b[first:last + 1], c[first:last + 1] = c[first:last + 1], b[first:last + 1]
        elif o == 0x08:
            a[first:last + 1] = c[first:last + 1]
        elif o == 0x09:
            self._add(a, a, b, first, last)
        elif o == 0x0A:
            self._add(a, a, c, first, last)
        elif o == 0x0B:
            self.carry = 1
            self._add(a, a, None, first, last)
        elif o == 0x0C:
            self._sub(a, a, b, first, last)
        elif o == 0x0D:
            self.carry = 1
            self._sub(a, a, None, first, last)
        elif o == 0x0E:
            self._sub(a, a, c, first, last)
        elif o == 0x0F:
            self._add(c, c, c, first, last)
        elif o == 0x10:
            self._add(c, a, c, first, last)
        elif o == 0x11:
            self.carry = 1
            self._add(c, c, None, first, last)
        elif o == 0x12:
            self._sub(c, a, c, first, last)
        elif o == 0x13:
            self.carry = 1
            self._sub(c, c, None, first, last)
        elif o == 0x14:
            self._sub(c, None, c, first, last)
        elif o == 0x15:
            self.carry = 1
            self._sub(c, None, c, first, last)
        elif o == 0x16:
            self.carry = 1 if any(b[first:last + 1]) else 0
        elif o == 0x17:
            self.carry = 1 if any(c[first:last + 1]) else 0
        elif o == 0x18:
            self._sub(None, a, c, first, last)
        elif o == 0x19:
            self._sub(None, a, b, first, last)
        elif o == 0x1A:
            self.carry = 1 if any(a[first:last + 1]) else 0
        elif o == 0x1B:
            self.carry = 1 if a[first:last + 1] != c[first:last + 1] else 0
        elif o in (0x1C, 0x1D, 0x1E):
            r = (a, b, c)[o - 0x1C]
            r[first:last + 1] = r[first + 1:last + 1] + [0]
        else:  # 0x1F a sl
            a[first:last + 1] = [0] + a[first:last]

    def _misc(self, op):
        fn = self.extra_ops.get(op)
        if fn:
            fn(self, op)
            return
        lo = op & 0x3C
        arg = op >> 6
        if lo == 0x00:
            if op == 0x000:
                return
            self._unknown(op)  # wrrom, enbank, wr pil: not on Voyagers
        elif lo == 0x04:
            if op == 0x3C4:
                self.s[0:8] = [0] * 8
            elif TMAP[arg] >= 0:
                self.s[TMAP[arg]] = 0
            else:
                self._unknown(op)
        elif lo == 0x08:
            if op == 0x3C8:
                if self.kb_state == KB_RELEASED:
                    self.kb_state = KB_WAIT_CHK
            elif TMAP[arg] >= 0:
                self.s[TMAP[arg]] = 1
            else:
                self._unknown(op)
        elif lo == 0x0C:
            if op == 0x3CC:
                self.carry = 1 if self.kb_state in (KB_PRESSED, KB_RELEASED) else 0
                if self.kb_state == KB_WAIT_CHK:
                    self.kb_state = KB_WAIT_CYC
                    self.kb_counter = KBD_RELEASE_DEBOUNCE_CYCLES
            elif TMAP[arg] >= 0:
                self.carry = self.s[TMAP[arg]]
            else:
                self._unknown(op)
        elif lo == 0x10:  # lc n
            pt = self._pt()
            self.c[pt] = arg
            self._set_pt(pt - 1 if pt > 0 else W - 1)
        elif lo == 0x14:
            if op == 0x3D4:
                pt = self._pt()
                self._set_pt(pt - 1 if pt > 0 else W - 1)
            elif TMAP[arg] >= 0:
                self.carry = 1 if self._pt() == TMAP[arg] else 0
            else:
                self._unknown(op)
        elif lo == 0x18:
            self._op18(op)
        elif lo == 0x1C:
            if op == 0x3DC:
                pt = self._pt() + 1
                self._set_pt(0 if pt >= W else pt)
            elif TMAP[arg] >= 0:
                self._set_pt(TMAP[arg])
            else:
                self._unknown(op)
        elif lo == 0x20:
            self._op20(op)
        elif lo == 0x24:  # selpf
            self.inst_state = INST_SELPF
        elif lo == 0x28:  # reg=c n
            self.ram_addr = (self.ram_addr & ~0x0F) | arg
            self._ram_write(self.ram_addr)
        elif lo == 0x2C:  # ? ext n
            self.carry = self.ext_flag[TMAP[arg]] if TMAP[arg] >= 0 else 0
        elif lo == 0x30:
            self._op30(op)
        elif lo == 0x38:  # c=reg n / c=data
            self.c = [0] * W
            if arg != 0:
                self.ram_addr = (self.ram_addr & ~0x0F) | arg
            self._ram_read(self.ram_addr)
        elif lo == 0x3C:
            if TMAP[arg] >= 0:  # rcr n
                j = TMAP[arg]
                self.c = self.c[j:] + self.c[:j]
            else:
                self._unknown(op)  # 0x3fc display compensation
        else:
            self._unknown(op)

    def _op18(self, op):
        k = op >> 6
        pt = self._pt()
        lo, hi = (pt, pt + 1) if pt < W - 1 else (0, W - 1)
        if k == 0x1:    # g=c
            self.g = [self.c[lo], self.c[hi]]
        elif k == 0x2:  # c=g
            self.c[lo], self.c[hi] = self.g
        elif k == 0x3:  # c<>g
            self.g, (self.c[lo], self.c[hi]) = [self.c[lo], self.c[hi]], self.g
        elif k == 0x5:
            self.m = self.c[:]
        elif k == 0x6:
            self.c = self.m[:]
        elif k == 0x7:
            self.c, self.m = self.m, self.c
        elif k == 0x9:  # f=sb
            self.fo = self._s_bits(0, 8)
        elif k == 0xA:  # sb=f
            self._set_s_bits(0, 8, self.fo)
        elif k == 0xB:  # f<>sb
            t = self._s_bits(0, 8)
            self._set_s_bits(0, 8, self.fo)
            self.fo = t
        elif k == 0xD:  # st=c
            self._set_s_bits(0, 4, self.c[0])
            self._set_s_bits(4, 4, self.c[1])
        elif k == 0xE:  # c=st
            self.c[0] = self._s_bits(0, 4)
            self.c[1] = self._s_bits(4, 4)
        elif k == 0xF:  # c<>st
            t0, t1 = self._s_bits(0, 4), self._s_bits(4, 4)
            self._set_s_bits(0, 4, self.c[0])
            self._set_s_bits(4, 4, self.c[1])
            self.c[0], self.c[1] = t0, t1
        else:
            self._unknown(op)

    def _op20(self, op):
        k = op >> 6
        if k == 0x0:    # pop stk
            self._pop()
        elif k == 0x1:  # powoff
            self._powoff()
        elif k == 0x2:
            self.q_sel = False
        elif k == 0x3:
            self.q_sel = True
        elif k == 0x4:  # ? p=q
            if self.p == self.q:
                self.carry = 1
        elif k == 0x5:  # lld: batteries fine
            self.carry = 0
        elif k == 0x6:  # clr abc
            self.a, self.b, self.c = [0] * W, [0] * W, [0] * W
        elif k == 0x7:  # goto c
            self.pc = self._c_addr()
        elif k == 0x8:  # c=keys
            self.c[4], self.c[3] = self.key_buf >> 4, self.key_buf & 0xF
        elif k == 0x9:
            self.decimal = False
        elif k == 0xA:
            self.decimal = True
        elif k == 0xD:  # ?c rtn
            if self.prev_carry:
                self.pc = self._pop()
        elif k == 0xE:  # ?nc rtn
            if not self.prev_carry:
                self.pc = self._pop()
        elif k == 0xF:  # rtn
            self.pc = self._pop()
        else:
            self._unknown(op)  # 0x2e0 / 0x320 come from the display chip

    def _op30(self, op):
        k = op >> 6
        if k == 0x1:
            self.n = self.c[:]
        elif k == 0x2:
            self.c = self.n[:]
        elif k == 0x3:
            self.c, self.n = self.n, self.c
        elif k == 0x4:  # ldi
            self.inst_state = INST_LDI
        elif k == 0x5:  # stk=c
            self._push(self._c_addr())
        elif k == 0x6:  # c=stk
            a = self._pop()
            self.c[6], self.c[5], self.c[4], self.c[3] = (
                a >> 12, (a >> 8) & 0xF, (a >> 4) & 0xF, a & 0xF)
        elif k == 0x8:  # goto keys
            self.pc = (self.pc & 0xFF00) | self.key_buf
        elif k == 0x9:  # sel ram (c=dadd)
            c = self.c
            self.ram_addr = ((c[2] << 8) | (c[1] << 4) | c[0]) & 0x3FF
        elif k == 0xB:  # data=c
            self._ram_write(self.ram_addr)
        elif k == 0xC:  # cxisa
            self.cxisa_addr = self._c_addr()
            self.inst_state = INST_CXISA
        elif k == 0xD:
            self.c = [x | y for x, y in zip(self.c, self.a)]
        elif k == 0xE:
            self.c = [x & y for x, y in zip(self.c, self.a)]
        elif k == 0xF:  # sel pfad
            self.pf_addr = (self.c[1] << 4) | self.c[0]
        else:
            self._unknown(op)

    def _s_bits(self, first, count):
        r = 0
        for i in range(count):
            if self.s[first + i]:
                r |= 1 << i
        return r

    def _set_s_bits(self, first, count, v):
        for i in range(count):
            self.s[first + i] = (v >> i) & 1

    def _ram_read(self, addr):
        if not self.ram_exists[addr]:
            return
        fn = self.ram_read_fn.get(addr)
        self.c = fn(addr) if fn else self.ram[addr][:]

    def _ram_write(self, addr):
        if not self.ram_exists[addr]:
            return
        fn = self.ram_write_fn.get(addr)
        if fn:
            fn(addr, self.c[:])
        else:
            self.ram[addr] = self.c[:]

    def _unknown(self, op):
        self.unknown_ops[op] = self.unknown_ops.get(op, 0) + 1

    # -------------------------------------------------------------- keyboard
    def _kbd_scanner_cycle(self):
        if self.kb_state == KB_WAIT_CYC:
            self.kb_counter -= 1
            if self.kb_counter == 0:
                if self.key_down:
                    self.kb_state = KB_PRESSED
                    self.awake = True
                else:
                    self.kb_state = KB_IDLE

    def _powoff(self):
        self.awake = False
        self.pc = 0
        # nut keyboard scanner sleep
        if self.kb_state == KB_PRESSED:
            self.awake = True
        if self.kb_state == KB_WAIT_CYC:
            if self.key_down:
                self.kb_state = KB_PRESSED
                self.awake = True
            else:
                self.kb_state = KB_IDLE
        for fn in self.on_sleep:
            fn(self)

    def press(self, hw_keycode):
        # In deep sleep (display off) only ON (0x18) wakes the CPU.
        if not self.awake and not self.display_enable and hw_keycode != 0x18:
            return False
        self.key_buf = hw_keycode
        self.key_down = True
        if self.kb_state == KB_IDLE:
            self.kb_state = KB_PRESSED
        self.awake = True
        return True

    def release(self):
        self.key_down = False
        if self.kb_state == KB_PRESSED:
            self.kb_state = KB_RELEASED

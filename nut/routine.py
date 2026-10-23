"""Call single Nut routines in the emulator, for unit tests.

    r = RoutineRunner(HP16C(plus=[obj]))
    out = r.call(0x2010, a=0x123, c=0x456, ram={0x11: 0x789})
    out.a, out.c, out.ram[0x11], out.carry, out.s

Registers and RAM registers are given and returned as Python ints (14
nibbles, digit 0 = least significant). The routine runs until it returns
to a sentinel return address.
"""
from .cpu import W

SENTINEL = 0xFFFE           # never a real ROM address on the Voyager map


def to_digits(v):
    return [(v >> (4 * i)) & 0xF for i in range(W)]


def from_digits(d):
    v = 0
    for x in reversed(d):
        v = (v << 4) | x
    return v


class Result:
    def __init__(self, cpu, ram_addrs, cycles):
        for n in "abcmn":
            setattr(self, n, from_digits(getattr(cpu, n)))
        self.g = (cpu.g[1] << 4) | cpu.g[0]
        self.p, self.q = cpu.p, cpu.q
        self.carry = cpu.carry
        self.s = list(cpu.s)
        self.ram = {a: from_digits(cpu.ram[a]) for a in ram_addrs}
        self.ram_addr = cpu.ram_addr
        self.cycles = cycles
        self.decimal = cpu.decimal


class RoutineRunner:
    def __init__(self, machine):
        self.m = machine
        self.cpu = machine.cpu

    def call(self, addr, max_cycles=200_000, ram=None, read_ram=(),
             s=None, p=None, q=None, decimal=False, ram_addr=0, **regs):
        cpu = self.cpu
        cpu.awake = True
        cpu.inst_state = 0
        cpu.carry = 0
        cpu.decimal = decimal
        cpu.ram_addr = ram_addr
        for n in "abcmn":
            if n in regs:
                setattr(cpu, n, to_digits(regs[n]))
        if "g" in regs:
            cpu.g = [regs["g"] & 0xF, (regs["g"] >> 4) & 0xF]
        if s is not None:
            cpu.s = [(s >> i) & 1 for i in range(14)] if isinstance(s, int) \
                else list(s)
        if p is not None:
            cpu.p = p
        if q is not None:
            cpu.q = q
        for a, v in (ram or {}).items():
            cpu.ram[a] = to_digits(v)
        cpu.stack = [SENTINEL, 0, 0, 0]
        cpu.pc = addr
        n = 0
        while not (cpu.pc == SENTINEL and cpu.inst_state == 0):
            cpu.cycle()
            n += 1
            if n > max_cycles:
                raise RuntimeError(f"routine {addr:04x} did not return "
                                   f"(pc {cpu.pc:04x})")
        addrs = set(read_ram) | set(ram or {})
        return Result(cpu, sorted(addrs), n)

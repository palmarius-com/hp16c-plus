namespace Nut.Dap;

public enum Flow { Next, Jump, CondJump, Call, CondCall, Return, CondReturn, Stop, Indirect }

/// <summary>One decoded instruction: length in words, text, jump target (if any), flow kind.</summary>
public readonly record struct Decoded(int Length, string Text, int? Target, Flow Kind);

/// <summary>
/// Nut disassembler with Nonpareil mnemonics. Port of nut/disasm.py decode(),
/// which follows Nonpareil's src/dis_nut.c. A word is 10 bits; some instructions take two words.
/// </summary>
public static class NutDisassembler
{
    static readonly int[] TMap = [3, 4, 5, 10, 8, 6, 11, -1, 2, 9, 7, 13, 1, 12, 0, -1];

    static readonly string[] Op20 =
    [
        "pop stk", "powoff", "sel p", "sel q", "? p=q", "lld", "clr abc", "goto c",
        "c=keys", "set hex", "set dec", "disp off", "disp toggle", "?c rtn", "?nc rtn", "rtn",
    ];

    static readonly string[] Op30 =
    [
        "disp blink", "n=c", "c=n", "c<>n", "ldi", "stk=c", "c=stk", "wptog",
        "goto keys", "sel ram", "clr regs", "data=c", "cxisa", "c=c|a", "c=c&a", "sel pfad",
    ];

    static readonly string[] Arith =
    [
        "a=0", "b=0", "c=0", "ab ex", "b=a", "ac ex", "c=b", "bc ex",
        "a=c", "a=a+b", "a=a+c", "a=a+1", "a=a-b", "a=a-1", "a=a-c", "c=c+c",
        "c=a+c", "c=c+1", "c=a-c", "c=c-1", "c=-c", "c=-c-1", "? b#0", "? c#0",
        "? a<c", "? a<b", "? a#0", "? a#c", "a sr", "b sr", "c sr", "a sl",
    ];

    static readonly string[] Field = ["p", "x", "wp", "w", "pq", "xs", "m", "s"];

    static string Op18(int arg) => arg switch
    {
        1 => "g=c", 2 => "c=g", 3 => "c<>g", 5 => "m=c", 6 => "c=m", 7 => "c<>m",
        9 => "f=sb", 10 => "sb=f", 11 => "f<>sb", 13 => "st=c", 14 => "c=st", 15 => "c<>st",
        _ => "",
    };

    static string Op00(int op) => op switch
    {
        0x000 => "nop", 0x040 => "wrrom", 0x100 => "enbank 1", 0x140 => "enbank 3",
        0x180 => "enbank 2", 0x1C0 => "enbank 4", _ => "",
    };

    static Decoded Con(int op) => new(1, $"con ${op:x3}", null, Flow.Next);

    /// <summary>Decode the instruction at <paramref name="addr"/>; the target text is left as a
    /// 4-digit hex address (the caller may replace it with a symbol).</summary>
    public static Decoded Decode(int[] rom, int addr)
    {
        int op = rom[addr & 0xFFFF];
        int t = op & 3;
        if (t == 3)
        {
            int off = (op >> 3) & 0x3F;
            if ((op & 0x200) != 0) off -= 64;
            int target = (addr + off) & 0xFFFF;
            string cond = (op & 4) != 0 ? "?c " : "?nc";
            return new(1, $"{cond} goto {target:X4}", target, Flow.CondJump);
        }
        if (t == 1)
        {
            int op2 = rom[(addr + 1) & 0xFFFF];
            int target = (op >> 2) | ((op2 & 0x3FC) << 6);
            bool call = (op2 & 2) == 0;
            string cond = (op2 & 1) != 0 ? "?c " : "?nc";
            return call
                ? new(2, $"{cond} gosub {target:X4}", target, Flow.CondCall)
                : new(2, $"{cond} golong {target:X4}", target, Flow.CondJump);
        }
        if (t == 2)
            return new(1, $"{Arith[op >> 5],-8}{Field[(op >> 2) & 7]}", null, Flow.Next);

        int lo = op & 0x3C, arg = op >> 6;
        bool undefinedSlot = lo is 0x04 or 0x08 or 0x0C or 0x14 or 0x1C or 0x2C or 0x3C
            && TMap[arg] < 0
            && op is not (0x3C4 or 0x3C8 or 0x3CC or 0x3D4 or 0x3DC or 0x3FC);
        if (undefinedSlot) return Con(op);

        switch (lo)
        {
            case 0x00:
                return Op00(op) is { Length: > 0 } m00 ? new(1, m00, null, Flow.Next) : Con(op);
            case 0x04:
                return new(1, op == 0x3C4 ? "clr s" : $"s=0 {TMap[arg]}", null, Flow.Next);
            case 0x08:
                return new(1, op == 0x3C8 ? "clr kb" : $"s=1 {TMap[arg]}", null, Flow.Next);
            case 0x0C:
                return new(1, op == 0x3CC ? "? kb" : $"? s=1 {TMap[arg]}", null, Flow.Next);
            case 0x10:
                return new(1, $"lc {arg:x}", null, Flow.Next);
            case 0x14:
                return new(1, op == 0x3D4 ? "dec pt" : $"? pt= {TMap[arg]}", null, Flow.Next);
            case 0x18:
                return Op18(arg) is { Length: > 0 } m18 ? new(1, m18, null, Flow.Next) : Con(op);
            case 0x1C:
                return new(1, op == 0x3DC ? "inc pt" : $"pt= {TMap[arg]}", null, Flow.Next);
            case 0x20:
            {
                string m = Op20[arg];
                var kind = m switch
                {
                    "rtn" => Flow.Return,
                    "?c rtn" or "?nc rtn" => Flow.CondReturn,
                    "goto c" => Flow.Indirect,
                    "powoff" => Flow.Stop,
                    _ => Flow.Next,
                };
                return new(1, m, null, kind);
            }
            case 0x24:
                return new(1, $"selpf {arg}", null, Flow.Next);
            case 0x28:
                return new(1, $"reg=c {arg}", null, Flow.Next);
            case 0x2C:
                return new(1, $"? ext {TMap[arg]}", null, Flow.Next);
            case 0x30:
            {
                if (op == 0x130)
                    return new(2, $"ldi ${rom[(addr + 1) & 0xFFFF]:x3}", null, Flow.Next);
                string m = Op30[arg];
                return new(1, m, null, m == "goto keys" ? Flow.Indirect : Flow.Next);
            }
            case 0x38:
                return new(1, arg == 0 ? "c=data" : $"c=reg {arg}", null, Flow.Next);
            case 0x3C:
                return new(1, op == 0x3FC ? "disp comp" : $"rcr {TMap[arg]}", null, Flow.Next);
        }
        return Con(op);
    }

    /// <summary>
    /// Addresses of the <paramref name="count"/> instructions that end right before
    /// <paramref name="addr"/>. Instructions have 1 or 2 words, so going backwards is a guess: start
    /// <paramref name="count"/> words back and decode forward; if that does not land exactly on
    /// <paramref name="addr"/>, start one word earlier, up to 2 x count words back. Without a clean
    /// landing the plain word-by-word guess is returned (a 2-word instruction may be split).
    /// </summary>
    public static List<int> Before(int[] rom, int addr, int count)
    {
        for (int back = count; back <= 2 * count; back++)
        {
            int start = addr - back;
            if (start < 0) break;
            var found = new List<int>();
            int a = start;
            while (a < addr)
            {
                found.Add(a);
                a += Decode(rom, a).Length;
            }
            if (a == addr && found.Count >= count) return found.GetRange(found.Count - count, count);
        }
        var plain = new List<int>();
        for (int a = Math.Max(addr - count, 0); a < addr; a++) plain.Add(a);
        return plain;
    }
}

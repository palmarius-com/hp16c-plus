using Nut.Core;

namespace HpSim;

/// <summary>Key names as printed on the calculator -> keycodes.</summary>
sealed class KeyNames(Skin skin, string modelId)
{
    int? Main(string s, StringComparison how) =>
        skin.Keys.FirstOrDefault(k => string.Equals(k.Main, s, how))?.Code;

    int Shift(char which) => skin.Keys.First(k => k.Main == which.ToString()).Code;

    List<int>? One(string s, StringComparison how)
    {
        if (Main(s, how) is { } code) return [code];
        if (skin.Keys.FirstOrDefault(k => string.Equals(k.F, s, how)) is { } f) return [Shift('f'), f.Code];
        if (skin.Keys.FirstOrDefault(k => string.Equals(k.G, s, how)) is { } g) return [Shift('g'), g.Code];
        return null;
    }

    /// <summary>Codes for one word of a keys line; null = unknown. Digit groups
    /// like 12AB are split; _ stands for a space in a legend. A key may be
    /// written in brackets, as the handbooks print them: [R/S] is R/S.</summary>
    public List<int>? Resolve(string word)
    {
        if (word.Length >= 3 && word[0] == '[' && word[^1] == ']') word = word[1..^1];
        var s = word.Replace('_', ' ');
        foreach (var candidate in new[] { s, Skin.Pretty(s) })
        {
            if (One(candidate, StringComparison.Ordinal) is { } exact) return exact;
            if (One(candidate, StringComparison.OrdinalIgnoreCase) is { } any) return any;
        }
        if (modelId == "16c" && Hp16c.Names.TryGetValue(word, out var named)) return [.. named];
        var all = new List<int>();
        foreach (var ch in s)
        {
            if (Main(ch.ToString(), StringComparison.OrdinalIgnoreCase) is not { } code) return null;
            all.Add(code);
        }
        return all;
    }
}

/// <summary>
/// Runs a test file: one command per line, see docs/TESTS.md. Plays keys through
/// a MachineRunner, the same queue the window uses.
/// </summary>
sealed class TestScript(MachineRunner run, KeyNames names, Skin skin)
{
    public sealed record Result(int Line, string Text, bool Ok, string Detail);

    const int On = 41, FirstKey = 35;
    const long ChordHold = 20000;
    bool _held, _unsettled;
    bool? _speedBefore;     // RealSpeed as the run found it; put back when the run ends

    void Settle(CancellationToken stop)
    {
        var limit = DateTime.UtcNow.AddSeconds(60);
        while (!run.Finished && DateTime.UtcNow < limit && !stop.IsCancellationRequested) Thread.Sleep(1);
    }

    void Tap(int code, CancellationToken stop)
    {
        run.Tap(code);
        Settle(stop);
    }

    void Release(CancellationToken stop)
    {
        if (!_held) return;
        _held = false;
        run.Release();
        Settle(stop);
    }

    /// <summary>Switch on from batteries in, clear Pr Error.</summary>
    public void Begin(CancellationToken stop)
    {
        Tap(On, stop);
        Tap(FirstKey, stop);
    }

    public IEnumerable<Result> Run(IEnumerable<string> lines, CancellationToken stop)
    {
        try
        {
            foreach (var r in RunLines(lines, stop)) yield return r;
        }
        finally
        {
            if (_speedBefore is { } before) run.RealSpeed = before;
        }
    }

    IEnumerable<Result> RunLines(IEnumerable<string> lines, CancellationToken stop)
    {
        int no = 0;
        foreach (var raw in lines)
        {
            no++;
            if (stop.IsCancellationRequested) yield break;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var cut = line.IndexOf("=>", StringComparison.Ordinal);
            var left = (cut < 0 ? line : line[..cut]).Trim();
            var problem = left.Length == 0 ? null : Act(left, stop);
            if (problem is not null) { yield return new(no, line, false, problem); continue; }
            if (cut >= 0) yield return Check(no, line, line[(cut + 2)..].Trim());
            Release(stop);
            if (_unsettled) { _unsettled = false; Settle(stop); }
        }
        Release(stop);
    }

    /// <summary>
    /// Carry out the left side of a line, left to right; returns a problem or null. Actions: key names; `wait SECONDS`
    /// (calculator time, counted in words at the real Voyager speed, the CPU still runs flat out: keys before it are
    /// pressed and the check comes after the wait instead of after the calculator went idle); `chord { K K }`
    /// (the keys go down in the order written, each for a while; a key marked `hold` stays down, the others are
    /// released; the CPU has one key buffer, so the last key written is the one the ROM sees); and, as the first
    /// word, `hold`, which keeps the last key of the line down for the check. `speed real|full` and `reset` are
    /// lines of their own.
    /// </summary>
    string? Act(string left, CancellationToken stop)
    {
        var words = left.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 2 && words[0].Equals("speed", StringComparison.OrdinalIgnoreCase) && words[1] is "real" or "full")
        {
            _speedBefore ??= run.RealSpeed;
            run.RealSpeed = words[1] == "real";
            return null;
        }
        if (words.Length == 1 && words[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            if (run.Read(v => v.DisplayEnable)) Tap(On, stop);
            run.Chord(On, names.Resolve("-")![0], ChordHold, stop);
            Settle(stop);
            Tap(FirstKey, stop);
            return null;
        }
        bool holdLine = words[0].Equals("hold", StringComparison.OrdinalIgnoreCase);
        var codes = new List<int>();
        for (int i = holdLine ? 1 : 0; i < words.Length; i++)
        {
            if (_held) return "a held key must be the last thing on the line";
            var word = words[i];
            if (word.Equals("wait", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= words.Length || !double.TryParse(words[i + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var secs) || secs < 0)
                    return $"bad wait time '{(i + 1 < words.Length ? words[i + 1] : "")}'";
                if (codes.Count > 0)
                {
                    // the keys before the wait: the last one is not settled, the wait comes right after it
                    foreach (var c in codes[..^1]) Tap(c, stop);
                    if (holdLine) { run.Press(codes[^1]); _held = true; }
                    else run.Tap(codes[^1]);
                    codes.Clear();
                }
                run.WaitCycles((long)(secs * MachineRunner.RealWordsPerSecond), stop);
                _unsettled = true;
                i++;
                continue;
            }
            if (word.Equals("chord", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var c in codes) Tap(c, stop);
                codes.Clear();
                if (i + 1 >= words.Length || words[i + 1] != "{") return "chord needs braces: chord { [ON] [-] }";
                int close = Array.IndexOf(words, "}", i + 2);
                if (close < 0) return "chord: the closing } is missing";
                var down = new List<(int Code, bool Hold)>();
                for (int k = i + 2; k < close; k++)
                {
                    bool h = words[k].Equals("hold", StringComparison.OrdinalIgnoreCase);
                    if (h) k++;
                    if (k >= close) return "chord: hold needs a key after it";
                    if (names.Resolve(words[k]) is not { Count: 1 } one) return $"chord: '{words[k]}' is not a single key";
                    down.Add((one[0], h));
                }
                if (down.Count == 0) return "chord: no keys";
                foreach (var (code, _) in down)
                {
                    run.Press(code);
                    run.WaitCycles(ChordHold, stop);
                }
                if (down.Any(d => d.Hold))
                {
                    _held = true;
                    run.WaitCycles(30000, stop);
                }
                else
                {
                    run.Release();
                    run.WaitCycles(3_000_000, stop);
                }
                i = close;
                continue;
            }
            if (names.Resolve(word) is not { } more) return $"unknown key '{word}'";
            codes.AddRange(more);
        }
        if (codes.Count > 0)
        {
            if (holdLine)
            {
                foreach (var c in codes[..^1]) Tap(c, stop);
                run.Press(codes[^1]);
                run.WaitCycles(30000, stop);
                _held = true;
            }
            else foreach (var c in codes) Tap(c, stop);
        }
        else if (holdLine && words.Length == 1) return "no keys";
        return null;
    }

    /// <summary>The right side: display text, then optional [C -G] annunciators.</summary>
    Result Check(int no, string line, string right)
    {
        var flags = "";
        int open = right.LastIndexOf('[');
        if (right.EndsWith(']') && open >= 0)
        {
            flags = right[(open + 1)..^1];
            right = right[..open].Trim();
        }
        // the expected text may stand in ticks: '005-  22 A'. Outer spaces never count:
        // the display's sign cell and its trailing blanks are trimmed on both sides.
        const char Tick = (char)39;
        if (right.Length >= 2 && right[0] == Tick && right[^1] == Tick) right = right[1..^1].Trim();
        var got = run.Read(v => v.Display()).Trim();
        if (got.Length == 0) got = "(blank)";
        var lit = run.Read(v => v.Annunciators()).Select(a => skin.Annunciators[a]).ToHashSet();
        var wrong = flags.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(f => f.StartsWith('-') ? lit.Contains(f[1..]) : !lit.Contains(f)).ToList();
        bool ok = got == right && wrong.Count == 0;
        var detail = (got == right ? "" : $"failed with '{got}'") +
                     (wrong.Count == 0 ? "" : $" annunciators wrong: {string.Join(" ", wrong)}");
        // a failing line shows both texts in ticks, so a stray space is visible
        var text = ok ? line : line[..line.IndexOf("=>", StringComparison.Ordinal)].TrimEnd() +
                   $"  =>  '{right}'" + (flags.Length > 0 ? $"  [{flags}]" : "");
        return new(no, text, ok, detail.Trim());
    }
}

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

    /// <summary>Carry out the left side of a line; returns a problem or null.</summary>
    string? Act(string left, CancellationToken stop)
    {
        // Trailing `wait SECONDS`: calculator time, counted in words at the real Voyager speed
        // (the CPU still runs flat out). Keys are pressed, then the check comes after the wait
        // instead of after the calculator went idle.
        long wait = 0;
        var words = left.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2 && words[^2].Equals("wait", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.TryParse(words[^1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var secs) || secs < 0)
                return $"bad wait time '{words[^1]}'";
            wait = (long)(secs * MachineRunner.RealWordsPerSecond);
            left = string.Join(' ', words[..^2]);
            if (left.Length == 0) { run.WaitCycles(wait, stop); _unsettled = true; return null; }
        }
        var parts = left.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        var first = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1] : "";
        if (first == "reset" && rest.Length == 0)
        {
            if (run.Read(v => v.DisplayEnable)) Tap(On, stop);
            run.Chord(On, names.Resolve("-")![0], ChordHold, stop);
            Settle(stop);
            Tap(FirstKey, stop);
            return null;
        }
        if (first == "chord")
        {
            if (names.Resolve(rest) is not { } chord) return $"unknown key '{rest}'";
            run.Chord(On, chord[^1], ChordHold, stop);
            run.WaitCycles(3_000_000, stop);
            return null;
        }
        bool hold = first == "hold";
        var codes = new List<int>();
        foreach (var word in (hold ? rest : left).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (names.Resolve(word) is not { } c) return $"unknown key '{word}'";
            codes.AddRange(c);
        }
        if (codes.Count == 0) return "no keys";
        bool timed = wait > 0;
        foreach (var c in hold || timed ? codes[..^1] : codes) Tap(c, stop);
        if (hold)
        {
            run.Press(codes[^1]);
            run.WaitCycles(timed ? wait : 30000, stop);
            _held = true;
            _unsettled = timed;
        }
        else if (timed)
        {
            run.Tap(codes[^1]);
            run.WaitCycles(wait, stop);
            _unsettled = true;
        }
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

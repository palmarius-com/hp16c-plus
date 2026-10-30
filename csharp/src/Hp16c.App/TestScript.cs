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
    /// like 12AB are split; _ stands for a space in a legend.</summary>
    public List<int>? Resolve(string word)
    {
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
    bool _held;

    void Settle(CancellationToken stop)
    {
        var limit = DateTime.UtcNow.AddSeconds(60);
        while (!run.Finished && DateTime.UtcNow < limit && !stop.IsCancellationRequested) Thread.Sleep(1);
    }

    void RunCycles(long n, CancellationToken stop)
    {
        var limit = DateTime.UtcNow.AddSeconds(60);
        while (!run.KeysApplied && DateTime.UtcNow < limit) Thread.Sleep(1);
        long end = run.Read(v => v.Cpu.Cycles) + n;
        while (run.Read(v => v.Cpu.Cycles) < end && run.Read(v => v.Cpu.Awake) &&
               DateTime.UtcNow < limit && !stop.IsCancellationRequested) Thread.Sleep(1);
    }

    void Tap(int code, CancellationToken stop)
    {
        run.Press(code);
        run.Release();
        Settle(stop);
    }

    void Release(CancellationToken stop)
    {
        if (!_held) return;
        _held = false;
        run.Release();
        Settle(stop);
    }

    void Chord(int second, CancellationToken stop)
    {
        run.Press(On);
        RunCycles(20000, stop);
        run.Press(second);
        RunCycles(20000, stop);
        run.Release();
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
        }
        Release(stop);
    }

    /// <summary>Carry out the left side of a line; returns a problem or null.</summary>
    string? Act(string left, CancellationToken stop)
    {
        var parts = left.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        var first = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1] : "";
        if (first == "reset" && rest.Length == 0)
        {
            if (run.Read(v => v.DisplayEnable)) Tap(On, stop);
            Chord(names.Resolve("-")![0], stop);
            Settle(stop);
            Tap(FirstKey, stop);
            return null;
        }
        if (first == "chord")
        {
            if (names.Resolve(rest) is not { } chord) return $"unknown key '{rest}'";
            Chord(chord[^1], stop);
            RunCycles(3_000_000, stop);
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
        foreach (var c in hold ? codes[..^1] : codes) Tap(c, stop);
        if (hold)
        {
            run.Press(codes[^1]);
            RunCycles(30000, stop);
            _held = true;
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
        var got = run.Read(v => v.Display()).Trim();
        if (got.Length == 0) got = "(blank)";
        var lit = run.Read(v => v.Annunciators()).Select(a => skin.Annunciators[a]).ToHashSet();
        var wrong = flags.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(f => f.StartsWith('-') ? lit.Contains(f[1..]) : !lit.Contains(f)).ToList();
        bool ok = got == right && wrong.Count == 0;
        var detail = (got == right ? "" : $"got '{got}'") +
                     (wrong.Count == 0 ? "" : $" annunciators wrong: {string.Join(" ", wrong)}");
        return new(no, line, ok, detail.Trim());
    }
}

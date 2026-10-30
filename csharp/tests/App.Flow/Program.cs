// End-to-end run of the simulator's own code without its window: the ROM
// store (real download), the session (patch, chip map, machine) and the
// machine runner (key queue on the CPU thread). Follows the test sheet
// literally, including its memory reset gesture.
//
//   dotnet run --project csharp/tests/App.Flow -c Release -- [--sheet FILE] [--patch FILE]
//
// Needs an empty ROM store for the download part (ROM > Open the ROM folder).
using System.Text;
using HpSim;
using Nut.Core;

string? sheetPath = null, patchPath = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--sheet") sheetPath = args[++i];
    else if (args[i] == "--patch") patchPath = args[++i];
}
var root = FindRoot(Directory.GetCurrentDirectory());
sheetPath ??= Path.Combine(root, "transfer", "16cplus-tests.md");
patchPath ??= RomStore.FindRepoPatch();
var report = new StringBuilder();
void Say(string s) { Console.WriteLine(s); report.AppendLine(s); }
int problems = 0;

Say($"ROM store: {RomStore.Dir}");
var present = ModelCatalog.Models.Where(m => RomStore.Has(m.Id)).Select(m => m.Id).ToList();
Say(present.Count == 0 ? "store empty before the test" : $"NOT empty: {string.Join(" ", present)}");

Say("\n-- download (what the 'Get ROM from Nonpareil' button calls)");
foreach (var m in ModelCatalog.Models)
{
    try
    {
        await RomStore.Download(m);
        var rom = RomStore.Load(m.Id)!;
        Say($"{m.Name}: {rom.Words.Count} words, original={ModelCatalog.Identify(rom)?.Id == m.Id}, from {m.RomUrl}");
    }
    catch (Exception ex) { Say($"{m.Name}: FAILED {ex.Message}"); problems++; }
}

var sheet = Sheet.Load(sheetPath);
Say($"\nsheet: {sheetPath}: {sheet.Sum(s => s.Steps.Count)} steps in {sheet.Count} sections");

var model = ModelCatalog.Get("16c");
var stock = new Session { Model = model, Base = RomStore.Load("16c")!, BaseLabel = "original HP-16C (Nonpareil)" };
Say("\n-- original HP-16C");
Say(stock.Info().TrimEnd());
var stockResult = RunSheet(stock);

Say($"\n-- HP-16C + {Path.GetFileName(patchPath)}");
var plus = new Session { Model = model, Base = RomStore.Load("16c")!, BaseLabel = "original HP-16C (Nonpareil)" };
plus.Patches.Add((Path.GetFileName(patchPath!), RomImage.Load(patchPath!)));
Say(plus.Info().TrimEnd());
if (plus.Info().Contains("checksum off")) problems++;
var plusResult = RunSheet(plus);
problems += plusResult.Failed;

// the exported full ROM must load back as the same machine
var exported = RomImage.Parse(plus.Image().ToNonpareilText("flow test").Split('\n'));
bool same = exported.CanonicalSha256() == plus.Image().CanonicalSha256();
Say($"\nexport and reload of the full ROM: {(same ? "same fingerprint" : "DIFFERENT")}");
if (!same) problems++;

Say($"\nSUMMARY original 16C: {stockResult.Passed}/{stockResult.Passed + stockResult.Failed} steps as on the sheet");
Say($"SUMMARY 16C+:         {plusResult.Passed}/{plusResult.Passed + plusResult.Failed} steps as on the sheet");
Directory.CreateDirectory(Path.Combine(root, "build"));
var outFile = Path.Combine(root, "build", "app-flow-report.txt");
File.WriteAllText(outFile, report.ToString());
Console.WriteLine($"report: {outFile}");
return problems == 0 ? 0 : 1;

(int Passed, int Failed) RunSheet(Session s)
{
    var img = s.Image();
    var (chips, large) = s.ChipMap(img);
    int pass = 0, fail = 0;
    using var run = new MachineRunner(s.CreateMachine(img, chips, large), s.Model.Name);
    var t = new Driver(run);
    foreach (var section in sheet)
    {
        int before = fail;
        t.MemoryReset();
        foreach (var step in section.Steps)
        {
            t.Do(step.Keys);
            var got = t.Lcd();
            var flags = t.Flags();
            t.AfterStep();
            if (got == step.Lcd && flags == step.Flags) { pass++; continue; }
            fail++;
            report.AppendLine($"  {section.Title} step {step.Number}: `{step.Keys}` expected '{step.Lcd}' [{step.Flags}], got '{got}' [{flags}]");
        }
        Say($"{(fail == before ? "ok  " : "FAIL")} {section.Title}: {section.Steps.Count - (fail - before)}/{section.Steps.Count}");
    }
    return (pass, fail);
}

static string FindRoot(string dir)
{
    for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
    {
        if (Directory.Exists(Path.Combine(d.FullName, "transfer"))) return d.FullName;
        var sub = Path.Combine(d.FullName, "HP16Cplus_challenge");
        if (Directory.Exists(Path.Combine(sub, "transfer"))) return sub;
    }
    throw new DirectoryNotFoundException("project root with transfer/ not found; use --sheet");
}

/// <summary>Plays keys the way the window does: press and release through the
/// runner's queue, then waits until the calculator has finished.</summary>
sealed class Driver(MachineRunner run)
{
    const int On = 41, Minus = 30, Times = 20, Bsp = 35;

    void Settle()
    {
        var limit = DateTime.UtcNow.AddSeconds(60);
        while (!run.Finished && DateTime.UtcNow < limit) Thread.Sleep(1);
    }

    void RunCycles(long n)
    {
        var limit = DateTime.UtcNow.AddSeconds(60);
        while (!run.KeysApplied && DateTime.UtcNow < limit) Thread.Sleep(1);
        long end = run.Read(v => v.Cpu.Cycles) + n;
        while (run.Read(v => v.Cpu.Cycles) < end && run.Read(v => v.Cpu.Awake) && DateTime.UtcNow < limit)
            Thread.Sleep(1);
    }

    void Tap(int code)
    {
        run.Press(code);
        run.Release();
        Settle();
    }

    void Chord(int second)
    {
        run.Press(On);
        RunCycles(20000);
        run.Press(second);
        RunCycles(20000);
        run.Release();
    }

    /// <summary>The sheet: off (ON), hold ON, press -, release ON; BSP clears Pr Error.</summary>
    public void MemoryReset()
    {
        if (run.Read(v => v.DisplayEnable)) Tap(On);
        Chord(Minus);
        Settle();
        Tap(Bsp);
    }

    public void Do(string keys)
    {
        if (keys == "ON") { Tap(On); return; }
        if (keys == "ON + *")
        {
            Chord(Times);
            RunCycles(3_000_000);
            return;
        }
        bool hold = keys.EndsWith(" (hold)");
        if (hold) keys = keys[..^7];
        var codes = Hp16c.TokensToKeys(keys.Replace("f·", "").Replace("g·", ""));
        if (!hold)
        {
            foreach (var c in codes) Tap(c);
            return;
        }
        foreach (var c in codes[..^1]) Tap(c);
        run.Press(codes[^1]);
        RunCycles(30000);
        _pendingRelease = true;
    }

    bool _pendingRelease;

    public string Lcd()
    {
        var d = run.Read(v => v.Display()).Trim();
        Finish();
        return d.Length == 0 ? "(blank)" : d;
    }

    public string Flags()
    {
        var (c, g) = run.Read(v => ((Hp16c)v).FlagsCG());
        return string.Join(" ", new[] { c ? "C" : "", g ? "G" : "" }.Where(x => x.Length > 0));
    }

    void Finish()
    {
        if (!_pendingRelease) return;
        _pendingRelease = false;
        _flagsHeld = true;
    }

    bool _flagsHeld;

    public void AfterStep()
    {
        if (!_flagsHeld) return;
        _flagsHeld = false;
        run.Release();
        Settle();
    }
}

sealed record Step(int Number, string Keys, string Lcd, string Flags);
sealed record Section(string Title, List<Step> Steps);

static class Sheet
{
    public static List<Section> Load(string path)
    {
        var sections = new List<Section>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.StartsWith("## ")) { sections.Add(new Section(line[3..], [])); continue; }
            if (!line.StartsWith("| ☐ |")) continue;
            var c = line.Split('|');
            if (!int.TryParse(c[2].Trim(), out var number)) continue; // table header
            sections[^1].Steps.Add(new Step(number, c[3].Trim().Trim('`'),
                                            c[4].Trim().Trim('`'), c[5].Trim()));
        }
        return sections;
    }
}

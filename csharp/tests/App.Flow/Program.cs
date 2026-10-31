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

string? sheetPath = null, patchPath = null, windowExe = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--sheet") sheetPath = args[++i];
    else if (args[i] == "--patch") patchPath = args[++i];
    else if (args[i] == "--window") windowExe = args[++i];
}
var root = FindRoot(Directory.GetCurrentDirectory());
sheetPath ??= Path.Combine(root, "transfer", "16cplus-tests.md");
patchPath ??= Path.Combine(root, "build", "16cplus.patch");
var report = new StringBuilder();
void Say(string s) { Console.WriteLine(s); report.AppendLine(s); }
int problems = 0;
var sheet = Sheet.Load(sheetPath);

if (windowExe is not null)
{
    // the same sheet through the real window, over the --api channel
    var pipeName = "voyager-flow-" + Environment.ProcessId;
    using var app = System.Diagnostics.Process.Start(windowExe, ["--api", pipeName]);
    using var machine = new PipeMachine(pipeName);
    Say("-- real window, download: " + machine.Ask("download 16c"));
    Say("-- real window, original HP-16C: " + machine.Ask("start 16c"));
    var a = RunSheetOn(machine);
    Say("-- real window, patch: " + machine.Ask("patch " + patchPath));
    var b = RunSheetOn(machine);
    // the status panel must show what was typed, all 128 bits
    const string H32 = "123456789ABCDEF0FEDCBA9876543210";
    var check = new Driver(machine);
    check.MemoryReset();
    check.Do("8 0 f·WSIZE");
    check.Do(H32 + " STO I");
    check.Do("F ENTER 1 + " + H32[..31] + " ENTER");
    var panel = machine.Ask("status");
    bool ok128 = panel.Contains("W=128") && panel.Contains("I     " + H32) &&
                 panel.Contains("x     0" + H32[..31]) && panel.Contains("y     0" + H32[..31]);
    Say($"panel 128 bit: {(ok128 ? "shows the typed values" : "WRONG: " + panel)}");
    machine.Ask("panelview 64");
    var low = machine.Ask("status");
    bool ok64 = low.Contains("(raw 64 bits), W=128") && low.Contains("x     " + ("0" + H32[..31])[16..]);
    Say($"panel 64 bit: {(ok64 ? "low 64 bits" : "WRONG: " + low)}");
    machine.Ask("panelview w");
    var byWord = machine.Ask("status");
    machine.Ask("panelview 128");
    // every register on the same line in every view, no line wider than the panel
    static int LineOf(string status, string start) =>
        Array.FindIndex(status.Split('|'), l => l.StartsWith(start));
    var views = new[] { panel, low, byWord };
    bool fixedLines = new[] { "A  ", "N  ", "x     ", "lastx ", "I     ", "recent PCs" }
        .All(k => views.Select(v => LineOf(v, k)).Distinct().Count() == 1);
    var longName = Path.Combine(root, "build", "a-really-long-patch-file-name-to-test-the-status-panel.patch");
    File.Copy(patchPath!, longName, overwrite: true);
    machine.Ask("start 16c");
    machine.Ask("patch " + longName);
    var longPanel = machine.Ask("status");
    var parts = longPanel.Split('|');
    bool fits = parts.All(l => l.TrimEnd().Length <= 42 + 3) && parts[0].TrimEnd().EndsWith("(below)") &&
                longPanel.Contains("notes") && LineOf(longPanel, "x     ") == LineOf(panel, "x     ");
    Say($"panel layout: fixed lines {fixedLines}, long title cut and in the notes {fits}");
    int extraWrong = !ok128 || !ok64 || !fixedLines || !fits ? 1 : 0, extraOk = 1 - extraWrong;

    // the three choices: preset with a hint, the user decides, the choice stays
    bool Check(string what, bool ok)
    {
        Say($"choice: {what}: {(ok ? "ok" : "WRONG")}");
        if (ok) extraOk++; else extraWrong++;
        return ok;
    }
    Check("renamed patch still labelled 16C+ by fingerprint", machine.Ask("label") == "ok 16C+");
    Check("start line names the known build", machine.Ask("message").Contains("Recognised as 16C+"));
    Check("large layout preset with hint", machine.Ask("message").Contains("Large chip layout preset"));
    Check("rominfo says preset", machine.Ask("rominfo").Contains("large (15C chips: ROM 0000-17FF and 2000-37FF), preset"));
    var own = machine.Ask("chips own");
    Check("own chips: says what will not run", own.Contains("ROM words at 2000-37FF are not loaded"));
    Check("own chips: no 16C+ label by itself", machine.Ask("label") == "ok 16C");
    Check("own chips: says the build needs the large layout", machine.Ask("message").Contains("needs the large chip layout"));
    var ownPanel = machine.Ask("status");
    Check("own chips: panel falls back and says why", ownPanel.Contains("(word size)") && ownPanel.Contains("extension RAM"));
    var shots = Path.Combine(root, "csharp", "docs", "screenshots");
    machine.Ask("screenshot " + Path.Combine(shots, "16cplus-own-chips.png"));
    Check("own chips stay after another patch", machine.Ask("patch " + patchPath).StartsWith("ok") &&
          machine.Ask("rominfo").Contains("own, your choice"));
    Check("large chosen", machine.Ask("chips large").Contains("ok") && machine.Ask("rominfo").Contains("large (15C chips: ROM 0000-17FF and 2000-37FF), your choice"));
    Check("back to preset", machine.Ask("chips preset").StartsWith("ok") && machine.Ask("rominfo").Contains(", preset"));
    Check("own label set", machine.Ask("label MY16") == "ok MY16");
    Check("own label stays after a restart of the layout", machine.Ask("chips large").StartsWith("ok") && machine.Ask("label") == "ok MY16");
    Check("label back to automatic", machine.Ask("label auto") == "ok 16C+");
    Check("start 16c: label 16C, own chips", machine.Ask("start 16c").StartsWith("ok") && machine.Ask("label") == "ok 16C" &&
          machine.Ask("rominfo").Contains("own, preset"));
    machine.Ask("start 16c");
    machine.Ask("patch " + patchPath);
    machine.Ask("screenshot " + Path.Combine(shots, "16cplus-large-preset.png"));
    machine.Ask("label MY16");
    machine.Ask("screenshot " + Path.Combine(shots, "16cplus-own-label.png"));
    machine.Ask("label auto");
    // leaving a calculator: changes are listed, a saved calculator keeps them
    machine.Ask("start 16c");
    Check("preset calculator: no changes", machine.Ask("changes") == "ok " && machine.Ask("skin") == "ok automatic");
    machine.Ask("patch " + patchPath);
    var listed = machine.Ask("changes");
    Check("patch is listed", listed.Contains("Patch applied: " + Path.GetFileName(patchPath)));
    Check("preset layout is not a change", !listed.Contains("Chip layout"));
    machine.Ask("chips own");
    machine.Ask("label MINE");
    listed = machine.Ask("changes");
    Check("layout and label are listed", listed.Contains("Chip layout: the calculator's own chips") && listed.Contains("Faceplate label: MINE"));
    var stateFile = Path.Combine(root, "build", "app-flow-state.json");
    machine.Ask("save-state " + stateFile);
    var stateText = File.ReadAllText(stateFile);
    Check("state file carries the branding", stateText.Contains("\"generator\": \"Palmarius Voyager simulator Version "));
    Check("state file says where the ROM is", stateText.Contains("\"romFile\": ") && stateText.Contains("16c.rom"));
    Check("state file shows + as it is", !stateText.Contains("u002B"));
    Check("own state loads again", machine.Ask("load-state " + stateFile) == "ok");
    File.WriteAllText(stateFile, stateText.Replace("\"p\": ", "\"p\": 1"));
    Check("changed state is refused", machine.Ask("load-state " + stateFile).Contains("changed after it was saved"));
    File.WriteAllText(stateFile, stateText.Replace("  ", "   "));
    Check("spaces alone are not a change", machine.Ask("load-state " + stateFile) == "ok");
    File.WriteAllText(stateFile, stateText.Replace("voyager-state 2", "voyager-state 1"));
    Check("old format is refused", machine.Ask("load-state " + stateFile).Contains("no checksum"));
    Check("romFile is relative, no user name", stateText.Contains("\"romFile\": \"data/roms/16c.rom\"") && !stateText.Contains("Users"));
    Check("state file records the chip layout", stateText.Contains("\"chipLayout\": \"own\""));
    File.WriteAllText(stateFile, stateText);
    machine.Ask("chips large");
    Check("other chip layout is refused with a reason", machine.Ask("load-state " + stateFile).Contains("needs the own chip layout"));
    machine.Ask("chips own");
    var chs = Path.Combine(root, "csharp", "docs", "examples", "16c-chs-stack-lift.patch");
    machine.Ask("patch " + chs);
    Check("other ROM: the user is asked", machine.Ask("load-state " + stateFile) == "different-rom");
    Check("load anyway works", machine.Ask("load-state-anyway " + stateFile) == "ok");
    machine.Ask("start 16c");
    Check("original ROM: no patch to export", machine.Ask("export-patch " + Path.Combine(root, "build", "none.patch")).StartsWith("error"));
    machine.Ask("patch " + chs);
    var patchOut = Path.Combine(root, "build", "app-flow-export.patch");
    static string Print(string info) => info[(info.IndexOf("SHA-256): ") + 10)..].Trim();
    var printBefore = Print(machine.Ask("rominfo"));
    Check("export patch", machine.Ask("export-patch " + patchOut) == "ok");
    var exportedWords = RomImage.Load(patchOut);
    Check("exported patch is exactly his ten words", exportedWords.Words.Count == 10 &&
          exportedWords.ToLines().SequenceEqual(RomImage.Load(chs).ToLines()));
    var fullFile = Path.Combine(root, "build", "app-flow-export.rom");
    machine.Ask("export-rom " + fullFile);
    Check("full ROM carries the warning line", File.ReadAllText(fullFile).Contains("not for publication"));
    File.Delete(fullFile);
    machine.Ask("start 16c");
    machine.Ask("patch " + patchOut);
    Check("exported patch gives the same ROM", Print(machine.Ask("rominfo")) == printBefore);
    machine.Ask("start 16c");
    machine.Ask("patch " + patchPath);
    machine.Ask("chips own");
    machine.Ask("label MINE");
    var skinFile = Path.Combine(root, "build", "app-flow-skin.json");
    machine.Ask("skin-save " + skinFile);
    machine.Ask("skin " + skinFile);
    Check("loaded skin is listed", machine.Ask("changes").Contains("Skin: app-flow-skin.json"));
    Check("invalid short name refused", machine.Ask("save-my ../evil").StartsWith("error"));
    var id = machine.Ask("save-my AppFlowTest App Flow Test")[3..];
    var calcDir = Path.Combine(Path.GetDirectoryName(windowExe)!, "data", "custom", id);
    Check("own folder with rom, json and skin", id == "AppFlowTest" &&
          new[] { ".rom", ".json", ".skin" }.All(e => File.Exists(Path.Combine(calcDir, id + e))));
    Check("taken short name refused", machine.Ask("save-my appflowtest Again").Contains("already exists"));
    Check("replacing needs the explicit command", machine.Ask("save-over appflowtest Again") == "ok appflowtest" &&
          machine.Ask("menu").Contains("Again"));
    machine.Ask("delete-my appflowtest");
    id = machine.Ask("save-my AppFlowTest App Flow Test")[3..];
    Check("saved: nothing left to ask", machine.Ask("changes") == "ok ");
    Check("saved calculator is in the Calculator menu", machine.Ask("menu").Contains("App Flow Test"));
    Check("saved calculator keeps layout and label", machine.Ask("start " + id).StartsWith("ok") &&
          machine.Ask("rominfo").Contains("own, your choice") && machine.Ask("label") == "ok MINE");
    Check("saved calculator keeps its skin", machine.Ask("skin") == "ok app-flow-skin.json");
    Check("saved calculator unchanged: no popup", machine.Ask("changes") == "ok ");
    machine.Ask("chips large");
    Check("change on a saved calculator is listed", machine.Ask("changes").Contains("Chip layout: large"));
    Check("test calculator deleted", machine.Ask("start 16c").StartsWith("ok") && machine.Ask("delete-my " + id) == "ok");
    Check("deleted calculator left the menu", !machine.Ask("menu").Contains("App Flow Test"));
    Check("its folder is gone", !Directory.Exists(calcDir));
    Check("a 16C+ preset does not exist", machine.Ask("start 16c+").StartsWith("error"));
    // the same sheet as a test file, one line per test, run by the Test window's code
    var sheetText = new StringBuilder("# HP-16C+ test sheet: 16C ROM + the 16C+ patch. One line, one test.\n");
    foreach (var part in sheet)
    {
        sheetText.Append($"\n# {part.Title}\nreset\n");
        foreach (var st in part.Steps)
        {
            bool held = st.Keys.EndsWith(" (hold)");
            var keyText = st.Keys.Replace(" (hold)", "").Replace("f·", "").Replace("g·", "");
            if (keyText == "ON + *") keyText = "chord *";
            else if (held) keyText = "hold " + keyText;
            var lit = st.Flags.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var flagText = string.Join(" ", new[] { "C", "G" }.Select(f => lit.Contains(f) ? f : "-" + f));
            sheetText.Append($"{keyText}  =>  {st.Lcd}  [{flagText}]\n");
        }
    }
    var sheetFile = Path.Combine(root, "csharp", "docs", "examples", "16cplus-test-sheet.txt");
    File.WriteAllText(sheetFile, sheetText.ToString());
    machine.Ask("start 16c");
    machine.Ask("patch " + patchPath);
    var sheetRun = machine.Ask("testrun " + sheetFile);
    Say("test window, sheet: " + sheetRun);
    Check("test window passes the whole sheet", sheetRun.StartsWith("ok 77 ok, 0 failed"));
    var badFile = Path.Combine(root, "build", "app-flow-bad.txt");
    File.WriteAllLines(badFile, ["1 2 ENTER 3 0 +  =>  43 h", "NOSUCHKEY"]);
    var badRun = machine.Ask("testrun " + badFile);
    Check("test window reports failures", badRun.StartsWith("ok 0 ok, 2 failed") && badRun.Contains("failed with '42 h'") && badRun.Contains("unknown key"));
    var shot = Path.Combine(root, "build", "app-flow-window.png");
    Say("screenshot: " + machine.Ask("screenshot " + shot));
    machine.Ask("quit");
    Say($"SUMMARY window, original 16C: {a.Passed}/{a.Passed + a.Failed} steps as on the sheet");
    Say($"SUMMARY window, 16C+:         {b.Passed}/{b.Passed + b.Failed} steps as on the sheet");
    Say($"SUMMARY window, other checks: {extraOk}/{extraOk + extraWrong} ok");
    File.WriteAllText(Path.Combine(root, "build", "app-flow-window-report.txt"), report.ToString());
    return b.Failed + extraWrong == 0 ? 0 : 1;
}

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
    using var run = new MachineRunner(s.CreateMachine(img, chips, large), s.Model.Name);
    return RunSheetOn(new RunnerMachine(run));
}

(int Passed, int Failed) RunSheetOn(IMachine machine)
{
    int pass = 0, fail = 0;
    var t = new Driver(machine);
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
sealed class Driver(IMachine run)
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
        long end = run.Cycles + n;
        while (run.Cycles < end && run.Awake && DateTime.UtcNow < limit)
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
        if (run.DisplayOn) Tap(On);
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
        var d = run.Display().Trim();
        Finish();
        return d.Length == 0 ? "(blank)" : d;
    }

    public string Flags()
    {
        return run.Flags();
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

interface IMachine
{
    void Press(int code);
    void Release();
    bool Finished { get; }
    bool KeysApplied { get; }
    bool Awake { get; }
    bool DisplayOn { get; }
    long Cycles { get; }
    string Display();
    string Flags();
}

sealed class RunnerMachine(MachineRunner r) : IMachine
{
    public void Press(int code) => r.Press(code);
    public void Release() => r.Release();
    public bool Finished => r.Finished;
    public bool KeysApplied => r.KeysApplied;
    public bool Awake => r.Read(v => v.Cpu.Awake);
    public bool DisplayOn => r.Read(v => v.DisplayEnable);
    public long Cycles => r.Read(v => v.Cpu.Cycles);
    public string Display() => r.Read(v => v.Display());
    public string Flags()
    {
        var (c, g) = r.Read(v => ((Hp16c)v).FlagsCG());
        return string.Join(" ", new[] { c ? "C" : "", g ? "G" : "" }.Where(x => x.Length > 0));
    }
}

/// <summary>The running app, over its --api pipe.</summary>
sealed class PipeMachine : IMachine, IDisposable
{
    readonly System.IO.Pipes.NamedPipeClientStream _pipe;
    readonly StreamReader _in;
    readonly StreamWriter _out;

    public PipeMachine(string name)
    {
        _pipe = new(".", name, System.IO.Pipes.PipeDirection.InOut);
        _pipe.Connect(30000);
        _in = new StreamReader(_pipe, Encoding.UTF8);
        _out = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
    }

    public string Ask(string command)
    {
        _out.WriteLine(command);
        return _in.ReadLine() ?? "error pipe closed";
    }

    string[] Poll() => Ask("poll").Split(' ');
    public void Press(int code) => Ask($"down {code}");
    public void Release() => Ask("up");
    public bool Finished => Poll()[1] == "1";
    public bool KeysApplied => Poll()[2] == "1";
    public bool Awake => Poll()[3] == "1";
    public bool DisplayOn => Poll()[4] == "1";
    public long Cycles => long.Parse(Poll()[5]);
    public string Display() { var a = Ask("display"); return a.Length > 3 ? a[3..] : ""; }
    public string Flags()
    {
        var on = Ask("flags").Split(' ');
        return string.Join(" ", new[] { "C", "G" }.Where(on.Contains));
    }
    public void Dispose() { _pipe.Dispose(); }
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

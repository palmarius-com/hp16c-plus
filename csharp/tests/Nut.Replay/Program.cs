// Replay the stored test cases (tests/cases/*.jsonl) on the C# emulator,
// like tests/replay_cases.py does for the Python one. Same cases, same
// expectations: the two emulators must agree.
//
//   dotnet run --project csharp/tests/Nut.Replay -c Release -- [options] [files...]
//     --root DIR    project root (default: found from the current directory)
//     --plus OBJ    16C+ machine with this object file on the 16C ROM
//     --patch FILE  16C+ built like the app does: nonpareil/16c.rom + this
//                   release patch (checks stock ROM and chip sums); with
//                   --compare ROM the image must equal that full ROM text
//     --show N      differences to print (default 10)
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Nut.Core;

string? root = null, plus = null, patch = null, compare = null;
int show = 10;
var files = new List<string>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root": root = args[++i]; break;
        case "--plus": plus = args[++i]; break;
        case "--patch": patch = args[++i]; break;
        case "--compare": compare = args[++i]; break;
        case "--show": show = int.Parse(args[++i]); break;
        default: files.Add(args[i]); break;
    }
}
root ??= FindRoot(Directory.GetCurrentDirectory());
if (args.Length >= 1 && args[0] == "--state-check")
{
    // save state mid-calculation, load it into a fresh machine, continue both
    var np = Path.Combine(root, "nonpareil");
    var a1 = Hp16c.Stock(np).Start();
    a1.Run("HEX 1 2 3 4 ENTER 5 + STO 3 7");
    var json = MachineState.Capture(a1).ToJson();
    var b1 = Hp16c.Stock(np);
    MachineState.FromJson(json).Apply(b1);
    var again = MachineState.Capture(b1).ToJson();
    bool same = Strip(json) == Strip(again);
    a1.Run("* RCL 3 +");
    b1.Run("* RCL 3 +");
    Console.WriteLine($"state round trip: json {(same ? "equal" : "DIFFERENT")}, " +
                      $"after more keys '{a1.Display()}' vs '{b1.Display()}', {json.Length} chars");
    return same && a1.Display() == b1.Display() ? 0 : 1;
    static string Strip(string j) => string.Join("\n", j.Split('\n').Where(l => !l.Contains("savedAt") && !l.Contains("checksum")));
}
if (args.Length >= 2 && args[0] == "--keys")
{
    // every digit key per its legend must enter that digit, on the real firmware
    int wrongKeys = 0;
    foreach (var m in ModelCatalog.Models)
    {
        var rom = RomImage.Load(Path.Combine(args[1], m.Id, m.Id + ".rom"));
        var wrong = new List<string>();
        for (int digit = 0; digit <= 9; digit++)
        {
            var v = new Voyager(Voyager.EmbeddedNcd(m.Id), null, rom);
            v.PowerOn();
            v.Key(41);
            int one = v.Legends.First(kv => kv.Value[0] == "1").Key;
            v.Key(one);                                    // clears Pr Error
            int code = v.Legends.First(kv => kv.Value[0] == digit.ToString()).Key;
            v.Key(code);
            var shown = v.Display().Trim();
            if (!shown.StartsWith(digit.ToString())) wrong.Add($"legend {digit} (key {code}) shows '{shown}'");
        }
        Console.WriteLine($"{m.Name}: {10 - wrong.Count}/10 digit keys ok {string.Join("; ", wrong)}");
        wrongKeys += wrong.Count;
    }
    return wrongKeys;
}
if (args.Length >= 2 && args[0] == "--smoke")
{
    // every Voyager model: ROMs from a Nonpareil ncd/ tree (ncd/<id>/<id>.rom)
    int fails = 0;
    foreach (var m in ModelCatalog.Models)
    {
        var rom = RomImage.Load(Path.Combine(args[1], m.Id, m.Id + ".rom"));
        var v = new Voyager(Voyager.EmbeddedNcd(m.Id), null, rom);
        v.PowerOn();
        v.Key(41);
        var one = v.Legends.First(kv => kv.Value[0] == "1").Key;
        v.Key(35 == one ? 35 : one);
        bool orig = ModelCatalog.Identify(rom)?.Id == m.Id;
        Console.WriteLine($"{m.Name}: original={orig} display '{v.Display()}' unknown ops {v.Cpu.UnknownOps.Count}");
        if (!orig || v.Display().Trim().Length == 0) fails++;
    }
    return fails;
}
var nonpareil = Path.Combine(root, "nonpareil");
if (!File.Exists(Path.Combine(nonpareil, "16c.rom")))
{
    Console.Error.WriteLine($"no ROM in {nonpareil}: run tools/fetch_nonpareil.py first");
    return 2;
}
if (files.Count == 0)
    files = Directory.GetFiles(Path.Combine(root, "tests", "cases"), "*.jsonl").Order().ToList();

var cases = new List<JsonElement>();
foreach (var f in files)
    foreach (var line in File.ReadLines(f))
        if (line.Length > 0) cases.Add(JsonDocument.Parse(line).RootElement.Clone());

Hp16c machine;
if (patch is not null)
{
    var img = RomImage.Build16cPlus(RomImage.Load(Path.Combine(nonpareil, "16c.rom")), RomImage.Load(patch));
    Console.WriteLine($"built 16C+ from 16c.rom + {Path.GetFileName(patch)}: {img.Words.Count} words, chip sums ok");
    if (compare is not null)
    {
        var want = RomImage.Load(compare);
        var diff = want.Words.Keys.Union(img.Words.Keys)
            .Where(a => want.Words.GetValueOrDefault(a, -1) != img.Words.GetValueOrDefault(a, -1)).ToList();
        Console.WriteLine(diff.Count == 0 ? $"image equals {Path.GetFileName(compare)}"
            : $"image differs from {Path.GetFileName(compare)} at {diff.Count} addresses, first {diff[0]:x4}");
        if (diff.Count > 0) return 1;
    }
    machine = new Hp16c(img, plusChipMap: true);
    plus = patch;
}
else machine = plus is null ? Hp16c.Stock(nonpareil) : Hp16c.Plus(nonpareil, [plus]);
var c = machine.Start();
var sw = Stopwatch.StartNew();
var bad = new List<string>();
int changed = 0;
foreach (var cs in cases)
{
    var keys = string.Join(" ", cs.GetProperty("keys").EnumerateArray().Select(k => k.GetString()));
    c.Run(keys);
    if (plus is not null && IntendedChange(cs)) { changed++; continue; }
    int w = cs.TryGetProperty("w", out var wp) ? wp.GetInt32() : 64;
    var diffs = new List<string>();
    foreach (var e in cs.GetProperty("expect").EnumerateObject())
    {
        var exp = e.Value.ValueKind switch
        {
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Null => "None",
            _ => e.Value.GetString() ?? "",
        };
        var got = Observe(c, e.Name, w);
        if (got != exp) diffs.Add($"{e.Name}: expected '{exp}', got '{got}'");
    }
    if (diffs.Count > 0) bad.Add($"{cs.GetProperty("id").GetString()} {string.Join("; ", diffs)}");
}
foreach (var b in bad.Take(show)) Console.WriteLine(b);
if (changed > 0) Console.WriteLine($"{changed} cases skipped as intended 16C+ changes");
Console.WriteLine($"replay {cases.Count - bad.Count}/{cases.Count} ok ({files.Count} files), " +
                  $"{sw.Elapsed.TotalSeconds:F0} s");
return bad.Count > 0 ? 1 : 0;

static string Observe(Hp16c c, string key, int w)
{
    switch (key)
    {
        case "display":
        case "display_float": return c.Display();
        case "error": return c.Error() ?? "None";
        case "result": return FirstWord(c.Display());
        case "skipped": return FirstWord(c.Display()) == "0" ? "True" : "False";
        case "m": return string.Concat(c.Cpu.M.Reverse().Select(d => d.ToString("x")));
        case "c": return c.FlagsCG().Carry ? "True" : "False";
        case "g": return c.FlagsCG().OutOfRange ? "True" : "False";
        case "i": return c.RegI().ToString("x17");
        case "x": case "y": case "z": case "t": case "lastx":
            ulong mask = w >= 64 ? ulong.MaxValue : (1UL << w) - 1;
            return (c.Reg64(key) & mask).ToString("x" + ((w + 3) / 4));
        default: return $"(not observed: {key})";
    }
}

static string FirstWord(string s) =>
    s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

// The 16C+ is meant to differ from the stock 16C here (tests/replay_cases.py).
static bool IntendedChange(JsonElement cs)
{
    if (!cs.TryGetProperty("op", out var op) || op.GetString() != "WSIZE") return false;
    if (!cs.TryGetProperty("inputs", out var inputs)) return false;
    int w = cs.GetProperty("w").GetInt32();
    var x = UInt128.Parse(inputs.GetProperty("x").GetString()!, NumberStyles.HexNumber);
    int comp = cs.GetProperty("comp").GetString() switch { "unsigned" => 0, "1's" => 1, _ => 2 };
    UInt128 one = 1;
    if (comp != 0 && (x >> (w - 1)) != 0)
        x = comp == 2 ? (one << w) - x : (one << w) - 1 - x;
    return x >= 65 && x <= 128;
}

static string FindRoot(string dir)
{
    for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
    {
        if (Directory.Exists(Path.Combine(d.FullName, "tests", "cases"))) return d.FullName;
        var sub = Path.Combine(d.FullName, "HP16Cplus_challenge");
        if (Directory.Exists(Path.Combine(sub, "tests", "cases"))) return sub;
    }
    throw new DirectoryNotFoundException("project root with tests/cases not found; use --root");
}

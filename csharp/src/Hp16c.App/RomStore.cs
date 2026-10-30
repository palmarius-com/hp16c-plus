using Nut.Core;

namespace HpSim;

/// <summary>
/// Everything the user adds lives in the data folder next to the exe: original
/// ROMs in data/roms, saved calculators in data/custom/SHORTNAME. HP's firmware
/// never ships with the app: it comes from Nonpareil (download) or from a file the
/// user has.
/// </summary>
static class RomStore
{
    public static string Dir { get; } = AppContext.BaseDirectory;

    static string RomPath(string modelId) => Path.Combine(Dir, "data", "roms", modelId + ".rom");

    /// <summary>The file an original ROM is loaded from.</summary>
    public static string? OriginalFile(string modelId)
    {
        return File.Exists(RomPath(modelId)) ? RomPath(modelId) : null;
    }

    /// <summary>Path for a state file: relative to the exe, so it holds no user
    /// name and reads the same on every machine; else just the file name.</summary>
    public static string Relative(string file)
    {
        var rel = Path.GetRelativePath(Dir, file);
        return rel.StartsWith("..") || Path.IsPathRooted(rel) ? Path.GetFileName(file) : rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    public static string MyRomFile(MyCalc c) => CalcFile(c.Id, ".rom");

    public static bool Has(string modelId) => Load(modelId) is not null;

    public static RomImage? Load(string modelId)
    {
        if (File.Exists(RomPath(modelId))) return RomImage.Load(RomPath(modelId));
        return null;
    }

    /// <summary>Keep an original ROM. Returns the model it belongs to; throws
    /// with a readable reason if it is not one of the original ROMs.</summary>
    public static ModelCatalog.Model Import(IEnumerable<string> lines)
    {
        var img = RomImage.Parse(lines);
        var model = ModelCatalog.Identify(img) ?? throw new InvalidDataException(
            $"This file has {img.Words.Count} ROM words but is not one of the original Voyager " +
            "ROMs from Nonpareil (10C, 11C, 12C, 15C, 16C). To run a modified ROM, use " +
            "ROM > Load full ROM instead.");
        Directory.CreateDirectory(Path.GetDirectoryName(RomPath(model.Id))!);
        File.WriteAllLines(RomPath(model.Id), img.ToLines());
        return model;
    }

    public static async Task Download(ModelCatalog.Model model)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var text = await http.GetStringAsync(model.RomUrl);
        var got = Import(text.Split('\n'));
        if (got.Id != model.Id) throw new InvalidDataException($"downloaded ROM is the {got.Name}");
    }

    // ------------------------------------------------- your calculators
    /// <summary>A calculator the user saved: a full ROM (original + patches)
    /// with the model whose keyboard and display run it.</summary>
    public sealed class MyCalc
    {
        public string Name { get; set; } = "";
        public string Model { get; set; } = "";
        public string Label { get; set; } = "";
        /// <summary>Chip layout chosen by the user; null = what the ROM needs.</summary>
        public bool? Large { get; set; }
        /// <summary>Where the saved skin came from; the skin is in NAME.skin.</summary>
        public string? SkinName { get; set; }
        public string Description { get; set; } = "";
        public string Created { get; set; } = "";
        public string RomFingerprint { get; set; } = "";
        public string Note { get; set; } =
            "The .rom file next to this one contains HP's firmware: for your own use, do not share it.";
        [System.Text.Json.Serialization.JsonIgnore] public string Id { get; set; } = "";
    }

    static string MyDir => Path.Combine(Dir, "data", "custom");

    static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };

    public static string Slug(string name)
    {
        var s = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : c == '+' ? 'p' : '-').ToArray());
        while (s.Contains("--")) s = s.Replace("--", "-");
        s = s.Trim('-');
        return s.Length == 0 ? "calculator" : s;
    }

    static string CalcFile(string id, string ext) => Path.Combine(MyDir, id, id + ext);

    public static List<MyCalc> MyCalcs()
    {
        if (!Directory.Exists(MyDir)) return [];
        var list = new List<MyCalc>();
        foreach (var dir in Directory.GetDirectories(MyDir).Order())
        {
            var id = Path.GetFileName(dir);
            try
            {
                var c = System.Text.Json.JsonSerializer.Deserialize<MyCalc>(File.ReadAllText(CalcFile(id, ".json")), Json);
                if (c is null || !File.Exists(CalcFile(id, ".rom"))) continue;
                c.Id = id;
                list.Add(c);
            }
            catch { /* skip a folder the user broke by hand */ }
        }
        return list;
    }

    public static RomImage LoadMyRom(MyCalc c) => RomImage.Load(CalcFile(c.Id, ".rom"));

    /// <summary>A short name from a long one: letters, digits and + - _ only.</summary>
    public static string SuggestShortName(string name) =>
        new(name.Where(c => char.IsLetterOrDigit(c) || c is '+' or '-' or '_').ToArray());

    /// <summary>A calculator with this short name exists already.</summary>
    public static bool ShortNameTaken(string? shortName) =>
        Directory.Exists(MyDir) && Directory.GetDirectories(MyDir).Select(Path.GetFileName)
            .Any(d => string.Equals(d, shortName?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Why a short name cannot be used; null = fine. It becomes a folder
    /// and file name.</summary>
    public static string? ShortNameProblem(string? shortName)
    {
        var s = shortName?.Trim() ?? "";
        if (s.Length == 0) return "Enter a short name.";
        if (s.Length > 32) return "The short name is too long (32 characters at most).";
        if (s != SuggestShortName(s)) return "Use only letters, digits and + - _ in the short name.";
        return null;
    }

    public static MyCalc SaveMyCalc(string shortName, string name, string model, string label, string description,
                                    RomImage image, bool? large = null, string? skinJson = null, string? skinName = null)
    {
        // Override replaces the whole calculator, also if only the capitals differ
        if (Directory.Exists(MyDir))
            foreach (var old in Directory.GetDirectories(MyDir))
                if (string.Equals(Path.GetFileName(old), shortName, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(old, recursive: true);
        Directory.CreateDirectory(Path.Combine(MyDir, shortName));
        var c = new MyCalc
        {
            Name = name, Model = model, Label = label, Description = description, Large = large,
            SkinName = skinJson is null ? null : skinName,
            Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            RomFingerprint = image.CanonicalSha256(), Id = shortName,
        };
        File.WriteAllText(CalcFile(c.Id, ".rom"),
            image.ToNonpareilText($"{name}: saved calculator ({model} keyboard and display)"));
        File.WriteAllText(CalcFile(c.Id, ".json"), System.Text.Json.JsonSerializer.Serialize(c, Json));
        if (skinJson is not null) File.WriteAllText(CalcFile(c.Id, ".skin"), skinJson);
        else File.Delete(CalcFile(c.Id, ".skin"));
        return c;
    }

    public static void DeleteMyCalc(MyCalc c) => Directory.Delete(Path.Combine(MyDir, c.Id), recursive: true);

    public static string? LoadMySkin(MyCalc c)
    {
        var f = CalcFile(c.Id, ".skin");
        return File.Exists(f) ? File.ReadAllText(f) : null;
    }
}

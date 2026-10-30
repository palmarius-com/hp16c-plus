using Nut.Core;

namespace HpSim;

/// <summary>
/// The user's original ROMs, one per model, kept in the application data
/// folder. HP's firmware never ships with the app: it comes from Nonpareil
/// (download) or from a file the user has.
/// </summary>
static class RomStore
{
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HP16C-Plus");

    static string RomPath(string modelId) => Path.Combine(Dir, "roms", modelId + ".rom");

    public static bool Has(string modelId) => Load(modelId) is not null;

    public static RomImage? Load(string modelId)
    {
        if (File.Exists(RomPath(modelId))) return RomImage.Load(RomPath(modelId));
        var old = Path.Combine(Dir, modelId + ".rom"); // first app version kept 16c.rom here
        return File.Exists(old) ? RomImage.Load(old) : null;
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
        public string Description { get; set; } = "";
        public string Created { get; set; } = "";
        public string RomFingerprint { get; set; } = "";
        public string Note { get; set; } =
            "The .rom file next to this one contains HP's firmware: for your own use, do not share it.";
        [System.Text.Json.Serialization.JsonIgnore] public string Id { get; set; } = "";
    }

    static string MyDir => Path.Combine(Dir, "my");

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

    public static List<MyCalc> MyCalcs()
    {
        if (!Directory.Exists(MyDir)) return [];
        var list = new List<MyCalc>();
        foreach (var f in Directory.GetFiles(MyDir, "*.json").Order())
        {
            try
            {
                var c = System.Text.Json.JsonSerializer.Deserialize<MyCalc>(File.ReadAllText(f), Json);
                if (c is null || !File.Exists(Path.ChangeExtension(f, ".rom"))) continue;
                c.Id = Path.GetFileNameWithoutExtension(f);
                list.Add(c);
            }
            catch { /* skip a file the user broke by hand */ }
        }
        return list;
    }

    public static RomImage LoadMyRom(MyCalc c) => RomImage.Load(Path.Combine(MyDir, c.Id + ".rom"));

    public static MyCalc SaveMyCalc(string name, string model, string label, string description, RomImage image)
    {
        Directory.CreateDirectory(MyDir);
        var c = new MyCalc
        {
            Name = name, Model = model, Label = label, Description = description,
            Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            RomFingerprint = image.CanonicalSha256(), Id = Slug(name),
        };
        File.WriteAllText(Path.Combine(MyDir, c.Id + ".rom"),
            image.ToNonpareilText($"{name}: saved calculator ({model} keyboard and display)"));
        File.WriteAllText(Path.Combine(MyDir, c.Id + ".json"), System.Text.Json.JsonSerializer.Serialize(c, Json));
        return c;
    }

    public static void DeleteMyCalc(MyCalc c)
    {
        File.Delete(Path.Combine(MyDir, c.Id + ".rom"));
        File.Delete(Path.Combine(MyDir, c.Id + ".json"));
    }

    /// <summary>When run from the repository: the 16C+ patch built by tools/build_plus.py.</summary>
    public static string? FindRepoPatch()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            foreach (var rel in new[] { "build/16cplus.patch", "build/16cplus.obj",
                                        "build/16cplus.patch" })
            {
                var p = Path.Combine(d.FullName, rel);
                if (File.Exists(p)) return p;
            }
        return null;
    }
}

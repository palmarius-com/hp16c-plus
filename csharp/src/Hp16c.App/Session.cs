using System.Text;
using Nut.Core;

namespace HpSim;

/// <summary>What is running: a model, its ROM (original or loaded), patches on top.</summary>
sealed class Session
{
    public required ModelCatalog.Model Model { get; init; }
    public required RomImage Base { get; init; }
    public required string BaseLabel { get; init; }
    /// <summary>The ROM file on disk, if it came from one.</summary>
    public string? RomFile { get; init; }
    /// <summary>Faceplate label of a saved calculator (e.g. 16C+).</summary>
    public string? Label { get; init; }
    /// <summary>Name of a saved calculator.</summary>
    public string? Name { get; init; }
    /// <summary>Short name (folder) of a saved calculator.</summary>
    public string? ShortName { get; init; }
    /// <summary>The user's chip layout choice; null = the preset.</summary>
    public bool? UserLarge { get; set; }
    /// <summary>The user's faceplate label; null = the preset.</summary>
    public string? UserLabel { get; set; }
    /// <summary>The user's skin (JSON text and where it came from); null = the preset.</summary>
    public string? SkinJson { get; set; }
    public string? SkinName { get; set; }
    public List<(string Name, RomImage Words)> Patches { get; } = [];

    // How the session began; Changes() compares against this.
    List<string>? _startPatches;
    bool? _startLarge;
    string? _startLabel;
    string? _startSkin;

    /// <summary>Remember the starting point (the first time only).</summary>
    public void MarkStart()
    {
        if (_startPatches is not null) return;
        MarkSaved();
    }

    /// <summary>What is running now counts as the starting point.</summary>
    public void MarkSaved()
    {
        _startPatches = Patches.Select(p => p.Name).ToList();
        _startLarge = UserLarge;
        _startLabel = UserLabel;
        _startSkin = SkinName;
    }

    /// <summary>What the user changed since the start; empty = nothing.</summary>
    public List<string> Changes()
    {
        var list = new List<string>();
        var start = _startPatches ?? [];
        var now = Patches.Select(p => p.Name).ToList();
        foreach (var p in now.Except(start)) list.Add($"Patch applied: {p}");
        foreach (var p in start.Except(now)) list.Add($"Patch removed: {p}");
        var img = Image();
        if (UserLarge is { } large && UserLarge != _startLarge && (large != NeedsLarge(img) || _startLarge is not null))
            list.Add($"Chip layout: {(large ? "large (15C chips)" : "the calculator's own chips")}");
        if (UserLabel != _startLabel)
            list.Add($"Faceplate label: {UserLabel ?? "automatic"}");
        if (SkinName != _startSkin)
            list.Add($"Skin: {SkinName ?? "automatic"}");
        return list;
    }

    public RomImage Image()
    {
        if (Patches.Count == 0) return Base;
        var img = new RomImage();
        foreach (var (a, w) in Base.Words) img.Words[a] = w;
        foreach (var (_, p) in Patches)
            foreach (var (a, w) in p.Words) img.Words[a] = w;
        return img;
    }

    static List<(int Lo, int Hi)> RomRanges(Voyager.Ncd ncd) =>
        ncd.Chips.SelectMany(c => c.Mems).Where(m => m.Space == "inst")
           .Select(m => (m.Base, m.Base + m.Size - 1)).ToList();

    /// <summary>Words the model's own chips have no ROM for.</summary>
    public List<int> BeyondOwnChips(RomImage img)
    {
        var ranges = RomRanges(Voyager.EmbeddedNcd(Model.Id));
        return img.Words.Keys.Where(a => !ranges.Any(r => a >= r.Lo && a <= r.Hi)).ToList();
    }

    /// <summary>The preset: the large 15C chip map when the ROM has words the
    /// model has no ROM for, else its own chips.</summary>
    public bool NeedsLarge(RomImage img) => BeyondOwnChips(img).Count > 0;

    /// <summary>The chip layout in use: the user's choice, else the preset.</summary>
    public (Voyager.Ncd Chips, bool Large) ChipMap(RomImage img)
    {
        bool large = UserLarge ?? NeedsLarge(img);
        return (large ? Voyager.EmbeddedNcd(ModelCatalog.LargeChipMap) : Voyager.EmbeddedNcd(Model.Id), large);
    }

    /// <summary>What does not run with the model's own chips, or null.</summary>
    public string? NotLoaded(RomImage img, bool large)
    {
        var beyond = BeyondOwnChips(img);
        if (large || beyond.Count == 0) return null;
        return $"{beyond.Count} ROM words at {beyond.Min():X4}-{beyond.Max():X4} are not loaded: " +
               $"the {Model.Name}'s own chips have no ROM there, so code that jumps there will not run.";
    }

    /// <summary>The label a known build suggests; only where it can really run.</summary>
    public string? SuggestedLabel(RomImage img) =>
        ChipMap(img).Large ? ModelCatalog.KnownBuild(img) : null;

    /// <summary>The faceplate label set for this ROM: the user's, a saved
    /// calculator's, or the suggestion; null = the model's.</summary>
    public string? FaceLabel(RomImage img) => UserLabel ?? Label ?? SuggestedLabel(img);

    /// <summary>The calculator for this session: 16C stack readers on a 16C.</summary>
    public Voyager CreateMachine(RomImage img, Voyager.Ncd chips, bool large)
    {
        var ncd = Voyager.EmbeddedNcd(Model.Id);
        var chipArg = large ? chips : null;
        var rom = img;
        if (!large && NeedsLarge(img))
        {
            rom = new RomImage();
            var beyond = BeyondOwnChips(img).ToHashSet();
            foreach (var (a, w) in img.Words)
                if (!beyond.Contains(a)) rom.Words[a] = w;
        }
        return Model.Id == "16c" ? new Hp16c(ncd, chipArg, rom) : new Voyager(ncd, chipArg, rom);
    }

    public string Info()
    {
        var img = Image();
        var (chips, large) = ChipMap(img);
        var sb = new StringBuilder();
        sb.AppendLine($"Calculator: {Model.Name} (keyboard, display)");
        sb.AppendLine($"ROM: {BaseLabel}");
        foreach (var (name, p) in Patches) sb.AppendLine($"Patch: {name} ({p.Words.Count} words)");
        string why = UserLarge is null ? "preset" : "your choice";
        sb.AppendLine(large ? $"Chip layout: large (15C chips: ROM 0000-17FF and 2000-37FF), {why}"
                            : $"Chip layout: the {Model.Name}'s own, {why}");
        if (NotLoaded(img, large) is { } missing) sb.AppendLine(missing);
        foreach (var (lo, hi) in RomRanges(chips))
        {
            if (!img.Words.Keys.Any(a => a >= lo && a <= hi)) continue;
            int sum = img.ChipSum(lo, hi);
            sb.AppendLine($"ROM chip {lo:X4}-{hi:X4}: checksum {(sum == 1 ? "ok" : $"off ({sum}, needs 1): the self-test will show an error, the calculator still runs")}");
        }
        sb.AppendLine($"Fingerprint (canonical SHA-256): {img.CanonicalSha256()}");
        return sb.ToString();
    }
}

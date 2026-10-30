using System.Text;
using Nut.Core;

namespace HpSim;

/// <summary>What is running: a model, its ROM (original or loaded), patches on top.</summary>
sealed class Session
{
    public required ModelCatalog.Model Model { get; init; }
    public required RomImage Base { get; init; }
    public required string BaseLabel { get; init; }
    /// <summary>Faceplate label of a saved calculator (e.g. 16C+).</summary>
    public string? Label { get; init; }
    /// <summary>Name of a saved calculator.</summary>
    public string? Name { get; init; }
    public List<(string Name, RomImage Words)> Patches { get; } = [];

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

    /// <summary>The model's own chips, or the large 15C chip map when the ROM
    /// has words the model has no ROM for.</summary>
    public (Voyager.Ncd Chips, bool Large) ChipMap(RomImage img)
    {
        var own = Voyager.EmbeddedNcd(Model.Id);
        var ranges = RomRanges(own);
        bool fits = img.Words.Keys.All(a => ranges.Any(r => a >= r.Lo && a <= r.Hi));
        return fits ? (own, false) : (Voyager.EmbeddedNcd(ModelCatalog.LargeChipMap), true);
    }

    /// <summary>The calculator for this session: 16C stack readers on a 16C.</summary>
    public Voyager CreateMachine(RomImage img, Voyager.Ncd chips, bool large)
    {
        var ncd = Voyager.EmbeddedNcd(Model.Id);
        var chipArg = large ? chips : null;
        return Model.Id == "16c" ? new Hp16c(ncd, chipArg, img) : new Voyager(ncd, chipArg, img);
    }

    public string Info()
    {
        var img = Image();
        var (chips, large) = ChipMap(img);
        var sb = new StringBuilder();
        sb.AppendLine($"Calculator: {Model.Name} (keyboard, display)");
        sb.AppendLine($"ROM: {BaseLabel}");
        foreach (var (name, p) in Patches) sb.AppendLine($"Patch: {name} ({p.Words.Count} words)");
        sb.AppendLine(large ? "Chip map: large (15C chips: ROM 0000-17FF and 2000-37FF), because the ROM needs it"
                            : $"Chip map: the {Model.Name}'s own");
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

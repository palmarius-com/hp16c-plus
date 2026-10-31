namespace Nut.Core;

/// <summary>
/// The Voyager calculators this emulator can run (Nut CPU + R2D2 display
/// chip). Chip definitions ship with the library; the ROMs do not: the user
/// loads them from Eric Smith's Nonpareil project or from a file.
/// </summary>
public static class ModelCatalog
{
    public sealed record Model(string Id, string Name, string Description, string StockSha256)
    {
        public string RomUrl => $"{RomImage.NonpareilRawBase}{Id}/{Id}.rom";
    }

    /// <summary>Canonical SHA-256 (see RomImage.CanonicalSha256) of each
    /// original Nonpareil ROM, to tell originals from modified ROMs.</summary>
    public static readonly Model[] Models =
    [
        new("10c", "HP-10C", "Scientific",
            "94fbb82765948a16811029bde8f216588562ebbc2fb04d37ff7dd9859d41314a"),
        new("11c", "HP-11C", "Scientific programmable",
            "5e87c9c19fa7cc01331423a4060fef6e64c2bd86be77cf2f30b0405705308b29"),
        new("12c", "HP-12C", "Financial",
            "4b27ae0e483520044ae4c4ce0163b9bff7d8c174f9f2d420a4a8d92235845f48"),
        new("15c", "HP-15C", "Scientific with matrices and complex numbers",
            "df7883c70b2eb2d6beff2e4ed6af25bd2e38fa4d50276143fea3bf45b73678e2"),
        new("16c", "HP-16C", "Computer scientist (integers, bases, bits)",
            RomImage.Stock16cCanonicalSha256),
    ];

    /// <summary>Chip map with the most room: ROM 0000-17FF and 2000-37FF, the
    /// 15C's RAM. For patches that need more space than the model has.</summary>
    public const string LargeChipMap = "15c-192";

    /// <summary>The same, with the second ROM chip as one 8K block at 1800-37FF.
    /// For a ROM that has words in 1800-1FFF (e.g. the 16CEx): its checksum covers
    /// the whole block. The machine runs the same either way; this only decides
    /// which chip boundaries the checksum report uses.</summary>
    public const string LargeChipMap8K = "15c-192-8k";

    /// <summary>The large chip map that fits this ROM.</summary>
    public static string LargeMapFor(RomImage rom) =>
        rom.Words.Keys.Any(a => a >= 0x1800 && a <= 0x1FFF) ? LargeChipMap8K : LargeChipMap;

    /// <summary>Fingerprints of known patched ROMs -> faceplate label. Identifies
    /// a build by what it is, whatever its file was called.</summary>
    public static readonly Dictionary<string, string> KnownBuilds = new()
    {
        ["4f408e65f64a7ba27a66237116187474c3c26517ac99324f5fa36cdd8feb37cd"] = "16C+",
    };

    /// <summary>Label of a known patched ROM, or null.</summary>
    public static string? KnownBuild(RomImage rom) =>
        KnownBuilds.GetValueOrDefault(rom.CanonicalSha256());

    public static Model Get(string id) => Models.First(m => m.Id == id);

    /// <summary>Which original ROM this is, if any.</summary>
    public static Model? Identify(RomImage rom)
    {
        var sha = rom.CanonicalSha256();
        return Models.FirstOrDefault(m => m.StockSha256 == sha);
    }
}

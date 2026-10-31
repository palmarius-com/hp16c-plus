using System.Security.Cryptography;
using System.Text;

namespace Nut.Core;

/// <summary>
/// Nut ROM words by address, and the 16C+ build from the user's own copy of
/// the original HP-16C ROM plus our patch file. We ship only the patch (our
/// words); the HP firmware comes from Nonpareil, loaded by the user.
/// </summary>
public sealed class RomImage
{
    /// <summary>SHA-256 of Nonpareil's ncd/16c/16c.rom in canonical form
    /// (sorted "aaaa:www\n" lines, lower case): independent of line endings
    /// and comments, so a copy saved by a browser still matches.</summary>
    public const string Stock16cCanonicalSha256 =
        "bbccd62ebc2da37688cbaa631b6fe38664896467a4aff676552ec51e17e7631d";

    public const string NonpareilRawBase = "https://raw.githubusercontent.com/brouhaha/nonpareil/HEAD/ncd/";
    public const string Stock16cUrl = NonpareilRawBase + "16c/16c.rom";

    public SortedDictionary<int, int> Words { get; } = [];

    public static RomImage Parse(IEnumerable<string> lines)
    {
        var img = new RomImage();
        foreach (var raw in lines)
        {
            var line = raw.Split('#')[0].Trim();
            // aaaa:www, then nothing or a note after white space (01DA:3D0 del)
            if (line.Length >= 8 && line[4] == ':' && (line.Length == 8 || char.IsWhiteSpace(line[8])) &&
                int.TryParse(line.AsSpan(0, 4), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var addr) &&
                int.TryParse(line.AsSpan(5, 3), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var word))
                img.Words[addr] = word;
        }
        return img;
    }

    public static RomImage Load(string path) => Parse(File.ReadAllLines(path));

    public string CanonicalSha256()
    {
        var sb = new StringBuilder();
        foreach (var (a, w) in Words) sb.Append($"{a:x4}:{w:x3}\n");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(sb.ToString())));
    }

    public bool IsStock16c => CanonicalSha256() == Stock16cCanonicalSha256;

    /// <summary>Sum of the words in [lo, hi] mod 1023; a Voyager chip passes the
    /// self-test when this is 1.</summary>
    public int ChipSum(int lo, int hi)
    {
        long s = 0;
        for (int a = lo; a <= hi; a++) s += Words.GetValueOrDefault(a);
        return (int)(s % 1023);
    }

    public IEnumerable<string> ToLines() => Words.Select(kv => $"{kv.Key:x4}:{kv.Value:x3}");

    /// <summary>
    /// 16C+: the stock 16C (0000-17FF) with the patch words on top, new code in
    /// 2000-37FF. Throws if the base is not the stock 16C ROM, if the patch
    /// touches anything else, or if a chip checksum fails.
    /// </summary>
    public static RomImage Build16cPlus(RomImage stock, RomImage patch)
    {
        if (!stock.IsStock16c)
            throw new InvalidDataException(
                "this is not Nonpareil's original 16c.rom (canonical SHA-256 " +
                $"{stock.CanonicalSha256()[..12]}..., expected {Stock16cCanonicalSha256[..12]}...)");
        var img = new RomImage();
        foreach (var (a, w) in stock.Words) img.Words[a] = w;
        foreach (var (a, w) in patch.Words)
        {
            if (a >= 0x3800 || (a >= 0x1800 && a < 0x2000) || w > 0x3FF)
                throw new InvalidDataException($"patch word {a:x4}:{w:x3} is outside the 16C+ layout");
            img.Words[a] = w;
        }
        for (int a = 0x2000; a < 0x3800; a++) img.Words.TryAdd(a, 0);
        foreach (var (lo, hi) in new[] { (0x0000, 0x17FF), (0x2000, 0x37FF) })
            if (img.ChipSum(lo, hi) != 1)
                throw new InvalidDataException($"chip {lo:x4}-{hi:x4} does not sum to 1 mod 1023");
        return img;
    }

    /// <summary>Full image as Nonpareil text (layout of 15c.rom). Contains HP code:
    /// written only to the user's own disk.</summary>
    public string ToNonpareilText(string title)
    {
        var sb = new StringBuilder();
        sb.Append($"# {title}\n# Built locally from the user's copy of the original HP ROM; not for publication.\n");
        foreach (var l in ToLines()) sb.Append(l).Append('\n');
        return sb.ToString();
    }

    /// <summary>Patch text: the words of this image that differ from an original
    /// ROM, nothing else. Contains no HP code that was not changed.</summary>
    public string ToPatchText(RomImage original, string title)
    {
        var sb = new StringBuilder();
        sb.Append($"# {title}\n");
        sb.Append($"# Made with the Palmarius Voyager simulator. Format: aaaa:www (hex address : 10-bit word).\n");
        sb.Append($"# For the original ROM with fingerprint {original.CanonicalSha256()}\n");
        sb.Append($"# Applied to it, this gives fingerprint {CanonicalSha256()}\n");
        foreach (var (a, w) in Words)
            if (original.Words.GetValueOrDefault(a, -1) != w) sb.Append($"{a:x4}:{w:x3}\n");
        return sb.ToString();
    }

    /// <summary>Full image for NutEm/PC: one "AAAA WWW" line per word from 0, gaps as 0.</summary>
    public string ToMClst(int size = 0x3800)
    {
        var sb = new StringBuilder();
        for (int a = 0; a < size; a++) sb.Append($"{a:X4} {Words.GetValueOrDefault(a):X3}\r\n");
        return sb.ToString();
    }
}

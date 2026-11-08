using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Ce.Flash;

/// <summary>One flash region of a unit the kit knows, by SHA-256 (no HP bytes in the program).</summary>
public sealed record KnownRegion(int Start, int Length, string Sha256, string Name);

/// <summary>The dump is not a calculator the kit has been tried on: nothing is built, nothing may be written.</summary>
public sealed class UnknownUnitException(IReadOnlyList<KnownRegion> differing)
    : Exception("This dump is not a unit the kit knows. These regions differ from the unit it was tried on: " +
                string.Join("; ", differing.Select(r => $"{r.Start:X5}+{r.Length:X5} {r.Name}")))
{
    public IReadOnlyList<KnownRegion> Differing { get; } = differing;
}

/// <summary>What <see cref="CeUnit.BuildSlot"/> made.</summary>
/// <param name="Slot">28672 bytes for flash 0xB180-0x1217F: Nut words 0000-17FF, 1800-1FFF zero, 2000-37FF, 16-bit little endian.</param>
/// <param name="Pointer">4 bytes for the literal at 0x1CD10: the address of the slot (0xB180).</param>
public sealed record SlotImage(byte[] Slot, byte[] Pointer, int UnitChipSum, int SumWithPatch, int ChecksumWordOld, int ChecksumWordNew, int FinalChipSum);

/// <summary>
/// A HP-15C Collector's Edition as its flash dump shows it (0x00000-0x1FFFF, 131072 bytes). Knows where the 16C ROM sits
/// (0x12180, 6144 words of 16 bits) and builds the image for the 15.2 slot from the unit's OWN 16C ROM plus the words of a
/// patch: the same as kit/build_image.ps1, which put the first image on the calculator.
/// </summary>
public sealed class CeUnit
{
    public const int DumpSize = 0x20000;
    public const int Rom16cAt = 0x12180, Rom16cWords = 0x1800;
    public const int SlotAt = 0xB180, SlotBytes = 0x7000, PointerAt = 0x1CD10;
    public const uint OriginalPointer = 0x12180;            // the literal at 0x1CD10 before an image: the original 16C ROM
    public const int ChecksumWord = 0x13FF;                 // free (000) in the original 16C ROM; the 16C+ patch keeps its checksum there
    const int Mod = 1023;

    readonly byte[] _dump;
    CeUnit(byte[] dump, IReadOnlyList<KnownRegion> imageRegions) { _dump = dump; ImageRegionsChanged = imageRegions; }
    public byte[] Dump => _dump;

    /// <summary>The slot (0xB180) and/or the pointer (0x1CD10) differ from the factory: an image (a 16C+ or another) is installed.</summary>
    public IReadOnlyList<KnownRegion> ImageRegionsChanged { get; }

    /// <summary>known-units.txt: lines "SSSSS NNNNN sha256  name" (hex start, hex length).</summary>
    public static List<KnownRegion> ParseKnown(string text)
    {
        var list = new List<KnownRegion>();
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line.TrimEnd('\r'), @"^([0-9A-Fa-f]{5}) ([0-9A-Fa-f]{5}) ([0-9a-f]{64})  (.*)$");
            if (m.Success)
                list.Add(new KnownRegion(Convert.ToInt32(m.Groups[1].Value, 16), Convert.ToInt32(m.Groups[2].Value, 16), m.Groups[3].Value, m.Groups[4].Value));
        }
        return list;
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Check the dump against the known regions; throws <see cref="UnknownUnitException"/> when any region differs. With
    /// allowImage the 15.2 slot (0xB180) and the pointer literal (0x1CD10) may differ: that is the same calculator with an
    /// image installed (what an upload starts from, and what a second upload finds).
    /// </summary>
    public static CeUnit FromDump(byte[] dump, IReadOnlyList<KnownRegion> known, bool allowImage = false)
    {
        if (dump.Length != DumpSize) throw new ArgumentException($"the dump must be {DumpSize} bytes (flash 0x00000-0x1FFFF), this one has {dump.Length}");
        if (known.Count == 0) throw new ArgumentException("no known regions");
        var bad = new List<KnownRegion>();
        var image = new List<KnownRegion>();
        bool pointerChanged = BitConverter.ToUInt32(dump, PointerAt) != OriginalPointer;
        foreach (var r in known)
        {
            var bytes = dump.AsSpan(r.Start, r.Length);
            bool holdsPointer = r.Start <= PointerAt && PointerAt + 4 <= r.Start + r.Length;
            if (allowImage && holdsPointer && pointerChanged)         // the 4 pointer bytes may be an image's; everything around them must be factory
            {
                var copy = bytes.ToArray();
                BitConverter.GetBytes(OriginalPointer).CopyTo(copy, PointerAt - r.Start);
                bytes = copy;
            }
            if (Sha256Hex(bytes) == r.Sha256)
            {
                if (allowImage && pointerChanged && r.Start == PointerAt) image.Add(r);
                continue;
            }
            if (allowImage && r.Start == SlotAt) image.Add(r);        // the slot itself may hold an image
            else bad.Add(r);
        }
        if (bad.Count > 0) throw new UnknownUnitException(bad);
        return new CeUnit(dump, image);
    }

    /// <summary>
    /// What a My Calculator changes in the 16C ROM: the words where it differs from the original 16C (Nonpareil's), in
    /// 0000-17FF, and every word of the second ROM range 2000-37FF. These, laid over the unit's OWN 16C ROM, are what goes to the
    /// calculator: never the original ROM itself (the CE's differs from it at six words).
    /// </summary>
    public static Dictionary<int, int> ChangesFrom(IReadOnlyDictionary<int, int> mine, IReadOnlyDictionary<int, int> original)
    {
        var changes = new Dictionary<int, int>();
        foreach (var (a, w) in mine)
        {
            if (a >= 0x1800 && a < 0x2000 || a >= 0x3800 || a < 0)
                throw new ArgumentException($"this calculator has a ROM word at {a:X4}, outside the two ROM ranges of the 16C slot (0000-17FF, 2000-37FF)");
            if (a >= 0x2000 || !original.TryGetValue(a, out var o) || o != w) changes[a] = w;
        }
        return changes;
    }

    /// <summary>The words of the changes that overwrite a word where this unit's 16C ROM differs from the original 16C (a CE-specific word).</summary>
    public List<int> OverwritesUnitSpecificWords(IReadOnlyDictionary<int, int> changes, IReadOnlyDictionary<int, int> original)
    {
        var own = Own16cRom();
        return changes.Keys.Where(a => a < Rom16cWords && original.TryGetValue(a, out var o) && own[a] != o && changes[a] != own[a]).OrderBy(a => a).ToList();
    }

    /// <summary>
    /// May the checksum word 0x13FF be set to keep the unit's chip sum? Yes when the changes do not use it, or when they set it as
    /// their own checksum word (the original 16C ROM with the changes sums to 1, as the 16C+ patch and make_patch --checksum leave it).
    /// No when it is part of someone's code: then the chip sum of the unit changes and the user is told.
    /// </summary>
    public static bool ChecksumWordIsFree(IReadOnlyDictionary<int, int> changes, IReadOnlyDictionary<int, int> original)
    {
        if (!changes.ContainsKey(ChecksumWord)) return true;
        var rom = new Dictionary<int, int>(original);
        foreach (var (a, w) in changes) if (a < Rom16cWords) rom[a] = w;
        return rom.Where(kv => kv.Key < Rom16cWords).Sum(kv => kv.Value) % Mod == 1;
    }

    /// <summary>
    /// The ROM the calculator's 16 mode runs, as Nut words: its own 16C ROM (pointer 0x12180, the factory state) or the image in the
    /// 15.2 slot (pointer 0xB180: words 0000-17FF and the non-zero words of 2000-37FF). Anything else is not understood.
    /// </summary>
    public Dictionary<int, int> RunningRom16(out string what)
    {
        uint pointer = BitConverter.ToUInt32(_dump, PointerAt);
        var words = new Dictionary<int, int>();
        if (pointer == OriginalPointer)
        {
            var own = Own16cRom();
            for (int a = 0; a < Rom16cWords; a++) words[a] = own[a];
            what = "the calculator's own 16C ROM (its factory 16 mode)";
            return words;
        }
        if (pointer == SlotAt)
        {
            for (int a = 0; a < 0x3800; a++)
            {
                if (a >= 0x1800 && a < 0x2000) continue;
                int w = BitConverter.ToUInt16(_dump, SlotAt + 2 * a);
                if (a < 0x1800 || w != 0) words[a] = w;
            }
            what = "the image installed in the 15.2 slot, which the 16 mode runs";
            return words;
        }
        throw new InvalidOperationException($"the pointer at 0x{PointerAt:X} is {pointer:X8}: neither the original 16C ROM nor the slot, not understood");
    }

    /// <summary>The words of an image that differ from this unit's own 16C ROM (0000-17FF) plus its non-zero words of 2000-37FF: "the differences from the backup" as a patch.</summary>
    public SortedDictionary<int, int> DifferencesFromOwnRom(SlotImage img)
    {
        var own = Own16cRom();
        var diff = new SortedDictionary<int, int>();
        for (int a = 0; a < 0x3800; a++)
        {
            if (a >= 0x1800 && a < 0x2000) continue;
            int w = img.Slot[2 * a] | (img.Slot[2 * a + 1] << 8);
            if (a < 0x1800 ? w != own[a] : w != 0) diff[a] = w;
        }
        return diff;
    }

    /// <summary>The unit's own 16C ROM, 6144 words.</summary>
    public int[] Own16cRom()
    {
        var rom = new int[Rom16cWords];
        for (int a = 0; a < Rom16cWords; a++) rom[a] = BitConverter.ToUInt16(_dump, Rom16cAt + 2 * a);
        return rom;
    }

    /// <summary>The addresses where the unit's 16C ROM differs from another 16C ROM (Eric Smith's, 6144 words).</summary>
    public List<int> DifferencesFrom(IReadOnlyList<int> other16cRom) =>
        Enumerable.Range(0, Rom16cWords).Where(a => Own16cRom()[a] != other16cRom[a]).ToList();

    /// <summary>
    /// The image for the slot: the unit's own 16C ROM, the patch words laid over it (0000-17FF and 2000-37FF only), and,
    /// with keepSum, the checksum word 0x13FF set so that chip 0000-17FF sums (mod 1023) to what the unit's own ROM did.
    /// </summary>
    public SlotImage BuildSlot(IReadOnlyDictionary<int, int> patchWords, bool keepSum = true)
    {
        foreach (var (a, w) in patchWords)
        {
            if ((a >= 0x1800 && a < 0x2000) || a < 0 || a >= 0x3800) throw new ArgumentException($"patch word outside the two ROM ranges: {a:X4}");
            if (w < 0 || w > 0x3FF) throw new ArgumentException($"patch word {a:X4} is not 10 bits: {w:X}");
        }
        int n0 = patchWords.Keys.Count(a => a < 0x1800), n1 = patchWords.Keys.Count(a => a >= 0x2000);
        if (n0 < 20 || n1 < 5000) throw new ArgumentException($"this does not look like the 16C+ patch ({n0} words in 0000-17FF, {n1} in 2000-37FF)");
        var chip0 = Own16cRom();
        int unitSum = chip0.Sum() % Mod;
        foreach (var (a, w) in patchWords) if (a < 0x1800) chip0[a] = w;
        int withPatch = chip0.Sum() % Mod;
        int oldWord = chip0[ChecksumWord], newWord = oldWord;
        if (keepSum)
        {
            int adj = (((unitSum - withPatch) % Mod) + Mod) % Mod;
            newWord = (oldWord + adj) % Mod;
            chip0[ChecksumWord] = newWord;
            if (chip0.Sum() % Mod != unitSum) throw new InvalidOperationException("checksum adjustment failed");
        }
        var slot = new byte[SlotBytes];
        for (int a = 0; a < Rom16cWords; a++) { slot[2 * a] = (byte)(chip0[a] & 0xFF); slot[2 * a + 1] = (byte)(chip0[a] >> 8); }
        foreach (var (a, w) in patchWords)
            if (a >= 0x2000) { slot[2 * a] = (byte)(w & 0xFF); slot[2 * a + 1] = (byte)(w >> 8); }
        var pointer = BitConverter.GetBytes((uint)SlotAt);
        return new SlotImage(slot, pointer, unitSum, withPatch, oldWord, newWord, chip0.Sum() % Mod);
    }

    /// <summary>Read the "aaaa:www" lines of a patch (a note after the word is fine).</summary>
    public static Dictionary<int, int> ParsePatch(string text)
    {
        var words = new Dictionary<int, int>();
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line.TrimEnd('\r'), @"^([0-9a-fA-F]{4}):([0-9a-fA-F]{3})\b");
            if (m.Success) words[Convert.ToInt32(m.Groups[1].Value, 16)] = Convert.ToInt32(m.Groups[2].Value, 16);
        }
        return words;
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Nut.Core;

namespace HpSim;

/// <summary>
/// What the faceplate shows: legends per key, gold brackets, annunciator
/// names, colours. Built automatically from a model's chip file, built in
/// (16C), or loaded from a JSON file the user wrote (File > Save skin as
/// template gives a starting point).
/// </summary>
sealed class Skin
{
    public sealed record Key(int Code, string Main, string F = "", string G = "", int Rows = 1)
    {
        [JsonIgnore] public int Row => Code / 10 - 1;
        [JsonIgnore] public int Col => (Code % 10 + 9) % 10; // keycode x1..x9, x0 = column 1..10
    }

    public sealed record Bracket(int Row, int FirstCol, int LastCol, string Label);

    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Body { get; set; } = "#2B2A28";
    public string Bezel { get; set; } = "#9C8E6E";
    public string FColor { get; set; } = "#E0A83C";
    public string GColor { get; set; } = "#5FB4E6";
    public List<Key> Keys { get; set; } = [];
    public List<Bracket> Brackets { get; set; } = [];
    public string[] Annunciators { get; set; } =
        ["", "", "USER", "f", "g", "BEGIN", "G", "RAD", "D.MY", "C", "PRGM"];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static Skin FromJson(string text) =>
        JsonSerializer.Deserialize<Skin>(text, Json) ?? throw new InvalidDataException("empty skin file");

    /// <summary>Faceplate generated from the legends in a chip file.</summary>
    public static Skin FromLegends(string name, Dictionary<int, string[]> legends)
    {
        var skin = new Skin { Name = name, Label = name.Replace("HP-", "") };
        foreach (var (code, l) in legends.OrderBy(kv => kv.Key))
        {
            string P(int i) => i < l.Length ? Pretty(l[i]) : "";
            var main = P(0);
            skin.Keys.Add(new Key(code, main, P(1), P(2), main == "ENTER" ? 2 : 1));
        }
        return skin;
    }

    static readonly Dictionary<string, string> Glyphs = new()
    {
        ["divide"] = "÷", ["multiply"] = "×", ["minus"] = "−", ["plus"] = "+",
        ["-"] = "−", ["*"] = "×", ["/"] = "÷", ["."] = "·", ["dot"] = "·",
        ["sqrt"] = "√x", ["x^2"] = "x²", ["e^x"] = "eˣ", ["10^x"] = "10ˣ", ["y^x"] = "yˣ",
        ["pi"] = "π", ["x<>y"] = "x≷y", ["xexy"] = "x≷y", ["x<->y"] = "x≷y",
        ["RDN"] = "R↓", ["RUP"] = "R↑", ["Rdown"] = "R↓", ["Rup"] = "R↑",
        ["CLX"] = "CLx",
        ["sigma+"] = "Σ+", ["sigma-"] = "Σ−", ["Sigma+"] = "Σ+", ["Sigma-"] = "Σ−",
        ["12div"] = "12÷", ["12x"] = "12×",
        ["add"] = "+", ["subtract"] = "−", ["decimal"] = "·", ["backspace"] = "←",
        ["Rdn"] = "R↓", ["Rup"] = "R↑", ["Pi"] = "π", ["mean"] = "x̄", ["Delta%"] = "Δ%",
        ["est,r"] = "ŷ,r", ["std dev"] = "s", ["xexy"] = "x≷y",
        ["Re<>Im"] = "Re≷Im", ["lin est,r"] = "ŷ,r", ["x hat, r"] = "x̂,r", ["y hat, r"] = "ŷ,r",
        ["lin est x"] = "x̂,r", ["lin est y"] = "ŷ,r", ["weighted avg"] = "x̄w",
        ["INTEGRATE"] = "∫ᵧˣ", ["DeltaDYS"] = "ΔDYS",
    };

    /// <summary>Chip-file spelling to faceplate spelling (x<=y -> x≤y, ->H -> →H, ...).</summary>
    public static string Pretty(string s)
    {
        if (Glyphs.TryGetValue(s, out var g)) return g;
        return s.Replace("<=", "≤").Replace(">=", "≥").Replace("!=", "≠").Replace("<>", "≷")
                .Replace("->", "→").Replace("^-1", "⁻¹").Replace("-1", "⁻¹").Replace("sigma", "Σ")
                .Replace("Sigma", "Σ").Replace("divide", "÷").Replace("sqrt", "√");
    }

    /// <summary>The 16C faceplate as printed (legends and brackets checked by hand).</summary>
    public static Skin Builtin16c() => new()
    {
        Name = "HP-16C",
        Label = "16C",
        Keys =
        [
            new(11, "A", "SL", "LJ"), new(12, "B", "SR", "ASR"), new(13, "C", "RL", "RLC"),
            new(14, "D", "RR", "RRC"), new(15, "E", "RLn", "RLCn"), new(16, "F", "RRn", "RRCn"),
            new(17, "7", "MASKL", "#B"), new(18, "8", "MASKR", "ABS"), new(19, "9", "RMD", "DBLR"),
            new(10, "÷", "XOR", "DBL÷"),
            new(21, "GSB", "x≷(i)", "RTN"), new(22, "GTO", "x≷I", "LBL"), new(23, "HEX", "HEX", "DSZ"),
            new(24, "DEC", "DEC", "ISZ"), new(25, "OCT", "OCT", "√x"), new(26, "BIN", "BIN", "1/x"),
            new(27, "4", "SB", "SF"), new(28, "5", "CB", "CF"), new(29, "6", "B?", "F?"),
            new(20, "×", "AND", "DBL×"),
            new(31, "R/S", "(i)", "P/R"), new(32, "SST", "I", "BST"), new(33, "R↓", "PRGM", "R↑"),
            new(34, "x≷y", "REG", "PSE"), new(35, "BSP", "PREFIX", "CLx"),
            new(36, "ENTER", "WINDOW", "LSTx", Rows: 2),
            new(37, "1", "1'S", "x≤y"), new(38, "2", "2'S", "x<0"), new(39, "3", "UNSGN", "x>y"),
            new(30, "−", "NOT", "x>0"),
            new(41, "ON"), new(42, "f"), new(43, "g"), new(44, "STO", "WSIZE", "<"),
            new(45, "RCL", "FLOAT", ">"), new(47, "0", "MEM", "x≠y"), new(48, "·", "STATUS", "x≠0"),
            new(49, "CHS", "EEX", "x=y"), new(40, "+", "OR", "x=0"),
        ],
        Brackets = [new(1, 2, 5, "SHOW"), new(2, 2, 4, "CLEAR"), new(2, 6, 8, "SET COMPL")],
    };

    /// <summary>PC keyboard character -> keycode, from the key legends (digits sit
    /// on different keys on different models).</summary>
    public Dictionary<string, int> KeyboardMap()
    {
        var map = new Dictionary<string, int>();
        foreach (var k in Keys)
        {
            var m = k.Main;
            string? pc = m switch
            {
                "÷" => "/", "×" => "*", "−" => "-", "+" => "+", "·" => ".", "ENTER" => "ENTER",
                "ON" => "ON", "f" => "f", "g" => "g", "BSP" => "BSP", "←" => "BSP",
                _ => m.Length == 1 && (char.IsDigit(m[0]) || m[0] is >= 'A' and <= 'F') ? m : null,
            };
            if (pc is not null) map.TryAdd(pc, k.Code);
        }
        return map;
    }
}

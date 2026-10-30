using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace HpSim;

/// <summary>
/// Renders the subset of Markdown our guides use: headings, paragraphs,
/// bullet and numbered lists, **bold**, `code`, fenced code blocks, tables
/// and [links](url) (shown as their text). No packages, works offline.
/// </summary>
static class MarkdownView
{
    static readonly FontFamily Mono = new("Cascadia Mono,Consolas,Menlo,monospace");
    static readonly IBrush CodeBack = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
    static readonly IBrush CodeInk = new SolidColorBrush(Color.FromRgb(0xE6, 0xC0, 0x7B));
    static readonly IBrush Rule = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));

    public static Control Render(string markdown)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(28, 20), MaxWidth = 860 };
        var lines = markdown.Replace("\r", "").Split('\n');
        var para = new List<string>();

        void FlushPara()
        {
            if (para.Count == 0) return;
            panel.Children.Add(Text(string.Join(" ", para.Select(l => l.Trim())), 15));
            para.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var t = line.Trim();
            if (t.StartsWith("```"))
            {
                FlushPara();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```"); i++) code.Add(lines[i]);
                panel.Children.Add(new Border
                {
                    Background = CodeBack,
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 8),
                    Child = new SelectableTextBlock
                    {
                        Text = string.Join("\n", code), FontFamily = Mono, FontSize = 13, Foreground = CodeInk,
                    },
                });
                continue;
            }
            if (t.StartsWith('|'))
            {
                FlushPara();
                var rows = new List<string[]>();
                for (; i < lines.Length && lines[i].Trim().StartsWith('|'); i++)
                {
                    var cells = lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                    if (cells.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':'))) continue;
                    rows.Add(cells);
                }
                i--;
                panel.Children.Add(Table(rows));
                continue;
            }
            if (t.Length == 0) { FlushPara(); continue; }
            if (t.StartsWith("<!--")) { FlushPara(); continue; } // HTML comments are notes, not text
            if (t.StartsWith('#'))
            {
                FlushPara();
                int level = t.TakeWhile(c => c == '#').Count();
                var h = Text(t[level..].Trim(), level == 1 ? 28 : level == 2 ? 21 : 17);
                h.FontWeight = FontWeight.Bold;
                h.Margin = new Thickness(0, level == 1 ? 0 : 12, 0, 2);
                panel.Children.Add(h);
                if (level <= 2) panel.Children.Add(new Border { Height = 1, Background = Rule });
                continue;
            }
            var bullet = System.Text.RegularExpressions.Regex.Match(t, @"^(-|\*|\d+\.)\s+(.*)$");
            if (bullet.Success)
            {
                FlushPara();
                var item = new List<string> { bullet.Groups[2].Value };
                while (i + 1 < lines.Length && lines[i + 1].StartsWith("  ") && lines[i + 1].Trim().Length > 0
                       && !System.Text.RegularExpressions.Regex.IsMatch(lines[i + 1].Trim(), @"^(-|\*|\d+\.)\s"))
                    item.Add(lines[++i].Trim());
                var mark = bullet.Groups[1].Value is "-" or "*" ? "•" : bullet.Groups[1].Value;
                var row = new DockPanel { Margin = new Thickness(14, 0, 0, 0) };
                var m = new TextBlock { Text = mark, Width = 26, FontSize = 15 };
                DockPanel.SetDock(m, Dock.Left);
                row.Children.Add(m);
                row.Children.Add(Text(string.Join(" ", item), 15));
                panel.Children.Add(row);
                continue;
            }
            para.Add(line);
        }
        FlushPara();
        return new ScrollViewer { Content = panel };
    }

    static Control Table(List<string[]> rows)
    {
        int cols = rows.Max(r => r.Length);
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < rows[r].Length; c++)
            {
                var cell = Text(rows[r][c], 14);
                if (r == 0) cell.FontWeight = FontWeight.Bold;
                var b = new Border
                {
                    BorderBrush = Rule, BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 4, 24, 4), Child = cell,
                };
                Grid.SetRow(b, r);
                Grid.SetColumn(b, c);
                grid.Children.Add(b);
            }
        }
        return grid;
    }

    /// <summary>Paragraph text with **bold**, `code` and [text](link) inline.</summary>
    static SelectableTextBlock Text(string s, double size)
    {
        var tb = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, Inlines = [] };
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[([^\]]+)\]\(([^)]+)\)",
            m => m.Groups[2].Value.StartsWith("http") ? $"{m.Groups[1].Value} ({m.Groups[2].Value})" : m.Groups[1].Value);
        var parts = System.Text.RegularExpressions.Regex.Split(s, @"(\*\*[^*]+\*\*|`[^`]+`)");
        foreach (var p in parts)
        {
            if (p.Length == 0) continue;
            if (p.StartsWith("**") && p.EndsWith("**") && p.Length > 4)
                tb.Inlines!.Add(new Run(p[2..^2]) { FontWeight = FontWeight.Bold });
            else if (p.StartsWith('`') && p.EndsWith('`') && p.Length > 2)
                tb.Inlines!.Add(new Run(p[1..^1]) { FontFamily = Mono, Foreground = CodeInk, Background = CodeBack });
            else
                tb.Inlines!.Add(new Run(p));
        }
        return tb;
    }
}

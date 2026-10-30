using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
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

sealed class MainWindow : Window
{
    readonly CalculatorView _calc = new();
    readonly TextBlock _status = new()
    {
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
        FontSize = 12,
        Margin = new Thickness(10),
    };
    readonly Border _statusPanel;
    readonly ContentControl _main = new();
    readonly TextBlock _message = new() { Margin = new Thickness(10, 4), TextWrapping = TextWrapping.Wrap };
    readonly MenuItem _realSpeed = new() { Header = "_Real speed", ToggleType = MenuItemToggleType.CheckBox };
    readonly MenuItem _showStatus = new() { Header = "_Status panel", ToggleType = MenuItemToggleType.CheckBox, IsChecked = true };
    MachineRunner? _runner;
    Session? _session;
    Skin? _customSkin;
    Dictionary<string, int> _pcKeys = [];

    public MainWindow()
    {
        Title = "Palmarius Voyager simulator (HP-10C, 11C, 12C, 15C, 16C, 16C+)";
        Width = 1240;
        Height = 660;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x18));

        _statusPanel = new Border
        {
            Width = 330,
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
            BorderBrush = Brushes.DimGray,
            BorderThickness = new Thickness(1, 0, 0, 0),
            IsVisible = false,
        };
        var save = new Button { Content = "Save state...", HorizontalAlignment = HorizontalAlignment.Stretch };
        var load = new Button { Content = "Load state...", HorizontalAlignment = HorizontalAlignment.Stretch };
        save.Click += async (_, _) => await SaveState();
        load.Click += async (_, _) => await LoadState();
        var stateButtons = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,8,*"),
            Margin = new Thickness(10),
            Children = { save, load },
        };
        Grid.SetColumn(load, 2);
        var panel = new DockPanel();
        DockPanel.SetDock(stateButtons, Dock.Bottom);
        panel.Children.Add(stateButtons);
        panel.Children.Add(new ScrollViewer { Content = _status });
        _statusPanel.Child = panel;
        _showStatus.Click += (_, _) => _statusPanel.IsVisible = _showStatus.IsChecked && _runner is not null;
        _realSpeed.Click += (_, _) => { if (_runner is not null) _runner.RealSpeed = _realSpeed.IsChecked; };

        var calcMenu = new List<object> { Item("Choose a calculator...", ShowStart), new Separator() };
        foreach (var m in ModelCatalog.Models)
            calcMenu.Add(Item(m.Name, () => StartOriginal(m)));
        calcMenu.Add(Item("HP-16C+ (16C ROM + 16C+ patch)", Start16cPlus));
        calcMenu.AddRange([new Separator(), Item("Batteries out and in (reset)", () => _runner?.PowerOn()), _realSpeed]);

        var menu = new Menu
        {
            ItemsSource = new[]
            {
                new MenuItem { Header = "_Calculator", ItemsSource = calcMenu },
                new MenuItem
                {
                    Header = "_ROM",
                    ItemsSource = new object[]
                    {
                        Item("Apply a patch...", async () => await ApplyPatch()),
                        Item("Remove all patches", RemovePatches),
                        new Separator(),
                        Item("Load full ROM file (original or modified)...", async () => await LoadFullRom()),
                        Item("Export full ROM (Nonpareil .rom)...", async () => await Export(false)),
                        Item("Export full ROM (NutEm/PC .MClst)...", async () => await Export(true)),
                        Item("Save as my calculator...", async () => await SaveMyCalc()),
                        new Separator(),
                        Item("ROM information", () => { if (_session is not null) ShowText("ROM information", _session.Info()); }),
                        Item("Open the ROM folder", () => OpenFolder(RomStore.Dir)),
                    },
                },
                new MenuItem
                {
                    Header = "_Skin",
                    ItemsSource = new object[]
                    {
                        Item("Automatic (from the calculator's chip file)", () => { _customSkin = null; ApplySkin(); }),
                        Item("Load skin file (.json)...", async () => await LoadSkin()),
                        Item("Save current skin as a template (.json)...", async () => await SaveSkin()),
                    },
                },
                new MenuItem { Header = "_View", ItemsSource = new object[] { _showStatus } },
                new MenuItem
                {
                    Header = "_Help",
                    ItemsSource = new object[]
                    {
                        Item("User guide", () => ShowDoc("User guide", "USER-GUIDE.md")),
                        Item("How to write a patch", () => ShowDoc("How to write a patch", "PATCHING.md")),
                        Item("Keyboard shortcuts", () => ShowText("Keyboard shortcuts", Shortcuts)),
                        Item("About", () => ShowText("About", About)),
                    },
                },
            },
        };

        var body = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(_message, Dock.Bottom);
        DockPanel.SetDock(_statusPanel, Dock.Right);
        body.Children.Add(menu);
        body.Children.Add(_message);
        body.Children.Add(_statusPanel);
        body.Children.Add(_main);
        Content = body;

        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnTextInput, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyUp += (_, _) => _calc.ReleaseFromKeyboard();
        Closed += (_, _) => _runner?.Dispose();
        DispatcherTimer.Run(() => { Tick(); return true; }, TimeSpan.FromMilliseconds(40));
        ShowStart();
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--model");
        if (i > 0 && i + 1 < args.Length)
        {
            var want = args[i + 1];
            var mine = RomStore.MyCalcs().FirstOrDefault(c =>
                string.Equals(c.Id, want, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, want, StringComparison.OrdinalIgnoreCase));
            if (mine is not null) StartMy(mine);
            else if (want == "16c+") Start16cPlus();
            else if (ModelCatalog.Models.FirstOrDefault(m => m.Id == want) is { } m) StartOriginal(m);
            else Say($"--model {want}: no such calculator (10c, 11c, 12c, 15c, 16c, 16c+ or one of yours).");
        }
    }

    const string Shortcuts =
        "Digits 0-9, A-F (16C), Enter, + - * /, Backspace (BSP or the key with the arrow),\n" +
        "period = decimal point, F1 = f, F2 = g, Esc = ON.\n\n" +
        "Hold a key with the mouse to hold it on the calculator (e.g. STATUS on the 16C).\n" +
        "ON + key chords: press and hold ON with the mouse, then press the other key on the keyboard.";

    const string About =
        "Palmarius Voyager simulator (github.com/palmarius-com/hp16c-plus), GPLv3.\n\n" +
        "Voyager simulator: a C# port of a Nut CPU emulator, following Eric Smith's\n" +
        "Nonpareil (GPLv3). It runs the original firmware of the HP-10C, 11C, 12C, 15C\n" +
        "and 16C, which is NOT included: you load it from Nonpareil or from your own file.\n" +
        "Downloads come from https://github.com/brouhaha/nonpareil (ncd/<model>/<model>.rom).\n\n" +
        "Chip definitions (keyboard and display maps): Nonpareil, Copyright Eric Smith,\n" +
        "GPLv3. HP is a trademark of HP Inc.; this project is not affiliated with HP.\n" +
        "Exported ROM files contain HP's firmware: keep them for your own use.\n\n" +
        "ROM folder: ";

    static MenuItem Item(string header, Action action)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => action();
        return m;
    }

    static MenuItem Item(string header, Func<Task> action)
    {
        var m = new MenuItem { Header = header };
        m.Click += async (_, _) => await action();
        return m;
    }

    void Tick()
    {
        if (_runner is null) return;
        _calc.Refresh();
        if (_statusPanel.IsVisible) _status.Text = _runner.Take().Status;
    }

    bool CalcActive => _runner is not null && _main.Content == _calc;

    // Keys without a character: by key code.
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!CalcActive) return;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0) return;
        string? pc = e.Key switch
        {
            Key.Enter => "ENTER", Key.Back => "BSP", Key.Escape => "ON", Key.F1 => "f", Key.F2 => "g",
            _ => null,
        };
        if (pc is not null && Press(pc)) e.Handled = true;
    }

    // Characters (digits, A-F, + - * / .): by the character typed, so every
    // keyboard layout works (on a German keyboard * is Shift and +).
    void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (!CalcActive || string.IsNullOrEmpty(e.Text)) return;
        var ch = char.ToUpperInvariant(e.Text[0]);
        string pc = ch switch { ',' => ".", 'X' => "*", ':' => "/", _ => ch.ToString() };
        if (Press(pc)) e.Handled = true;
    }

    bool Press(string pc)
    {
        if (pc == "BSP" && !_pcKeys.ContainsKey("BSP")) pc = "←";
        if (!_pcKeys.TryGetValue(pc, out var code)) return false;
        _calc.PressFromKeyboard(code);
        return true;
    }

    // ------------------------------------------------------- start page
    void ShowStart()
    {
        StopMachine();
        var list = new StackPanel { Spacing = 8 };
        foreach (var m in ModelCatalog.Models)
        {
            var model = m;
            bool has = RomStore.Has(m.Id);
            var start = new Button { Content = "Start", IsEnabled = has, MinWidth = 70 };
            start.Click += (_, _) => StartOriginal(model);
            var dl = new Button { Content = has ? "Download again" : "Get ROM from Nonpareil" };
            dl.Click += async (_, _) => { await Download(model); };
            var row = new DockPanel { LastChildFill = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { start, dl } };
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Inlines =
                {
                    new Avalonia.Controls.Documents.Run(m.Name) { FontWeight = FontWeight.Bold, FontSize = 16 },
                    new Avalonia.Controls.Documents.Run($"   {m.Description}   "),
                    new Avalonia.Controls.Documents.Run(has ? "ROM ready" : "no ROM yet")
                        { Foreground = has ? Brushes.LightGreen : Brushes.Orange },
                },
            });
            list.Children.Add(new Border
            {
                Padding = new Thickness(12, 8),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)),
                Child = row,
            });
        }
        var mine = new StackPanel { Spacing = 8 };
        foreach (var c in RomStore.MyCalcs())
        {
            var calc = c;
            var start = new Button { Content = "Start", MinWidth = 70 };
            start.Click += (_, _) => StartMy(calc);
            var del = new Button { Content = "Delete" };
            del.Click += async (_, _) =>
            {
                if (await Confirm($"Delete your calculator \"{calc.Name}\"? Its saved ROM is removed; " +
                                  "the original ROMs stay.")) { RomStore.DeleteMyCalc(calc); ShowStart(); }
            };
            var row = new DockPanel();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { start, del } };
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Inlines =
                {
                    new Avalonia.Controls.Documents.Run(c.Name) { FontWeight = FontWeight.Bold, FontSize = 16 },
                    new Avalonia.Controls.Documents.Run($"   {ModelCatalog.Get(c.Model).Name} keyboard" +
                        (c.Description.Length > 0 ? $"   {c.Description}" : "")) { Foreground = Brushes.Silver },
                },
            });
            mine.Children.Add(new Border
            {
                Padding = new Thickness(12, 8),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(0x20, 0x2A, 0x24)),
                Child = row,
            });
        }

        var open = new Button { Content = "Open an original ROM file..." };
        open.Click += async (_, _) => await OpenOriginal();
        var full = new Button { Content = "Load a full (modified) ROM file..." };
        full.Click += async (_, _) => await LoadFullRom();
        var plus = new Button { Content = "Start the HP-16C+" };
        plus.Click += (_, _) => Start16cPlus();
        var guide = new Button { Content = "User guide" };
        guide.Click += (_, _) => ShowDoc("User guide", "USER-GUIDE.md");

        _main.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(36, 24),
                Spacing = 14,
                MaxWidth = 820,
                HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    new TextBlock { Text = "Choose a calculator", FontSize = 24, FontWeight = FontWeight.Bold },
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = "This simulator runs the calculators' real firmware. The firmware belongs to HP " +
                               "and is not included. Get it from Eric Smith's Nonpareil project with one click " +
                               "(the app downloads it straight from https://github.com/brouhaha/nonpareil, folder ncd/), " +
                               "or open a ROM file you already have. " +
                               "The app checks that the file is an original ROM and keeps a copy for you.",
                    },
                    list,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { open, full, plus, guide } },
                    new TextBlock { Text = "Your calculators", FontSize = 20, FontWeight = FontWeight.Bold,
                                    Margin = new Thickness(0, 10, 0, 0) },
                    mine.Children.Count > 0 ? mine : new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Silver,
                        Text = "None yet. Start a calculator, apply a patch (or load a full ROM), then " +
                               "ROM > Save as my calculator. It appears here with its own Start button.",
                    },
                },
            },
        };
        Say("");
    }

    async Task Download(ModelCatalog.Model m)
    {
        try
        {
            Say($"Downloading the {m.Name} ROM from github.com/brouhaha/nonpareil ...");
            await RomStore.Download(m);
            Say($"{m.Name} ROM checked (original) and saved.");
            ShowStart();
        }
        catch (Exception ex) { Say($"Download failed: {ex.Message}"); }
    }

    async Task<(string Name, string[] Lines)?> PickText(string title, params string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = [.. patterns, "*"] }],
        });
        if (files.Count == 0) return null;
        await using var s = await files[0].OpenReadAsync();
        using var reader = new StreamReader(s);
        return (files[0].Name, (await reader.ReadToEndAsync()).Split('\n'));
    }

    async Task OpenOriginal()
    {
        var f = await PickText("Original ROM (Nonpareil .rom)", "*.rom", "*.txt");
        if (f is null) return;
        try
        {
            var m = RomStore.Import(f.Value.Lines);
            Say($"{m.Name} ROM checked (original) and saved.");
            ShowStart();
        }
        catch (Exception ex) { Say(ex.Message); }
    }

    // ----------------------------------------------------------- machine
    void StartOriginal(ModelCatalog.Model m)
    {
        var rom = RomStore.Load(m.Id);
        if (rom is null)
        {
            Say($"No {m.Name} ROM yet: get it on the start page (Calculator > Choose a calculator).");
            ShowStart();
            return;
        }
        Run(new Session { Model = m, Base = rom, BaseLabel = $"original {m.Name} (Nonpareil)" });
    }

    void Start16cPlus()
    {
        var m = ModelCatalog.Get("16c");
        var rom = RomStore.Load("16c");
        if (rom is null) { Say("The 16C+ needs the HP-16C ROM first: get it on the start page."); ShowStart(); return; }
        var patch = RomStore.FindRepoPatch();
        var s = new Session { Model = m, Base = rom, BaseLabel = "original HP-16C (Nonpareil)" };
        if (patch is null)
        {
            Run(s);
            Say("16C running. Now ROM > Apply a patch... and choose the 16C+ patch file (16cplus.patch).");
            return;
        }
        s.Patches.Add((Path.GetFileName(patch), RomImage.Load(patch)));
        Run(s);
    }

    void Run(Session s)
    {
        RomImage img;
        Voyager.Ncd chips;
        bool large;
        try
        {
            img = s.Image();
            (chips, large) = s.ChipMap(img);
        }
        catch (Exception ex) { Say(ex.Message); return; }
        StopMachine();
        var ncd = Voyager.EmbeddedNcd(s.Model.Id);
        var chipArg = large ? chips : null;
        Voyager calc = s.Model.Id == "16c" ? new Hp16c(ncd, chipArg, img) : new Voyager(ncd, chipArg, img);
        string title = (s.Name ?? s.Model.Name) + (s.Patches.Count > 0 ? " + " + string.Join(" + ", s.Patches.Select(p => p.Name)) : "");
        _session = s;
        _runner = new MachineRunner(calc, title) { RealSpeed = _realSpeed.IsChecked };
        _calc.Runner = _runner;
        ApplySkin();
        _main.Content = _calc;
        _statusPanel.IsVisible = _showStatus.IsChecked;
        _calc.Focus();
        var bad = s.Info().Split('\n').Where(l => l.Contains("checksum off")).ToList();
        Say($"{title}: press ON (Esc) to switch on." +
            (large ? " Using the large 15C chip map for the extra ROM." : "") +
            (bad.Count > 0 ? " Note: a ROM checksum is off (see ROM > ROM information)." : ""));
    }

    void StopMachine()
    {
        _runner?.Dispose();
        _runner = null;
        _calc.Runner = null;
        _statusPanel.IsVisible = false;
    }

    void ApplySkin()
    {
        if (_session is null) return;
        var skin = _customSkin ?? (_session.Model.Id == "16c"
            ? Skin.Builtin16c()
            : Skin.FromLegends(_session.Model.Name, Voyager.EmbeddedNcd(_session.Model.Id).Legends));
        if (_session.Patches.Count > 0 && _customSkin is null && _session.Model.Id == "16c" &&
            _session.Patches.Any(p => p.Name.StartsWith("16cplus")))
            skin.Label = "16C+";
        if (_customSkin is null && !string.IsNullOrWhiteSpace(_session.Label)) skin.Label = _session.Label;
        _calc.Skin = skin;
        _pcKeys = skin.KeyboardMap();
    }

    // -------------------------------------------------------------- ROM
    async Task ApplyPatch()
    {
        if (_session is null) { Say("Start a calculator first, then apply the patch to it."); return; }
        var f = await PickText("Patch file (aaaa:www lines)", "*.patch", "*.obj", "*.txt");
        if (f is null) return;
        var patch = RomImage.Parse(f.Value.Lines);
        if (patch.Words.Count == 0)
        {
            Say($"{f.Value.Name} has no patch lines. A patch line looks like 01f4:399 (address:word, hex).");
            return;
        }
        if (patch.Words.Any(kv => kv.Key > 0xFFFF || kv.Value > 0x3FF))
        {
            Say("A word in the patch is larger than 3FF (Nut words have 10 bits).");
            return;
        }
        _session.Patches.Add((f.Value.Name, patch));
        Run(_session);
    }

    void RemovePatches()
    {
        if (_session is null) return;
        _session.Patches.Clear();
        Run(_session);
    }

    async Task LoadFullRom()
    {
        var f = await PickText("Full ROM (Nonpareil .rom or aaaa:www lines)", "*.rom", "*.txt");
        if (f is null) return;
        var img = RomImage.Parse(f.Value.Lines);
        if (img.Words.Count == 0) { Say($"{f.Value.Name} has no ROM lines (aaaa:www)."); return; }
        var original = ModelCatalog.Identify(img);
        var model = original ?? await AskModel(f.Value.Name);
        if (model is null) return;
        Run(new Session
        {
            Model = model,
            Base = img,
            BaseLabel = original is not null ? $"original {model.Name} ({f.Value.Name})" : $"modified ROM {f.Value.Name}",
        });
    }

    async Task<ModelCatalog.Model?> AskModel(string file)
    {
        var combo = new ComboBox
        {
            ItemsSource = ModelCatalog.Models.Select(m => m.Name).ToList(),
            SelectedIndex = _session is null ? 4 : Array.IndexOf(ModelCatalog.Models, _session.Model),
            MinWidth = 160,
        };
        var ok = new Button { Content = "Run", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dlg = new Window
        {
            Title = "Which calculator?",
            Width = 460, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 12,
                Children =
                {
                    new TextBlock { TextWrapping = TextWrapping.Wrap,
                        Text = $"{file} is not an original ROM. Which calculator's keyboard and display should run it?" },
                    combo,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
                },
            },
        };
        ModelCatalog.Model? result = null;
        ok.Click += (_, _) => { result = ModelCatalog.Models[Math.Max(0, combo.SelectedIndex)]; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    async Task Export(bool mclst)
    {
        if (_session is null) { Say("Start a calculator first."); return; }
        var img = _session.Image();
        var name = _session.Model.Id + (_session.Patches.Count > 0 ? "-patched" : "");
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export the full ROM (contains HP's firmware: for your own use)",
            SuggestedFileName = name + (mclst ? ".MClst" : ".rom"),
        });
        if (file is null) return;
        await using var s = await file.OpenWriteAsync();
        await using var w = new StreamWriter(s);
        int size = (img.Words.Keys.Max() / 0x400 + 1) * 0x400;
        await w.WriteAsync(mclst ? img.ToMClst(size) : img.ToNonpareilText($"{_session.Model.Name} ROM: {_session.BaseLabel}" +
            string.Concat(_session.Patches.Select(p => $" + {p.Name}"))));
        Say($"Exported {file.Name}. Load it again with ROM > Load full ROM file. It contains HP's firmware: keep it for your own use.");
    }

    // -------------------------------------------------- your calculators
    void StartMy(RomStore.MyCalc c)
    {
        try
        {
            Run(new Session
            {
                Model = ModelCatalog.Get(c.Model),
                Base = RomStore.LoadMyRom(c),
                BaseLabel = $"your calculator {c.Name} (saved {c.Created})",
                Label = c.Label,
                Name = c.Name,
            });
        }
        catch (Exception ex) { Say($"{c.Name} could not start: {ex.Message}"); }
    }

    async Task SaveMyCalc()
    {
        if (_session is null) { Say("Start a calculator first (and apply your patches)."); return; }
        bool plus = _session.Patches.Any(p => p.Name.StartsWith("16cplus"));
        var name = new TextBox
        {
            Text = plus ? "HP-16C+" : _session.Model.Name + (_session.Patches.Count > 0 ? " (patched)" : " (mine)"),
        };
        var label = new TextBox { Text = _calc.Skin.Label, MaxLength = 10 };
        var desc = new TextBox
        {
            Text = string.Join(" + ", _session.Patches.Select(p => p.Name)),
            Watermark = "optional: what is special about it",
        };
        var ok = new Button { Content = "Save", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dlg = new Window
        {
            Title = "Save as my calculator",
            Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 8,
                Children =
                {
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
                        $"Keeps the ROM that is running now ({_session.BaseLabel}" +
                        string.Concat(_session.Patches.Select(p => " + " + p.Name)) +
                        $") with the {_session.Model.Name} keyboard and display. It then has its own " +
                        "Start button on the start page." },
                    new TextBlock { Text = "Name" }, name,
                    new TextBlock { Text = "Label on the faceplate (short)" }, label,
                    new TextBlock { Text = "Description" }, desc,
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange, Text =
                        "The saved ROM contains HP's firmware: it stays on this computer, for your own use. " +
                        "Share the patch instead." },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
                },
            },
        };
        bool save = false;
        ok.Click += (_, _) => { save = !string.IsNullOrWhiteSpace(name.Text); dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        if (!save) return;
        var c = RomStore.SaveMyCalc(name.Text!.Trim(), _session.Model.Id, label.Text?.Trim() ?? "",
                                    desc.Text?.Trim() ?? "", _session.Image());
        Say($"Saved \"{c.Name}\". It is on the start page now; launch it directly with --model {c.Id}.");
    }

    async Task<bool> Confirm(string question)
    {
        var yes = new Button { Content = "Yes" };
        var no = new Button { Content = "No", IsDefault = true, IsCancel = true };
        var dlg = new Window
        {
            Title = "Please confirm",
            Width = 440, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right, Children = { no, yes } },
                },
            },
        };
        bool result = false;
        yes.Click += (_, _) => { result = true; dlg.Close(); };
        no.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    // ------------------------------------------------------------- state
    async Task SaveState()
    {
        if (_runner is null || _session is null) return;
        var st = _runner.SaveState();
        st.Calculator = _session.Model.Name;
        st.Model = _session.Model.Id;
        st.Rom = _session.BaseLabel;
        st.RomFingerprint = _session.Image().CanonicalSha256();
        st.Patches = _session.Patches.Select(p => p.Name).ToList();
        st.Note = "Voyager simulator machine state. Registers: hex digits, most significant first. " +
                  "ram: register address -> 14 digits. Load it with the same calculator, ROM and patches.";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the calculator's state",
            SuggestedFileName = $"{_session.Model.Id}-state-{DateTime.Now:yyyyMMdd-HHmm}.json",
        });
        if (file is null) return;
        await using var s = await file.OpenWriteAsync();
        await using var w = new StreamWriter(s);
        await w.WriteAsync(st.ToJson());
        Say($"State saved to {file.Name} (display: '{st.DisplayText.Trim()}').");
    }

    async Task LoadState()
    {
        if (_runner is null || _session is null) return;
        var f = await PickText("Calculator state (.json)", "*.json");
        if (f is null) return;
        try
        {
            var st = MachineState.FromJson(string.Join('\n', f.Value.Lines));
            if (st.Model != _session.Model.Id)
                throw new InvalidDataException($"this state is from the {st.Calculator}; start that calculator first");
            var fp = _session.Image().CanonicalSha256();
            if (st.RomFingerprint != fp)
                throw new InvalidDataException("this state was saved with a different ROM or other patches (" +
                    $"{st.Rom}{string.Concat(st.Patches.Select(p => " + " + p))}); load the same ones first");
            _runner.LoadState(st);
            Say($"State loaded from {f.Value.Name} (saved {st.SavedAt}).");
        }
        catch (Exception ex) { Say("State not loaded: " + ex.Message); }
    }

    // ------------------------------------------------------------- skins
    async Task LoadSkin()
    {
        var f = await PickText("Skin file (.json)", "*.json");
        if (f is null) return;
        try
        {
            _customSkin = Skin.FromJson(string.Join('\n', f.Value.Lines));
            ApplySkin();
            Say($"Skin {f.Value.Name} loaded. Skin > Automatic goes back.");
        }
        catch (Exception ex) { Say("Skin not loaded: " + ex.Message); }
    }

    async Task SaveSkin()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the current skin as a template",
            SuggestedFileName = $"skin-{_session?.Model.Id ?? "16c"}.json",
        });
        if (file is null) return;
        await using var s = await file.OpenWriteAsync();
        await using var w = new StreamWriter(s);
        await w.WriteAsync(_calc.Skin.ToJson());
        Say($"Saved {file.Name}: edit legends and colours, then Skin > Load skin file.");
    }

    // -------------------------------------------------------------- help
    void ShowDoc(string title, string resource)
    {
        using var s = typeof(MainWindow).Assembly.GetManifestResourceStream("Docs." + resource);
        if (s is null) { ShowText(title, $"{resource} is missing in this build."); return; }
        new Window
        {
            Title = title,
            Width = 900, Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
            Content = MarkdownView.Render(new StreamReader(s).ReadToEnd()),
        }.Show(this);
    }

    void ShowText(string title, string text)
    {
        if (title == "About") text += RomStore.Dir;
        new Window
        {
            Title = title,
            Width = 820, Height = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                Content = new SelectableTextBlock
                {
                    Text = text,
                    Margin = new Thickness(18),
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
                    FontSize = 13,
                },
            },
        }.Show(this);
    }

    static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch { /* no file manager: the path is shown in Help > About */ }
    }

    void Say(string text) => _message.Text = text;
}

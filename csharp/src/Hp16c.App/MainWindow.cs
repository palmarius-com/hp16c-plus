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

sealed partial class MainWindow : Window
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
    readonly ComboBox _view = new()
    {
        ItemsSource = new[] { "Word size", "64 bits", "128 bits" },
        SelectedIndex = 0,
        MinWidth = 120,
        IsVisible = false,
    };
    bool _userView, _settingView; // once the user chose, detection leaves the view alone
    readonly MenuItem _chipsOwn = new() { Header = "The calculator's own chips", ToggleType = MenuItemToggleType.Radio, GroupName = "chips" };
    readonly MenuItem _chipsLarge = new() { Header = "Large (15C chips)", ToggleType = MenuItemToggleType.Radio, GroupName = "chips" };
    MachineRunner? _runner;
    Session? _session;
    bool _closeNow; // the user answered, or the API said quit
    readonly Menu _menu = new();
    string? _pickedPath;
    readonly System.Collections.ObjectModel.ObservableCollection<object> _calcItems = [];
    int _myInsertAt, _myCount;
    Dictionary<string, int> _pcKeys = [];

    public MainWindow()
    {
        Title = "Palmarius Voyager simulator (HP-10C, 11C, 12C, 15C, 16C + custom) Version " +
                typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
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
        _view.SelectionChanged += (_, _) =>
        {
            if (_runner is not null) _runner.View = (PanelView)Math.Max(0, _view.SelectedIndex);
            if (!_settingView) _userView = true;
        };
        var viewRow = new DockPanel { Margin = new Thickness(10, 8, 10, 0), Children = { _view } };
        DockPanel.SetDock(_view, Dock.Right);
        var panel = new DockPanel();
        DockPanel.SetDock(stateButtons, Dock.Bottom);
        DockPanel.SetDock(viewRow, Dock.Top);
        panel.Children.Add(stateButtons);
        panel.Children.Add(viewRow);
        panel.Children.Add(new ScrollViewer { Content = _status });
        _statusPanel.Child = panel;
        _chipsOwn.Click += (_, _) => SetChips(false);
        _chipsLarge.Click += (_, _) => SetChips(true);
        _showStatus.Click += (_, _) => _statusPanel.IsVisible = _showStatus.IsChecked && _runner is not null;
        _realSpeed.Click += (_, _) => { if (_runner is not null) _runner.RealSpeed = _realSpeed.IsChecked; };

        var calcMenu = _calcItems;
        foreach (var head in new object[]
        {
            Item("Choose a calculator...", async () => { if (await ConfirmLeave()) ShowStart(); }),
            new Separator(),
        }) calcMenu.Add(head);
        foreach (var m in ModelCatalog.Models)
            calcMenu.Add(Item(m.Name, async () => { if (await ConfirmLeave()) StartOriginal(m); }));
        _myInsertAt = calcMenu.Count;
        foreach (var tail in new object[] { new Separator(), Item("Save as my calculator...", async () => await SaveMyCalc()),
                                             new Separator(), Item("Batteries out and in (reset)", () => _runner?.PowerOn()), _realSpeed })
            calcMenu.Add(tail);
        RefreshMyMenu();

        var menu = _menu;
        menu.ItemsSource =             new[]
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
                        Item("Export patch...", async () => await ExportPatch()),
                        new Separator(),
                        new MenuItem { Header = "Chip layout", ItemsSource = new object[] { _chipsOwn, _chipsLarge } },
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
                        Item("Automatic (from the calculator's chip file)", () => SetSkin(null, null)),
                        Item("Load skin file (.json)...", async () => await LoadSkin()),
                        Item("Save current skin as a template (.json)...", async () => await SaveSkin()),
                        new Separator(),
                        Item("Faceplate label...", async () => await AskLabel()),
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
                        Item("Test window...", ShowTests),
                        Item("How to write a test", () => ShowDoc("How to write a test", "TESTS.md")),
                        Item("Keyboard shortcuts", () => ShowText("Keyboard shortcuts", Shortcuts)),
                        Item("About", () => ShowText("About", About)),
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
        Closing += (_, e) =>
        {
            if (_closeNow || (_session?.Changes().Count ?? 0) == 0) return;
            e.Cancel = true;
            Dispatcher.UIThread.Post(async () =>
            {
                if (!await ConfirmLeave()) return;
                _closeNow = true;
                Close();
            });
        };
        DispatcherTimer.Run(() => { Tick(); return true; }, TimeSpan.FromMilliseconds(40));
        ShowStart();
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--model");
        if (i > 0 && i + 1 < args.Length && !StartByName(args[i + 1]))
            Say($"--model {args[i + 1]}: no such calculator (10c, 11c, 12c, 15c, 16c or one of yours).");
        int a = Array.IndexOf(args, "--api");
        if (a > 0)
        {
            var name = a + 1 < args.Length && !args[a + 1].StartsWith("--") ? args[a + 1] : ApiServer.DefaultPipe;
            new ApiServer(name, ApiCommand).Start();
        }
    }

    /// <summary>Your calculators in the Calculator menu, under the originals.</summary>
    void RefreshMyMenu()
    {
        for (; _myCount > 0; _myCount--) _calcItems.RemoveAt(_myInsertAt);
        var mine = RomStore.MyCalcs();
        if (mine.Count == 0) return;
        var items = new List<object> { new Separator() };
        foreach (var c in mine)
        {
            var calc = c;
            items.Add(Item(c.Name, async () => { if (await ConfirmLeave()) StartMy(calc); }));
        }
        foreach (var item in items) _calcItems.Insert(_myInsertAt + _myCount++, item);
    }

    /// <summary>Start a model id or one of your calculators by name or id.</summary>
    bool StartByName(string want)
    {
        var mine = RomStore.MyCalcs().FirstOrDefault(c =>
            string.Equals(c.Id, want, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Name, want, StringComparison.OrdinalIgnoreCase));
        if (mine is not null) StartMy(mine);
        else if (ModelCatalog.Models.FirstOrDefault(m => m.Id == want) is { } m) StartOriginal(m);
        else return false;
        return true;
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
        _session = null;
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
        RefreshMyMenu();
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
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { open, full, guide } },
                    new TextBlock { Text = "Your calculators", FontSize = 20, FontWeight = FontWeight.Bold,
                                    Margin = new Thickness(0, 10, 0, 0) },
                    mine.Children.Count > 0 ? mine : new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Silver,
                        Text = "None yet. Start a calculator, apply a patch (or load a full ROM), then " +
                               "Calculator > Save as my calculator. It appears here with its own Start button.",
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
        _pickedPath = files[0].TryGetLocalPath();
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
        Run(new Session { Model = m, Base = rom, BaseLabel = $"original {m.Name} (Nonpareil)",
                          RomFile = RomStore.OriginalFile(m.Id) });
    }

    void SetChips(bool large)
    {
        if (_session is null) { Say("Start a calculator first."); SyncChips(false); return; }
        _session.UserLarge = large;
        Run(_session);
    }

    void SyncChips(bool large)
    {
        _chipsOwn.IsChecked = !large;
        _chipsLarge.IsChecked = large;
    }

    void SetLabel(string? label)
    {
        if (_session is null) return;
        _session.UserLabel = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        ApplySkin();
    }

    async Task AskLabel()
    {
        if (_session is null) { Say("Start a calculator first."); return; }
        var box = new TextBox { Text = _calc.Skin.Label, MaxLength = 10, Watermark = "empty = automatic" };
        var img = _session.Image();
        var hint = ModelCatalog.KnownBuild(img) is { } known
            ? $"Text under the blue square, free to choose. This ROM is the known {known} build, so {known} is the suggestion" +
              (_session.SuggestedLabel(img) is null ? " once it runs on the large chip layout." : ".")
            : "Text under the blue square, free to choose.";
        var ok = new Button { Content = "Set", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var dlg = new Window
        {
            Title = "Faceplate label",
            Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 10,
                Children =
                {
                    new TextBlock { Text = hint + " Empty goes back to the automatic label.",
                                    TextWrapping = TextWrapping.Wrap },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
                },
            },
        };
        ok.Click += (_, _) => { SetLabel(box.Text); dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
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
        s.MarkStart();
        Voyager calc = s.CreateMachine(img, chips, large);
        string title = (s.Name ?? s.Model.Name) + (s.Patches.Count > 0 ? " + " + string.Join(" + ", s.Patches.Select(p => p.Name)) : "");
        _session = s;
        SyncChips(large);
        _runner = new MachineRunner(calc, title) { RealSpeed = _realSpeed.IsChecked };
        _view.IsVisible = calc is Hp16c;
        bool preset = !_userView && calc is Hp16c { HasExtensionRam: true };
        if (!_userView)
        {
            _settingView = true;
            _view.SelectedIndex = (int)(preset ? PanelView.Bits128 : PanelView.WordSize);
            _settingView = false;
        }
        _runner.View = (PanelView)Math.Max(0, _view.SelectedIndex);
        _calc.Runner = _runner;
        ApplySkin();
        _main.Content = _calc;
        _statusPanel.IsVisible = _showStatus.IsChecked;
        _calc.Focus();
        var bad = s.Info().Split('\n').Where(l => l.Contains("checksum off")).ToList();
        var known = ModelCatalog.KnownBuild(img);
        Say($"{title}: press ON (Esc) to switch on." +
            (large && s.UserLarge is null ? " Large chip layout preset, the ROM needs it (ROM > Chip layout)." : "") +
            (s.NotLoaded(img, large) is { } missing ? " " + missing : "") +
            (known is not null && s.UserLabel is null
                ? (large ? $" Recognised as {known} (Skin > Faceplate label changes the label)."
                         : $" This is the known {known} build, it needs the large chip layout.") : "") +
            (preset ? " Status panel set to 128 bits; change it with the dropdown." : "") +
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
        bool own = _session.SkinJson is not null;
        var skin = own ? Skin.FromJson(_session.SkinJson!) : _session.Model.Id == "16c"
            ? Skin.Builtin16c()
            : Skin.FromLegends(_session.Model.Name, Voyager.EmbeddedNcd(_session.Model.Id).Legends);
        var label = _session.FaceLabel(_session.Image());
        if (_session.UserLabel is not null || (!own && label is not null)) skin.Label = label!;
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
        if (model is null || !await ConfirmLeave()) return;
        Run(new Session
        {
            Model = model,
            Base = img,
            BaseLabel = original is not null ? $"original {model.Name} ({f.Value.Name})" : $"modified ROM {f.Value.Name}",
            RomFile = _pickedPath,
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

    string? PatchText()
    {
        var original = _session is null ? null : RomStore.Load(_session.Model.Id);
        if (original is null) return null;
        var img = _session!.Image();
        if (img.CanonicalSha256() == original.CanonicalSha256()) return null;
        return img.ToPatchText(original, $"Patch for the {_session.Model.Name}: {_session.Name ?? string.Join(" + ", _session.Patches.Select(p => p.Name))}");
    }

    async Task ExportPatch()
    {
        if (_session is null) { Say("Start a calculator first."); return; }
        var text = PatchText();
        if (text is null) { Say("Nothing to export: this is the original ROM (or its original is not loaded)."); return; }
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export patch (only your changes)",
            SuggestedFileName = RomStore.Slug(_session.Name ?? _session.Model.Id + "-patch") + ".patch",
        });
        if (file is null) return;
        await using var s = await file.OpenWriteAsync();
        await using var w = new StreamWriter(s);
        await w.WriteAsync(text);
        Say($"Exported {file.Name}: only the words that differ from the original ROM. Safe to share.");
    }

    async Task<bool> WarnFullRom()
    {
        var go = new Button { Content = "Export" };
        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true };
        var dlg = new Window
        {
            Title = "This file contains HP's firmware",
            Width = 500, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 10,
                Children =
                {
                    new TextBlock { TextWrapping = TextWrapping.Wrap,
                        Text = "A full ROM contains HP's firmware. Keep it for your own use and do not publish " +
                               "it. To share your work, use ROM > Export patch: it holds only your changes." },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, go } },
                },
            },
        };
        bool result = false;
        go.Click += (_, _) => { result = true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    async Task Export(bool mclst)
    {
        if (_session is null) { Say("Start a calculator first."); return; }
        if (!await WarnFullRom()) return;
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
                RomFile = RomStore.MyRomFile(c),
                Label = c.Label,
                Name = c.Name,
                ShortName = c.Id,
                UserLarge = c.Large,
                SkinJson = RomStore.LoadMySkin(c),
                SkinName = c.SkinName,
            });
        }
        catch (Exception ex) { Say($"{c.Name} could not start: {ex.Message}"); }
    }

    /// <summary>Before leaving a calculator: if the user changed it, say what
    /// and offer to keep it. False = stay here.</summary>
    async Task<bool> ConfirmLeave()
    {
        var changes = _session?.Changes() ?? [];
        if (changes.Count == 0) return true;
        var save = new Button { Content = "Save as my calculator...", IsDefault = true };
        var drop = new Button { Content = "Don't save" };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var list = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 0, 0) };
        foreach (var c in changes)
            list.Children.Add(new TextBlock { Text = "- " + c, TextWrapping = TextWrapping.Wrap });
        var dlg = new Window
        {
            Title = "Keep your changes?",
            Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 10,
                Children =
                {
                    new TextBlock { TextWrapping = TextWrapping.Wrap,
                        Text = $"You changed the {_session!.Name ?? _session.Model.Name}:" },
                    list,
                    new TextBlock { TextWrapping = TextWrapping.Wrap,
                        Text = "Keep it as your own calculator before switching?" },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, drop, save } },
                },
            },
        };
        string answer = "cancel";
        void Pick(Button b, string a) => b.Click += (_, _) => { answer = a; dlg.Close(); };
        Pick(save, "save"); Pick(drop, "drop"); Pick(cancel, "cancel");
        await dlg.ShowDialog(this);
        return answer switch { "drop" => true, "save" => await SaveMyCalc(), _ => false };
    }

    async Task<bool> SaveMyCalc()
    {
        if (_session is null) { Say("Start a calculator first (and apply your patches)."); return false; }
        bool plus = ModelCatalog.KnownBuild(_session.Image()) == "16C+";
        var name = new TextBox
        {
            Text = _session.Name ?? (plus ? "HP-16C+" : _session.Model.Name + (_session.Patches.Count > 0 ? " (patched)" : " (mine)")),
        };
        var shortName = new TextBox { Text = _session.ShortName ?? RomStore.SuggestShortName(name.Text ?? ""), MaxLength = 32 };
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Orange, IsVisible = false };
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
                    new TextBlock { Text = "Short name (its folder: letters, digits, + - _)" }, shortName, problem,
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
        // the button says what will happen: new, update this one, or replace another
        void ShowSaveAction()
        {
            var s = shortName.Text?.Trim() ?? "";
            bool own = string.Equals(s, _session.ShortName, StringComparison.OrdinalIgnoreCase);
            bool taken = RomStore.ShortNameTaken(s);
            ok.Content = !taken ? "Save" : own ? "Update" : "Override";
            problem.Text = taken && !own ? $"\"{s}\" already exists and will be replaced." : "";
            problem.IsVisible = problem.Text.Length > 0;
        }
        shortName.TextChanged += (_, _) => ShowSaveAction();
        ShowSaveAction();
        ok.Click += (_, _) =>
        {
            var issue = string.IsNullOrWhiteSpace(name.Text) ? "Enter a name."
                : RomStore.ShortNameProblem(shortName.Text);
            problem.Text = issue ?? "";
            problem.IsVisible = issue is not null;
            if (issue is not null) return;
            save = true;
            dlg.Close();
        };
        cancel.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        if (!save) return false;
        var c = RomStore.SaveMyCalc(shortName.Text!.Trim(), name.Text!.Trim(), _session.Model.Id, label.Text?.Trim() ?? "",
                                    desc.Text?.Trim() ?? "", _session.Image(), _session.UserLarge,
                                    _session.SkinJson, _session.SkinName);
        _session.MarkSaved();
        RefreshMyMenu();
        Say($"Saved \"{c.Name}\". It is on the start page now; launch it directly with --model {c.Id}.");
        return true;
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
    void ShowTests()
    {
        if (_session is null) { Say("Start a calculator first: tests run on a fresh copy of it."); return; }
        new TestWindow(_session.Name ?? _session.Model.Name, () => RunTests).Show(this);
    }

    /// <summary>Run test lines on a fresh copy of the current calculator.</summary>
    async Task<string> RunTests(string title, IReadOnlyList<string> lines, CancellationToken stop,
                                Action<TestScript.Result> show)
    {
        if (_session is null) return "no calculator running";
        var s = _session;
        var img = s.Image();
        var (chips, large) = s.ChipMap(img);
        var skin = _calc.Skin;
        int pass = 0, fail = 0;
        // the test machine takes over the window; the user's calculator waits untouched
        var own = _runner!;
        var run = new MachineRunner(s.CreateMachine(img, chips, large), $"Test on a fresh {s.Name ?? s.Model.Name}")
        {
            View = own.View,
            KeySeen = code => Dispatcher.UIThread.Post(() => _calc.ShowKey(code)),
        };
        _runner = run;
        _calc.Runner = run;
        _calc.Locked = true;
        _menu.IsEnabled = false;
        _view.IsEnabled = false;
        var said = _message.Text;
        Say("Test running: display and status show the test calculator. Your calculator is unchanged and comes back afterwards.");
        try
        {
            await Task.Run(() =>
            {
                var script = new TestScript(run, new KeyNames(skin, s.Model.Id), skin);
                script.Begin(stop);
                foreach (var r in script.Run(lines, stop))
                {
                    if (r.Ok) pass++; else fail++;
                    show(r);
                }
            }, CancellationToken.None);
        }
        finally
        {
            run.KeySeen = null;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            _calc.ShowKey(-1);
            _runner = own;
            _calc.Runner = own;
            _calc.Locked = false;
            _menu.IsEnabled = true;
            _view.IsEnabled = true;
            Say(said ?? "");
            run.Dispose();
        }
        return $"{pass} ok, {fail} failed" + (stop.IsCancellationRequested ? " (stopped)" : "");
    }

    MachineState CurrentState()
    {
        var st = _runner!.SaveState();
        st.Generator = "Palmarius Voyager simulator Version " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        st.Calculator = _session!.Model.Name;
        st.Model = _session.Model.Id;
        st.Rom = _session.BaseLabel;
        if (_session.RomFile is { } file)
            st.RomFile = RomStore.Relative(file);
        st.ChipLayout = _session.ChipMap(_session.Image()).Large ? "large" : "own";
        st.RomFingerprint = _session.Image().CanonicalSha256();
        st.Patches = _session.Patches.Select(p => p.Name).ToList();
        st.Note = "Voyager simulator machine state. Registers: hex digits, most significant first. " +
                  "ram: register address -> 14 digits. Load it with the same calculator, ROM and patches.";
        return st;
    }

    /// <summary>Load a state into the running calculator. Another calculator or
    /// chip layout cannot work and is refused; for another ROM or patch set
    /// the user decides (anyway = true).</summary>
    string ApplyState(MachineState st, bool anyway)
    {
        if (_runner is null || _session is null) return "error no calculator running";
        if (st.Model != _session.Model.Id)
            throw new InvalidDataException($"this state is from the {st.Calculator}; start that calculator first");
        var layout = _session.ChipMap(_session.Image()).Large ? "large" : "own";
        if (st.ChipLayout != layout)
            throw new InvalidDataException($"this state needs the {st.ChipLayout} chip layout, this calculator runs " +
                                           $"the {layout} one; change it with ROM > Chip layout");
        if (!anyway && st.RomFingerprint != _session.Image().CanonicalSha256())
            return "different-rom";
        _runner.LoadState(st);
        return "ok";
    }

    async Task<bool> AskLoadAnyway(MachineState st)
    {
        var load = new Button { Content = "Load anyway" };
        var cancel = new Button { Content = "Cancel", IsDefault = true, IsCancel = true };
        var dlg = new Window
        {
            Title = "Different ROM",
            Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 10,
                Children =
                {
                    new TextBlock { TextWrapping = TextWrapping.Wrap,
                        Text = $"This state was saved with: {st.Rom}{string.Concat(st.Patches.Select(p => " + " + p))}." },
                    new TextBlock { TextWrapping = TextWrapping.Wrap,
                        Text = "The ROM running now is different. Registers and memory load fine, but the " +
                               "calculator may misbehave if the code they belong to has moved. That is " +
                               "useful while you work on a patch." },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8,
                        HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, load } },
                },
            },
        };
        bool result = false;
        load.Click += (_, _) => { result = true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        await dlg.ShowDialog(this);
        return result;
    }

    async Task SaveState()
    {
        if (_runner is null || _session is null) return;
        var st = CurrentState();
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
            if (ApplyState(st, false) == "different-rom")
            {
                if (!await AskLoadAnyway(st)) return;
                ApplyState(st, true);
            }
            Say($"State loaded from {f.Value.Name} (saved {st.SavedAt}).");
        }
        catch (Exception ex) { Say("State not loaded: " + ex.Message); }
    }

    // ------------------------------------------------------------- skins
    void SetSkin(string? json, string? name)
    {
        if (_session is null) { Say("Start a calculator first."); return; }
        if (json is not null) Skin.FromJson(json); // refuse a broken file before keeping it
        _session.SkinJson = json;
        _session.SkinName = name;
        ApplySkin();
    }

    async Task LoadSkin()
    {
        var f = await PickText("Skin file (.json)", "*.json");
        if (f is null) return;
        try
        {
            SetSkin(string.Join('\n', f.Value.Lines), f.Value.Name);
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

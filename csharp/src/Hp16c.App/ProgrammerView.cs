using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Ce.Flash;
using Nut.Core;

namespace HpSim;

/// <summary>
/// The screen "Calculator > Update a HP-15C CE...": nothing but the programmer. The calculator simulation is stopped before
/// this is shown, the debugger has no target and the --api channel answers "busy" (MainWindow), so nothing else touches the
/// port or the CPU thread while a job runs. One worker thread owns the serial port; this view only starts it, polls an
/// immutable snapshot and drains a log queue. While a job runs the window cannot be closed, the menus are off and the PC
/// is kept awake (<see cref="PowerGuard"/>).
/// Three actions: 1. dump the calculator (read only); 2. upload one of YOUR calculators (based on the 16C: the words where it differs
/// from the original 16C, laid over the unit's own 16C ROM, see CeUnit); 3. restore a saved dump. Every byte that crosses the
/// port goes to data/ce/ce-programmer-*.log (see RecordingTransport); what an upload overwrites is saved page by page.
/// </summary>
sealed class ProgrammerView : UserControl
{
    sealed record DumpFile(string Path, string Text) { public override string ToString() => Text; }

    readonly ComboBox _ports = new() { MinWidth = 150 };
    readonly Button _refresh = new() { Content = "Refresh" };
    readonly Button _dump = new() { Content = "1. Dump the connected calculator (reads only)", Padding = new Thickness(14, 8) };
    readonly Button _saveBackup = new() { Content = "Save this dump as my calculator...", Padding = new Thickness(14, 8), IsEnabled = false };
    readonly Button _savePatch = new() { Content = "Save what was written as a patch", Padding = new Thickness(14, 8), IsEnabled = false };
    readonly ComboBox _mine = new() { MinWidth = 320 };
    readonly TextBlock _mineInfo = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
    readonly CheckBox _confirm = new() { Content = "I understand that this writes to the flash of the calculator (never to its boot loader). What it overwrites is saved, and a dump brings it back." };
    readonly Button _upload = new() { Content = "2. Upload the selected calculator", Padding = new Thickness(14, 8), IsEnabled = false };
    readonly ComboBox _dumps = new() { MinWidth = 420 };
    readonly Button _restore = new() { Content = "3. Restore the calculator from this dump", Padding = new Thickness(14, 8), IsEnabled = false };
    readonly Button _cancel = new() { Content = "Cancel (between two pages)", Padding = new Thickness(14, 8), IsEnabled = false };
    readonly Button _back = new() { Content = "Back to the calculators", Padding = new Thickness(14, 8) };
    readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 18 };
    readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, Height = 260, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), FontSize = 12 };
    readonly Action _leave;
    readonly string _dir;
    readonly ConcurrentQueue<string> _lines = new();
    readonly DispatcherTimer _timer;
    volatile JobProgress? _latest;
    volatile byte[]? _sessionDump;                         // the dump taken on this screen: the baseline of an upload
    volatile CeUnit? _sessionUnit;
    volatile SortedDictionary<int, int>? _lastDiff;        // what the last upload wrote, as differences from the backup's own 16C ROM
    volatile string? _lastDiffName;
    readonly RomImage? _original16c = RomStore.Load("16c");
    List<RomStore.MyCalc> _mineList = [];
    Dictionary<int, int>? _changes;                        // of the selected calculator, null if it cannot be uploaded
    CancellationTokenSource? _cts;
    Task? _task;
    bool _busy;

    /// <summary>True from the moment a job starts until it has ended: closing is refused and the menus are off.</summary>
    public bool Busy => _busy;
    public event Action<bool>? BusyChanged;

    public ProgrammerView(string dataDir, Action leave)
    {
        _dir = dataDir;
        _leave = leave;
        var steps = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Put the calculator into programming mode (HP's steps):\n" +
                   "  1. Open the battery compartment and plug the programming cable into the POGO connector; the USB side into this PC.\n" +
                   "  2. Turn the calculator on (ON).\n" +
                   "  3. Press and hold ERASE on the cable, press and release RESET, release ERASE.\n" +
                   "  4. A new port \"Bossa Program Port (COMx)\" appears: choose it below (Refresh if it is not there yet).\n" +
                   "     Windows without that port: install the driver once (folder 15c-ce\\driver of the zip: right-click atm6124_cdc.inf, Install).\n" +
                   "Only the HP-15C Collector's Edition with the HP programming cable. Reading never changes the calculator."
        };
        var portRow = Row(new TextBlock { Text = "Port:", VerticalAlignment = VerticalAlignment.Center }, _ports, _refresh);
        var uploadRow = Row(new TextBlock { Text = "Calculator:", VerticalAlignment = VerticalAlignment.Center }, _mine);
        var restoreRow = Row(new TextBlock { Text = "Dump:", VerticalAlignment = VerticalAlignment.Center }, _dumps);
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 10, MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    new TextBlock { Text = "Update a HP-15C Collector's Edition", FontSize = 22, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "The calculator simulation is stopped while this screen is open.", Opacity = 0.7 },
                    steps, portRow, Row(_dump, _saveBackup),
                    new TextBlock { Text = "Upload one of your calculators (the 16 and the 15.2 mode of the CE then run it):", Margin = new Thickness(0, 8, 0, 0) },
                    uploadRow, _mineInfo, _confirm, Row(_upload, _savePatch),
                    new TextBlock { Text = "Restore: put the slot and the pointer back as a saved dump has them:", Margin = new Thickness(0, 8, 0, 0) },
                    restoreRow, _restore,
                    Row(_cancel, _back), _bar, _state, _log,
                },
            },
        };
        _refresh.Click += (_, _) => RefreshPorts();
        _dump.Click += (_, _) => StartDump();
        _upload.Click += (_, _) => StartUpload();
        _saveBackup.Click += async (_, _) => await SaveBackupAsCalculator();
        _savePatch.Click += (_, _) => SaveDifferencesAsPatch();
        _restore.Click += (_, _) => StartRestore();
        _cancel.Click += (_, _) => { _cts?.Cancel(); Add("Cancel asked: it stops before the next page and puts back what was written."); };
        _back.Click += (_, _) => { if (!_busy) _leave(); };
        _mine.SelectionChanged += (_, _) => SelectMine();
        _dumps.SelectionChanged += (_, _) => UpdateButtons();
        _ports.SelectionChanged += (_, _) => UpdateButtons();
        _confirm.IsCheckedChanged += (_, _) => UpdateButtons();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
        RefreshPorts();
        LoadMine();
        LoadDumps();
        Add("Ready. Nothing is sent to the calculator until you press a button.");
    }

    static StackPanel Row(params Control[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var c in items) row.Children.Add(c);
        return row;
    }

    void RefreshPorts()
    {
        string? keep = _ports.SelectedItem as string;
        var names = SerialSamBaTransport.PortNames();
        _ports.ItemsSource = names;
        _ports.SelectedItem = names.Contains(keep ?? "") ? keep : names.Length == 1 ? names[0] : null;   // only a single candidate is chosen for you
        Add(names.Length == 0 ? "No serial ports. Is the calculator in programming mode?" : "Ports: " + string.Join(", ", names));
    }

    // ---------------------------------------------------------------- the calculators that can go to the CE
    void LoadMine()
    {
        var all = RomStore.MyCalcs();
        _mineList = all.Where(c => c.Model == "16c").ToList();
        _mine.ItemsSource = _mineList.Select(c => c.Name).ToList();
        int other = all.Count - _mineList.Count;
        if (_original16c is null)
            _mineInfo.Text = "The original HP-16C ROM is not in the ROM store yet (start page: Get ROM from Nonpareil): it is needed to see what a calculator changes.";
        else if (_mineList.Count == 0)
            _mineInfo.Text = "You have no calculator based on the HP-16C yet. Apply a patch to the 16C and save it as your calculator (Calculator > Save as my calculator...).";
        else
            _mineInfo.Text = "Only calculators based on the HP-16C can go to the CE's 16 mode" + (other > 0 ? $" ({other} of yours are based on other models and are not offered)." : ".");
        Add($"{_mineList.Count} of your calculators are based on the HP-16C.");
    }

    void SelectMine()
    {
        _changes = null;
        int i = _mine.SelectedIndex;
        if (i < 0 || i >= _mineList.Count || _original16c is null) { UpdateButtons(); return; }
        try
        {
            var mine = RomStore.LoadMyRom(_mineList[i]);
            var changes = CeUnit.ChangesFrom(mine.Words, _original16c.Words);
            bool free = CeUnit.ChecksumWordIsFree(changes, _original16c.Words);
            var sb = new StringBuilder();
            sb.Append($"{changes.Keys.Count(a => a < 0x1800)} words changed in the 16C ROM (0000-17FF), {changes.Keys.Count(a => a >= 0x2000)} in the second ROM range (2000-37FF). ");
            sb.Append(free ? "The chip sum of the unit is kept (checksum word 13FF). " : "This calculator uses word 13FF for code: the unit's chip checksum will change. ");
            if (_sessionUnit is { } u)
            {
                var over = u.OverwritesUnitSpecificWords(changes, _original16c.Words);
                if (over.Count > 0) sb.Append($"It overwrites {over.Count} word(s) where the CE's own 16C ROM differs from the original ({string.Join(" ", over.Select(a => a.ToString("X4")))}): the CE's dead code at 13FD-13FF, as the 16C+ patch always did. ");
            }
            sb.Append("What is written is these words over the calculator's OWN 16C ROM, not the original ROM.");
            _mineInfo.Text = sb.ToString();
            _changes = changes;
        }
        catch (ArgumentException e) { _mineInfo.Text = "Cannot be uploaded: " + e.Message; }
        UpdateButtons();
    }

    // ---------------------------------------------------------------- the saved dumps
    void LoadDumps()
    {
        var items = new List<DumpFile>();
        if (Directory.Exists(_dir))
            foreach (var f in new DirectoryInfo(_dir).GetFiles("*.bin").Where(f => f.Length == CeUnit.DumpSize).OrderByDescending(f => f.LastWriteTime))
                items.Add(new DumpFile(f.FullName, $"{f.Name}   ({f.LastWriteTime:yyyy-MM-dd HH:mm})   {DescribeDump(File.ReadAllBytes(f.FullName))}"));
        _dumps.ItemsSource = items;
        if (items.Count > 0 && _dumps.SelectedIndex < 0) _dumps.SelectedIndex = 0;
        UpdateButtons();
    }

    static string DescribeDump(byte[] bytes)
    {
        try
        {
            var u = CeUnit.FromDump(bytes, KnownRegions(), allowImage: true);
            return u.ImageRegionsChanged.Count == 0 ? "[factory state of the known calculator]" : "[known calculator with an image installed]";
        }
        catch (UnknownUnitException) { return "[NOT a calculator the kit knows: cannot be used]"; }
    }

    // ---------------------------------------------------------------- state of the buttons and the job
    void Add(string text) => _lines.Enqueue($"{DateTime.Now:HH:mm:ss}  {text}");

    void UpdateButtons()
    {
        bool port = _ports.SelectedItem is string;
        bool ok = _confirm.IsChecked == true && port && !_busy;
        _dump.IsEnabled = port && !_busy;
        _saveBackup.IsEnabled = _sessionUnit is not null && !_busy;
        _savePatch.IsEnabled = _lastDiff is not null && !_busy;
        _upload.IsEnabled = ok && _sessionDump is not null && _sessionUnit is not null && _changes is not null;
        _restore.IsEnabled = ok && _dumps.SelectedItem is DumpFile d && !d.Text.Contains("NOT a calculator");
        _cancel.IsEnabled = _busy && _cts is not null;
        _back.IsEnabled = !_busy;
        _refresh.IsEnabled = !_busy;
        _ports.IsEnabled = !_busy;
        _mine.IsEnabled = !_busy;
        _dumps.IsEnabled = !_busy;
        _confirm.IsEnabled = !_busy;
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        var handle = TopLevel.GetTopLevel(this)?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (busy) PowerGuard.Enter(handle, "VoyagerSim is working on a calculator: closing now could leave it half written."); else PowerGuard.Leave(handle);
        UpdateButtons();
        BusyChanged?.Invoke(busy);
    }

    void Poll()
    {
        bool any = false;
        var sb = new StringBuilder();
        while (_lines.TryDequeue(out var l)) { sb.AppendLine(l); any = true; }
        if (any)
        {
            _log.Text += sb.ToString();
            _log.CaretIndex = _log.Text?.Length ?? 0;
        }
        if (_latest is { } p)
        {
            _bar.Value = p.Steps == 0 ? 0 : (double)p.Step / p.Steps;
            _state.Text = $"{p.Stage}: {p.Message}   ({p.Elapsed:mm\\:ss})";
        }
        if (_busy && _task is { IsCompleted: true })
        {
            _task = null;
            _cts?.Dispose();
            _cts = null;
            _confirm.IsChecked = false;                    // every write asks again
            SetBusy(false);
            LoadDumps();
            SelectMine();
        }
    }

    /// <summary>Start a job: one worker thread owns the port; run(flash, rec) does the work and says what it did through Add.</summary>
    void Start(string what, string port, bool writes, Action<Sam4lFlash, RecordingTransport> run)
    {
        Directory.CreateDirectory(_dir);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss"), logPath = Path.Combine(_dir, $"ce-programmer-{stamp}-{what}.log");
        _bar.Value = 0;
        _latest = null;
        _cts = writes ? new CancellationTokenSource() : null;
        SetBusy(true);
        _task = Task.Run(() =>
        {
            try
            {
                using var logFile = new StreamWriter(logPath, false, new UTF8Encoding(false));
                using var rec = new RecordingTransport(new SerialSamBaTransport(port), logFile, $"VoyagerSim programmer: {what} {port}") { DetailLimit = writes ? 2000 : 600 };
                run(new Sam4lFlash(new SamBaClient(rec)), rec);
                Add($"Log of everything that crossed the port: {logPath}");
            }
            catch (JobCancelledException e) { Add(e.Message); _sessionDump = null; }
            catch (JobFailedException e)
            {
                Add("Stopped: " + e.Message);
                if (writes) { _sessionDump = null; Add(e.DeviceMayDiffer ? "The calculator may differ from before: put it into programming mode again and use 3. Restore with a saved dump." : "The calculator is as it was."); }
            }
            catch (IOException e) { Add("Connection problem: " + e.Message); if (writes) _sessionDump = null; }
            catch (SamBaException e) { Add("The calculator did not answer as expected: " + e.Message); if (writes) _sessionDump = null; }
            catch (Exception e) { Add("Unexpected: " + e); if (writes) _sessionDump = null; }
        });
    }

    // ---------------------------------------------------------------- 1. dump
    void StartDump()
    {
        if (_busy || _ports.SelectedItem is not string port) { Add("Choose a port first."); return; }
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss"), dumpPath = Path.Combine(_dir, $"dump-{stamp}.bin");
        _sessionDump = null;
        _sessionUnit = null;
        Add($"Dump from {port}: two full reads of the 128 KB flash, nothing is written.");
        Start("dump", port, false, (flash, rec) =>
        {
            var bytes = CeDump.Read(flash, new ProgressSink(p => _latest = p));
            File.WriteAllBytes(dumpPath, bytes);
            Add($"Saved {dumpPath}");
            Add($"131072 bytes, two identical reads, SHA-256 {CeUnit.Sha256Hex(bytes).ToUpperInvariant()}. It contains the calculator's firmware: do not share it.");
            try
            {
                var unit = CeUnit.FromDump(bytes, KnownRegions(), allowImage: true);
                _sessionUnit = unit;
                _sessionDump = bytes;
                Add(unit.ImageRegionsChanged.Count == 0
                    ? "This is the calculator the kit has been tried on, in its factory state: you can upload."
                    : "This is the calculator the kit has been tried on; a 16C+ (or another image) is installed in its 15.2 slot. You can upload again. Keep this dump: it is a state you can restore.");
            }
            catch (UnknownUnitException e) { Add(e.Message + " Nothing can be written to this calculator by this program."); }
        });
    }

    // ---------------------------------------------------------------- 2. upload
    void StartUpload()
    {
        if (_busy || _ports.SelectedItem is not string port || _sessionDump is not { } dump || _sessionUnit is not { } unit || _changes is not { } changes
            || _original16c is null || _mine.SelectedIndex < 0 || _confirm.IsChecked != true) return;
        string name = _mineList[_mine.SelectedIndex].Name, stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss"), preDir = Path.Combine(_dir, $"preimages-{stamp}");
        bool keep = CeUnit.ChecksumWordIsFree(changes, _original16c.Words);
        Add($"Upload \"{name}\" to {port}: baseline check against the dump of this session, then the slot pages, the pointer last.");
        Start("upload", port, true, (flash, rec) =>
        {
            var img = unit.BuildSlot(changes, keep);
            Add($"Image built: unit's own 16C ROM + {changes.Count} changed words; chip sum {img.UnitChipSum} (unit) -> {img.FinalChipSum} (image); checksum word 13FF {img.ChecksumWordOld:X3} -> {img.ChecksumWordNew:X3}.");
            rec.Note($"upload of '{name}': {changes.Count} words");
            var res = new CeUpload(flash, dump, img, (page, pre) =>
            {
                Directory.CreateDirectory(preDir);
                File.WriteAllBytes(Path.Combine(preDir, $"page-{page:D3}.bin"), pre);
            }).Run(true, new ProgressSink(p => _latest = p), _cts!.Token);
            _sessionDump = null;                           // the calculator is not the dump any more: take a new one for the next job
            _lastDiff = unit.DifferencesFromOwnRom(img);   // kept for the button "Save what was written as a patch"
            _lastDiffName = name;
            Add("The differences from the backup's own 16C ROM can be kept as a patch: button \"Save what was written as a patch\".");
            Add($"Done: {res.PagesWritten} pages written, {res.PagesAlreadyRight} already right, read back and verified ({res.Elapsed:mm\\:ss}). The pages it overwrote are in {preDir}.");
            Add("Leave programming mode: press RESET on the cable, then ON. Modes: g + ENTER + ON, then 4, then 7 (16) or y^x (15.2).");
        });
    }

    // ---------------------------------------------------------------- the backup as a calculator, the upload as a patch
    async Task SaveBackupAsCalculator()
    {
        if (_sessionUnit is not { } unit) return;
        RomImage img = new();
        string what;
        try
        {
            foreach (var (a, w) in unit.RunningRom16(out what)) img.Words[a] = w;
        }
        catch (InvalidOperationException e) { Add("Cannot be saved: " + e.Message); return; }
        string? name = await AskName($"HP-16C (CE backup {DateTime.Now:yyyy-MM-dd HH:mm})");
        if (name is null) return;
        string shortName = RomStore.SuggestShortName(name);
        for (int n = 2; RomStore.ShortNameTaken(shortName); n++) shortName = RomStore.SuggestShortName(name) + n;
        RomStore.SaveMyCalc(shortName, name, "16c", "16CE", $"from a dump of a HP-15C CE: {what}", img);
        Add($"Saved as your calculator \"{name}\" ({shortName}): {what}.");
        LoadMine();
    }

    void SaveDifferencesAsPatch()
    {
        if (_lastDiff is not { } diff) return;
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, $"upload-{DateTime.Now:yyyyMMdd-HHmmss}.patch");
        var sb = new StringBuilder();
        sb.AppendLine($"# What the upload of \"{_lastDiffName}\" changed in the 16 mode of a HP-15C CE, as the differences from the backup's own 16C ROM");
        sb.AppendLine("# (0000-17FF: the words that differ; 2000-37FF: its non-zero words). Apply it on the 16C ROM of the CE (the calculator \"HP-16C (15C CE ROM)\").");
        sb.AppendLine($"# {diff.Count} words, made {DateTime.Now:yyyy-MM-dd HH:mm} by VoyagerSim. Format: aaaa:www (hex address : 10-bit word), as Nonpareil.");
        foreach (var (a, w) in diff) sb.AppendLine($"{a:x4}:{w:x3}");
        File.WriteAllText(path, sb.ToString());
        Add($"Saved {diff.Count} words as a patch: {path}");
    }

    async Task<string?> AskName(string suggestion)
    {
        var box = new TextBox { Text = suggestion, MinWidth = 440 };
        var ok = new Button { Content = "Save", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        string? result = null;
        var dlg = new Window
        {
            Title = "Save as my calculator", Width = 540, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { new TextBlock { Text = "Name of the calculator:" }, box, Row(ok, cancel) } },
        };
        ok.Click += (_, _) => { result = box.Text; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        if (TopLevel.GetTopLevel(this) is Window owner) await dlg.ShowDialog(owner); else dlg.Show();
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    // ---------------------------------------------------------------- 3. restore
    void StartRestore()
    {
        if (_busy || _ports.SelectedItem is not string port || _dumps.SelectedItem is not DumpFile d || _confirm.IsChecked != true) return;
        var dump = File.ReadAllBytes(d.Path);
        try { CeUnit.FromDump(dump, KnownRegions(), allowImage: true); }
        catch (UnknownUnitException e) { Add(e.Message + " Not used."); return; }
        Add($"Restore from {Path.GetFileName(d.Path)} to {port}: the slot and the pointer are written back as that dump has them, then the whole flash is compared.");
        Start("restore", port, true, (flash, rec) =>
        {
            var res = CeRestore.Run(flash, dump, new ProgressSink(p => _latest = p), _cts!.Token);
            _sessionDump = null;
            Add($"Done: {res.PagesWritten} pages written, {res.PagesAlreadyRight} already right; the whole flash equals the dump ({res.Elapsed:mm\\:ss}).");
            Add("Leave programming mode: press RESET on the cable, then ON.");
        });
    }

    static List<KnownRegion> KnownRegions()
    {
        using var s = typeof(ProgrammerView).Assembly.GetManifestResourceStream("Ce.known-units.txt")!;
        using var r = new StreamReader(s);
        return CeUnit.ParseKnown(r.ReadToEnd());
    }

    sealed class ProgressSink(Action<JobProgress> on) : IProgress<JobProgress>
    {
        public void Report(JobProgress value) => on(value);
    }
}

/// <summary>
/// What a program can do against being closed or put to sleep while it writes to a calculator: keep the PC awake and tell
/// Windows why a shutdown should wait. It cannot stop Task Manager's End task, a power cut or a pulled cable; the writing
/// is made safe against those by the order of the pages, the saved pre-images and Restore (Ce.Flash).
/// </summary>
static class PowerGuard
{
    const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool ShutdownBlockReasonCreate(IntPtr hwnd, string reason);
    [DllImport("user32.dll")] static extern bool ShutdownBlockReasonDestroy(IntPtr hwnd);

    public static void Enter(IntPtr window, string reason)
    {
        if (!OperatingSystem.IsWindows()) return;                    // macOS and Linux: not done yet
        SetThreadExecutionState(EsContinuous | EsSystemRequired);
        if (window != IntPtr.Zero) ShutdownBlockReasonCreate(window, reason);
    }

    public static void Leave(IntPtr window)
    {
        if (!OperatingSystem.IsWindows()) return;
        SetThreadExecutionState(EsContinuous);
        if (window != IntPtr.Zero) ShutdownBlockReasonDestroy(window);
    }
}

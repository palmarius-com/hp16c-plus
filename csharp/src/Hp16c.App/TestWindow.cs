using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace HpSim;

/// <summary>Help > Test window: write key sequences with expected displays and
/// run them on a fresh copy of the current calculator.</summary>
sealed class TestWindow : Window
{
    const string Sample =
        "# One line, one test: keys as printed on the calculator => what the display must show.\n" +
        "# Help > How to write a test has the details.\n" +
        "1 2 ENTER 3 0 +  =>  42 h\n";

    const double LineHeight = 18; // the same for numbers and text, or they drift apart

    readonly Func<Func<string, IReadOnlyList<string>, CancellationToken, Action<TestScript.Result>, Task<string>>> _ready;
    readonly TextBox _script = new()
    {
        AcceptsReturn = true,
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
        FontSize = 13,
        LineHeight = LineHeight,
        TextWrapping = TextWrapping.NoWrap,
        Text = Sample,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(6, 0, 0, 0),
        MinHeight = 400,
    };
    readonly TextBlock _numbers = new()
    {
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
        FontSize = 13,
        LineHeight = LineHeight,
        Foreground = Brushes.Gray,
        TextAlignment = TextAlignment.Right,
        Margin = new Thickness(6, 0, 0, 0),
        MinWidth = 30,
    };
    readonly StackPanel _results = new() { Spacing = 2, Margin = new Thickness(8) };
    readonly TextBlock _summary = new() { Margin = new Thickness(0, 6), VerticalAlignment = VerticalAlignment.Center };
    readonly Button _run = new() { Content = "Run", IsDefault = false };
    readonly ScrollViewer _left = new(), _right = new();
    CancellationTokenSource? _stop;

    public TestWindow(string calculator,
        Func<Func<string, IReadOnlyList<string>, CancellationToken, Action<TestScript.Result>, Task<string>>> ready)
    {
        _ready = ready;
        Title = $"Tests on {calculator}";
        Width = 980;
        Height = 620;
        var open = new Button { Content = "Open..." };
        var save = new Button { Content = "Save..." };
        var stop = new Button { Content = "Stop" };
        open.Click += async (_, _) => await OpenFile();
        save.Click += async (_, _) => await SaveFile();
        stop.Click += (_, _) => _stop?.Cancel();
        _run.Click += async (_, _) => await RunTests();
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8),
            Children = { _run, stop, open, save, _summary },
        };
        DockPanel.SetDock(bar, Dock.Top);
        var split = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        _right.Content = _results;
        Grid.SetColumn(_right, 1);
        var editor = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(_script, 1);
        editor.Children.Add(_numbers);
        editor.Children.Add(_script);
        _left.Content = editor; // numbers and text scroll together
        split.Children.Add(_left);
        split.Children.Add(_right);
        _script.TextChanged += (_, _) => ShowNumbers();
        ShowNumbers();
        Content = new DockPanel { Children = { bar, split } };
    }

    void ShowNumbers()
    {
        int count = (_script.Text ?? "").Count(c => c == '\n') + 1;
        _numbers.Text = string.Join('\n', Enumerable.Range(1, count));
    }

    async Task RunTests()
    {
        _run.IsEnabled = false;
        _results.Children.Clear();
        _summary.Text = "running...";
        _stop = new CancellationTokenSource();
        var lines = (_script.Text ?? "").Replace("\r", "").Split('\n');
        void Show(TestScript.Result r) => Dispatcher.UIThread.Post(() =>
        {
            _results.Children.Add(new SelectableTextBlock
            {
                Text = $"{(r.Ok ? "ok  " : "FAIL")} line {r.Line}: {r.Text}" + (r.Ok ? "" : $"  -> {r.Detail}"),
                Foreground = r.Ok ? Brushes.LightGreen : Brushes.Orange,
                FontFamily = _script.FontFamily, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
            // after layout, so the new result counts for the end of the list
            Dispatcher.UIThread.Post(() => FollowTest(r.Line), DispatcherPriority.Background);
        });
        var sum = await _ready()("", lines, _stop.Token, Show);
        var end = $"{(_stop.IsCancellationRequested ? "Stopped" : "Finished")}: {sum}";
        _summary.Text = end;
        _results.Children.Add(new TextBlock
        {
            Text = end, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 0),
            FontFamily = _script.FontFamily, FontSize = 12,
        });
        Dispatcher.UIThread.Post(_right.ScrollToEnd, DispatcherPriority.Background);
        _run.IsEnabled = true;
    }

    /// <summary>The last result at the bottom on the right, its line at the
    /// bottom on the left; the rest moves up on both sides.</summary>
    void FollowTest(int line)
    {
        _right.ScrollToEnd();
        _left.Offset = new Vector(_left.Offset.X, Math.Max(0, LineHeight * line - _left.Viewport.Height));
    }

    async Task OpenFile()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Test file",
            FileTypeFilter = [new FilePickerFileType("Test file") { Patterns = ["*.txt", "*"] }],
        });
        if (files.Count == 0) return;
        await using var s = await files[0].OpenReadAsync();
        using var reader = new StreamReader(s);
        _script.Text = await reader.ReadToEndAsync();
    }

    async Task SaveFile()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the tests", SuggestedFileName = "my-tests.txt",
        });
        if (file is null) return;
        await using var s = await file.OpenWriteAsync();
        await using var w = new StreamWriter(s);
        await w.WriteAsync(_script.Text ?? "");
    }
}

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

    readonly Func<Func<string, IReadOnlyList<string>, CancellationToken, Action<TestScript.Result>, Task<string>>> _ready;
    readonly TextBox _script = new()
    {
        AcceptsReturn = true,
        FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
        FontSize = 13,
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
        Foreground = Brushes.Gray,
        TextAlignment = TextAlignment.Right,
        Margin = new Thickness(6, 0, 0, 0),
        MinWidth = 30,
    };
    readonly StackPanel _results = new() { Spacing = 2, Margin = new Thickness(8) };
    readonly TextBlock _summary = new() { Margin = new Thickness(0, 6), VerticalAlignment = VerticalAlignment.Center };
    readonly Button _run = new() { Content = "Run", IsDefault = false };
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
        var right = new ScrollViewer { Content = _results };
        Grid.SetColumn(right, 1);
        var editor = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(_script, 1);
        editor.Children.Add(_numbers);
        editor.Children.Add(_script);
        split.Children.Add(new ScrollViewer { Content = editor }); // numbers and text scroll together
        split.Children.Add(right);
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
            _results.Children.Add(new SelectableTextBlock
            {
                Text = $"{(r.Ok ? "ok  " : "FAIL")} line {r.Line}: {r.Text}" + (r.Ok ? "" : $"  -> {r.Detail}"),
                Foreground = r.Ok ? Brushes.LightGreen : Brushes.Orange,
                FontFamily = _script.FontFamily, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            }));
        _summary.Text = await _ready()("", lines, _stop.Token, Show);
        _run.IsEnabled = true;
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

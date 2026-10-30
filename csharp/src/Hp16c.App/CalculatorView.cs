using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace HpSim;

/// <summary>Draws the calculator (skin + LCD) and turns clicks into key presses.</summary>
sealed class CalculatorView : Control
{
    // logical faceplate size; scaled to the control
    const double FaceW = 1000, FaceH = 620;
    const double KeysTop = 232, PitchX = 94, PitchY = 96, KeyW = 64, KeyH = 46, KeysLeft = 47;

    static readonly IBrush BezelDark = new SolidColorBrush(Color.FromRgb(0x5E, 0x55, 0x42));
    static readonly IBrush LcdBack = new SolidColorBrush(Color.FromRgb(0xA9, 0xB0, 0x98));
    static readonly IBrush LcdInk = new SolidColorBrush(Color.FromRgb(0x1C, 0x1E, 0x1A));
    static readonly IBrush LcdGhost = new SolidColorBrush(Color.FromArgb(0x16, 0, 0, 0));
    static readonly IBrush KeyCap = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    static readonly IBrush KeyTop = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
    static readonly IBrush KeyPressed = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
    static readonly IBrush White = Brushes.White;
    static readonly Typeface Sans = new("Arial");

    MachineRunner? _runner;
    Skin _skin = Skin.Builtin16c();
    IBrush Body = Brushes.Black, Bezel = Brushes.Gray, Gold = Brushes.Goldenrod, Blue = Brushes.SkyBlue;

    public Skin Skin
    {
        get => _skin;
        set
        {
            _skin = value;
            static IBrush B(string c, IBrush fallback) =>
                Color.TryParse(c, out var col) ? new SolidColorBrush(col) : fallback;
            Body = B(value.Body, Brushes.Black);
            Bezel = B(value.Bezel, Brushes.Gray);
            Gold = B(value.FColor, Brushes.Goldenrod);
            Blue = B(value.GColor, Brushes.SkyBlue);
            InvalidateVisual();
        }
    }
    MachineRunner.Snapshot? _snap;
    int _pressed = -1;

    public MachineRunner? Runner
    {
        get => _runner;
        set { _runner = value; _snap = null; InvalidateVisual(); }
    }

    public CalculatorView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void Refresh()
    {
        if (_runner is null) return;
        _snap = _runner.Take();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? FaceW * 0.8 : availableSize.Width;
        return new Size(w, w * FaceH / FaceW);
    }

    (double Scale, double Ox, double Oy) Fit()
    {
        double s = Math.Min(Bounds.Width / FaceW, Bounds.Height / FaceH);
        return (s, (Bounds.Width - FaceW * s) / 2, (Bounds.Height - FaceH * s) / 2);
    }

    static Rect KeyRect(Skin.Key k) =>
        new(KeysLeft + k.Col * PitchX, KeysTop + k.Row * PitchY,
            KeyW, KeyH + (k.Rows - 1) * PitchY);

    public override void Render(DrawingContext ctx)
    {
        var (s, ox, oy) = Fit();
        using var _ = ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(ox, oy));

        ctx.DrawRectangle(Body, null, new Rect(0, 0, FaceW, FaceH), 22, 22);
        ctx.DrawRectangle(BezelDark, null, new Rect(14, 14, FaceW - 28, 178), 10, 10);
        ctx.DrawRectangle(Bezel, null, new Rect(18, 18, FaceW - 36, 170), 8, 8);
        var lcd = new Rect(150, 38, 560, 130);
        DrawLcd(ctx, lcd);
        DrawBadge(ctx, lcd, FaceW - 18, _skin.Label);

        foreach (var (row, first, last, label) in _skin.Brackets)
        {
            double x0 = KeysLeft + first * PitchX - 6, x1 = KeysLeft + last * PitchX + KeyW + 6;
            double y = KeysTop + row * PitchY - 30;
            var pen = new Pen(Gold, 1.6);
            ctx.DrawLine(pen, new Point(x0, y + 5), new Point(x0, y));
            ctx.DrawLine(pen, new Point(x1, y + 5), new Point(x1, y));
            ctx.DrawLine(pen, new Point(x0, y), new Point(x1, y));
            var ft = Format(label, 11, Gold, false);
            ctx.DrawRectangle(Body, null, new Rect((x0 + x1) / 2 - ft.Width / 2 - 4, y - 7, ft.Width + 8, 14));
            ctx.DrawText(ft, new Point((x0 + x1) / 2 - ft.Width / 2, y - ft.Height / 2));
        }

        foreach (var k in _skin.Keys) DrawKey(ctx, k);
    }

    static readonly IBrush PalmBlue = new SolidColorBrush(Color.FromRgb(0x00, 0x80, 0xFF)); // PALM_BLUE 0x041F
    static readonly FontFamily BadgeFont = new("Cascadia Mono,Consolas,Menlo,monospace");

    /// <summary>
    /// Palmarius badge as on our ESP32 10C: a black plate beside the LCD with the
    /// blue square centred in it and the model name in white below. Its sizes
    /// (LCD 52 high; plate 50x48, r4, 2 px inside the LCD's height; square 29x29,
    /// r8, 3 px line) scaled to this LCD. The plate is centred between the LCD
    /// and the bezel's right edge; square and name share equal top and bottom
    /// margins inside it.
    /// </summary>
    static void DrawBadge(DrawingContext ctx, Rect lcd, double bezelRight, string label)
    {
        double k = lcd.Height / 52;
        double h = 48 * k, w = 50 * k;
        double frameRight = lcd.Right + 4;
        double x = frameRight + (bezelRight - frameRight - w) / 2;
        double y = lcd.Y + (lcd.Height - h) / 2;
        ctx.DrawRectangle(Brushes.Black, null, new Rect(x, y, w, h), 4 * k, 4 * k);

        double sq = 29 * k, line = 3 * k, gap = 2.5 * k;
        var ft = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(BadgeFont, FontStyle.Normal, FontWeight.Bold),
            label.Length > 4 ? 7 * k : 8.5 * k, Brushes.White);
        double margin = (h - sq - gap - ft.Height) / 2;
        var square = new Rect(x + (w - sq) / 2, y + margin, sq, sq);
        ctx.DrawRectangle(null, new Pen(PalmBlue, line), square.Deflate(line / 2), 8 * k - line / 2, 8 * k - line / 2);
        ctx.DrawText(ft, new Point(x + (w - ft.Width) / 2, square.Bottom + gap));
    }

    void DrawKey(DrawingContext ctx, Skin.Key k)
    {
        var r = KeyRect(k);
        bool down = k.Code == _pressed;
        var cap = k.Code switch { 42 => Gold, 43 => Blue, _ => down ? KeyPressed : KeyCap };
        if (k.F.Length > 0) Text(ctx, k.F, r.Center.X, r.Top - 12, k.F.Length > 7 ? 10 : 12, Gold);
        ctx.DrawRectangle(Brushes.Black, null, r.Inflate(2), 6, 6);
        ctx.DrawRectangle(cap, null, r, 5, 5);
        double faceH = (k.Rows == 2 ? r.Height - 18 : 30);
        var face = new Rect(r.X + 3, r.Y + (down ? 4 : 2), r.Width - 6, faceH);
        if (k.Code is not (42 or 43)) ctx.DrawRectangle(down ? KeyCap : KeyTop, null, face, 4, 4);
        var mainInk = k.Code is 42 or 43 ? Brushes.Black : White;
        double size = k.Main.Length > 4 ? 13 : k.Main.Length > 3 ? 15 : 19;
        Text(ctx, k.Main, face.Center.X, face.Center.Y, size, mainInk, bold: true);
        if (k.G.Length > 0) Text(ctx, k.G, r.Center.X, r.Bottom - 8, 11.5, Blue);
    }

    // ------------------------------------------------------------- LCD
    void DrawLcd(DrawingContext ctx, Rect area)
    {
        ctx.DrawRectangle(Brushes.Black, null, area.Inflate(4), 4, 4);
        ctx.DrawRectangle(LcdBack, null, area, 3, 3);
        var segs = _snap?.Segments;
        bool on = _snap?.DisplayOn ?? false;
        double cw = 46, x0 = area.X + 26, y0 = area.Y + 20, h = 66;
        for (int d = 0; d < 11; d++)
        {
            var lit = new Func<char, bool>(c => on && segs is not null &&
                                              segs[d].TryGetValue(c, out var v) && v);
            double x = x0 + d * cw;
            if (d == 0)
            {
                DrawSeg(ctx, 'g', x + 8, y0, 26, h, lit('g'));
            }
            else
            {
                foreach (var c in "abcdefg") DrawSeg(ctx, c, x, y0, 30, h, lit(c));
                var dot = new Rect(x + 33, y0 + h - 6, 6, 6);
                ctx.DrawRectangle(lit('h') ? LcdInk : LcdGhost, null, dot);
                var comma = new StreamGeometry();
                using (var g = comma.Open())
                {
                    g.BeginFigure(new Point(x + 33, y0 + h + 1), true);
                    g.LineTo(new Point(x + 38, y0 + h + 1));
                    g.LineTo(new Point(x + 32, y0 + h + 10));
                    g.EndFigure(true);
                }
                ctx.DrawGeometry(lit('i') ? LcdInk : LcdGhost, null, comma);
            }
            if (lit('j') && d < _skin.Annunciators.Length && _skin.Annunciators[d].Length > 0)
                Text(ctx, _skin.Annunciators[d], x + 16, area.Bottom - 16, 12, LcdInk, bold: true);
        }
    }

    static void DrawSeg(DrawingContext ctx, char seg, double x, double y, double w, double h, bool on)
    {
        const double t = 6, slant = 5;
        double hm = h / 2;
        Point P(double px, double py) => new(x + px + slant * (1 - py / h), y + py);
        Point[] pts = seg switch
        {
            'a' => [P(t / 2 + 1, 0), P(w - t / 2 - 1, 0), P(w - t - 1, t), P(t + 1, t)],
            'b' => [P(w, 1), P(w, hm - 1), P(w - t, hm - t / 2 - 1), P(w - t, t + 1)],
            'c' => [P(w, hm + 1), P(w, h - 1), P(w - t, h - t - 1), P(w - t, hm + t / 2 + 1)],
            'd' => [P(t + 1, h - t), P(w - t - 1, h - t), P(w - t / 2 - 1, h), P(t / 2 + 1, h)],
            'e' => [P(0, hm + 1), P(t, hm + t / 2 + 1), P(t, h - t - 1), P(0, h - 1)],
            'f' => [P(0, 1), P(t, t + 1), P(t, hm - t / 2 - 1), P(0, hm - 1)],
            _ => [P(1, hm), P(t, hm - t / 2), P(w - t, hm - t / 2), P(w - 1, hm),
                  P(w - t, hm + t / 2), P(t, hm + t / 2)],
        };
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(pts[0], true);
            foreach (var p in pts.Skip(1)) g.LineTo(p);
            g.EndFigure(true);
        }
        ctx.DrawGeometry(on ? LcdInk : LcdGhost, null, geo);
    }

    // ------------------------------------------------------------ text
    static FormattedText Format(string s, double size, IBrush ink, bool bold) =>
        new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            bold ? new Typeface(Sans.FontFamily, FontStyle.Normal, FontWeight.Bold) : Sans, size, ink);

    static void Text(DrawingContext ctx, string s, double cx, double cy, double size, IBrush ink,
                     bool bold = false)
    {
        var ft = Format(s, size, ink, bold);
        ctx.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
    }

    // ----------------------------------------------------------- input
    int HitTest(Point p)
    {
        var (s, ox, oy) = Fit();
        var q = new Point((p.X - ox) / s, (p.Y - oy) / s);
        foreach (var k in _skin.Keys)
            if (KeyRect(k).Inflate(4).Contains(q)) return k.Code;
        return -1;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        int code = HitTest(e.GetPosition(this));
        if (code < 0 || _runner is null) return;
        _pressed = code;
        e.Pointer.Capture(this);
        _runner.Press(code);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed < 0) return;
        _pressed = -1;
        e.Pointer.Capture(null);
        _runner?.Release();
        InvalidateVisual();
    }

    /// <summary>Key from the PC keyboard (press now, release on key up).</summary>
    public void PressFromKeyboard(int code)
    {
        if (_runner is null || _pressed == code) return;
        _pressed = code;
        _runner.Press(code);
        InvalidateVisual();
    }

    public void ReleaseFromKeyboard()
    {
        if (_pressed < 0) return;
        _pressed = -1;
        _runner?.Release();
        InvalidateVisual();
    }
}

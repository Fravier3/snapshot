using FranCapture.Core.Localization;
using FranCaptureStudio.Interop;
using SD = System.Drawing;
using SD2 = System.Drawing.Drawing2D;
using WF = System.Windows.Forms;

namespace FranCaptureStudio.Capture;

public enum OverlayMode { Region, Window, ColorPicker, Ruler }
public enum OverlayPurpose { Capture, SelectOnly }
public enum OverlayAction { Cancel, Capture, Edit, Copy, SaveAs, Ocr, Pin, Record, Scroll, Accept, ColorPicked }

public sealed class OverlayResult : IDisposable
{
    public OverlayAction Action { get; init; }
    public SD.Rectangle ScreenRect { get; init; }
    public SD.Bitmap? Image { get; init; }
    public SD.Color Color { get; init; }
    public void Dispose() => Image?.Dispose();
}

public sealed class OverlayOptions
{
    public OverlayMode Mode { get; init; } = OverlayMode.Region;
    public OverlayPurpose Purpose { get; init; } = OverlayPurpose.Capture;
    public string AcceptLabel { get; init; } = "Aceptar";
    public SD.Rectangle? InitialSelection { get; init; }
    public bool IncludeCursor { get; init; }
}

/// <summary>
/// Selector a pantalla completa sobre una imagen congelada del escritorio virtual. Se implementa con WinForms
/// porque trabaja en píxeles físicos exactos, sin virtualización de DPI, en uno o varios monitores.
/// </summary>
public sealed class OverlayForm : WF.Form
{
    readonly OverlayOptions _opt;
    readonly SD.Rectangle _vb;                 // escritorio virtual en coordenadas de pantalla
    readonly SD.Bitmap _screen, _dim;
    readonly List<WindowInfo> _windows;
    SD.Rectangle _sel;                         // selección en coordenadas del formulario
    SD.Point _mouse, _dragStart;
    SD.Rectangle _selAtDrag;
    enum Drag { None, Creating, Moving, Resizing, Measuring }
    Drag _drag;
    int _handle = -1;
    readonly List<(OverlayAction Action, string Label, bool Primary, SD.Rectangle Bounds)> _buttons = [];
    SD.Point _rulerA, _rulerB; bool _hasRuler;
    WindowInfo? _hoverWindow;
    public OverlayResult Result { get; private set; } = new() { Action = OverlayAction.Cancel };

    static readonly SD.Color Accent = SD.Color.FromArgb(0, 184, 255);
    const int WM_DPICHANGED = 0x02E0;

    public OverlayForm(OverlayOptions options)
    {
        _opt = options;
        _vb = ScreenCapture.VirtualBounds;
        _windows = options.Mode == OverlayMode.Window ? ScreenCapture.TopLevelWindows() : [];
        using (var raw = ScreenCapture.Capture(_vb, options.IncludeCursor))
        {
            _screen = new SD.Bitmap(raw.Width, raw.Height, SD.Imaging.PixelFormat.Format32bppPArgb);
            using var g = SD.Graphics.FromImage(_screen);
            g.DrawImageUnscaled(raw, 0, 0);
        }
        _dim = new SD.Bitmap(_screen.Width, _screen.Height, SD.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = SD.Graphics.FromImage(_dim))
        {
            g.DrawImageUnscaled(_screen, 0, 0);
            int alpha = options.Mode is OverlayMode.Ruler or OverlayMode.ColorPicker ? 0 : 115;
            if (alpha > 0) { using var b = new SD.SolidBrush(SD.Color.FromArgb(alpha, 8, 10, 14)); g.FillRectangle(b, 0, 0, _dim.Width, _dim.Height); }
        }

        AutoScaleMode = WF.AutoScaleMode.None;
        FormBorderStyle = WF.FormBorderStyle.None;
        StartPosition = WF.FormStartPosition.Manual;
        Bounds = _vb;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        Cursor = WF.Cursors.Cross;
        Text = "Fran Capture Studio – selector";
        SetStyle(WF.ControlStyles.AllPaintingInWmPaint | WF.ControlStyles.UserPaint | WF.ControlStyles.OptimizedDoubleBuffer | WF.ControlStyles.Opaque, true);
        if (options.InitialSelection is { } init)
        {
            _sel = SD.Rectangle.Intersect(new SD.Rectangle(init.X - _vb.X, init.Y - _vb.Y, init.Width, init.Height), new SD.Rectangle(SD.Point.Empty, _vb.Size));
            LayoutButtons();
        }
        var p = WF.Cursor.Position;
        _mouse = new SD.Point(p.X - _vb.X, p.Y - _vb.Y);
    }

    protected override WF.CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; /* WS_EX_TOOLWINDOW: fuera de Alt+Tab */ return cp; }
    }

    protected override void WndProc(ref WF.Message m)
    {
        if (m.Msg == WM_DPICHANGED) { Bounds = _vb; return; } // conservar tamaño en píxeles físicos
        base.WndProc(ref m);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _vb;
        Native.SetForegroundWindow(Handle);
        Activate();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        // Si otra ventana roba el foco, el selector se cancela para no dejar la pantalla bloqueada.
        if (Visible && !_closing) BeginInvoke(() => { if (!_closing && WF.Form.ActiveForm != this) Finish(OverlayAction.Cancel); });
    }

    bool _closing;

    double Scale => Native.ScaleAt(_mouse.X + _vb.X, _mouse.Y + _vb.Y);

    // ---------- Entrada ----------

    protected override void OnKeyDown(WF.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == WF.Keys.Escape) { Finish(OverlayAction.Cancel); return; }
        if (_opt.Mode != OverlayMode.Region) return;
        bool has = _sel.Width > 0 && _sel.Height > 0;
        if (e.KeyCode == WF.Keys.Enter && has) { Finish(_opt.Purpose == OverlayPurpose.Capture ? OverlayAction.Capture : OverlayAction.Accept); return; }
        if (e.Control && e.KeyCode == WF.Keys.C && has && _opt.Purpose == OverlayPurpose.Capture) { Finish(OverlayAction.Copy); return; }
        if (e.Control && e.KeyCode == WF.Keys.A) { _sel = new SD.Rectangle(SD.Point.Empty, _vb.Size); LayoutButtons(); Invalidate(); return; }
        if (has && e.KeyCode is WF.Keys.Left or WF.Keys.Right or WF.Keys.Up or WF.Keys.Down)
        {
            int dx = e.KeyCode == WF.Keys.Left ? -1 : e.KeyCode == WF.Keys.Right ? 1 : 0;
            int dy = e.KeyCode == WF.Keys.Up ? -1 : e.KeyCode == WF.Keys.Down ? 1 : 0;
            if (e.Control) { dx *= 10; dy *= 10; }
            if (e.Shift) _sel = new SD.Rectangle(_sel.X, _sel.Y, Math.Max(1, _sel.Width + dx), Math.Max(1, _sel.Height + dy));
            else _sel.Offset(dx, dy);
            ClampSelection(); LayoutButtons(); Invalidate();
            e.Handled = true;
        }
    }

    protected override bool IsInputKey(WF.Keys keyData) => true;

    protected override void OnMouseDown(WF.MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _mouse = e.Location;
        if (e.Button == WF.MouseButtons.Right)
        {
            if (_opt.Mode == OverlayMode.Region && _sel.Width > 0) { _sel = SD.Rectangle.Empty; _buttons.Clear(); Invalidate(); }
            else if (_opt.Mode == OverlayMode.Ruler && _hasRuler) { _hasRuler = false; Invalidate(); }
            else Finish(OverlayAction.Cancel);
            return;
        }
        if (e.Button != WF.MouseButtons.Left) return;
        switch (_opt.Mode)
        {
            case OverlayMode.ColorPicker:
                Finish(OverlayAction.ColorPicked);
                return;
            case OverlayMode.Window:
                if (_hoverWindow != null) { _sel = ToForm(_hoverWindow.Bounds); Finish(OverlayAction.Capture); }
                return;
            case OverlayMode.Ruler:
                _rulerA = _rulerB = e.Location; _hasRuler = true; _drag = Drag.Measuring; Invalidate();
                return;
        }
        foreach (var b in _buttons)
            if (b.Bounds.Contains(e.Location)) { Finish(b.Action); return; }
        _dragStart = e.Location; _selAtDrag = _sel;
        _handle = HitHandle(e.Location);
        if (_handle >= 0) _drag = Drag.Resizing;
        else if (_sel.Contains(e.Location))
        {
            if (e.Clicks >= 2) { Finish(_opt.Purpose == OverlayPurpose.Capture ? OverlayAction.Capture : OverlayAction.Accept); return; }
            _drag = Drag.Moving;
        }
        else { _drag = Drag.Creating; _sel = SD.Rectangle.Empty; _buttons.Clear(); }
        Invalidate();
    }

    protected override void OnMouseMove(WF.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.Location;
        switch (_drag)
        {
            case Drag.Creating:
                _sel = Normalize(_dragStart, e.Location);
                break;
            case Drag.Moving:
                _sel = _selAtDrag; _sel.Offset(e.X - _dragStart.X, e.Y - _dragStart.Y); ClampSelection();
                break;
            case Drag.Resizing:
                _sel = ResizeBy(_selAtDrag, _handle, e.X - _dragStart.X, e.Y - _dragStart.Y);
                break;
            case Drag.Measuring:
                _rulerB = e.Location;
                if ((WF.Control.ModifierKeys & WF.Keys.Shift) != 0)
                    _rulerB = Math.Abs(_rulerB.X - _rulerA.X) >= Math.Abs(_rulerB.Y - _rulerA.Y) ? new SD.Point(_rulerB.X, _rulerA.Y) : new SD.Point(_rulerA.X, _rulerB.Y);
                break;
            default:
                UpdateHover(e.Location);
                break;
        }
        Invalidate();
    }

    protected override void OnMouseUp(WF.MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != WF.MouseButtons.Left) return;
        if (_drag == Drag.Creating && (_sel.Width < 3 || _sel.Height < 3)) _sel = SD.Rectangle.Empty;
        if (_drag is Drag.Creating or Drag.Moving or Drag.Resizing) LayoutButtons();
        _drag = Drag.None;
        UpdateHover(e.Location);
        Invalidate();
    }

    void UpdateHover(SD.Point p)
    {
        if (_opt.Mode == OverlayMode.Window)
        {
            var sp = new SD.Point(p.X + _vb.X, p.Y + _vb.Y);
            _hoverWindow = _windows.FirstOrDefault(w => w.Bounds.Contains(sp));
            Cursor = WF.Cursors.Hand;
            return;
        }
        if (_opt.Mode != OverlayMode.Region) { Cursor = WF.Cursors.Cross; return; }
        if (_buttons.Any(b => b.Bounds.Contains(p))) { Cursor = WF.Cursors.Hand; return; }
        int h = HitHandle(p);
        Cursor = h switch
        {
            0 or 7 => WF.Cursors.SizeNWSE,
            2 or 5 => WF.Cursors.SizeNESW,
            1 or 6 => WF.Cursors.SizeNS,
            3 or 4 => WF.Cursors.SizeWE,
            _ => _sel.Contains(p) ? WF.Cursors.SizeAll : WF.Cursors.Cross,
        };
    }

    // ---------- Geometría ----------

    static SD.Rectangle Normalize(SD.Point a, SD.Point b) =>
        SD.Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    SD.Rectangle ToForm(SD.Rectangle screen) =>
        SD.Rectangle.Intersect(new SD.Rectangle(screen.X - _vb.X, screen.Y - _vb.Y, screen.Width, screen.Height), new SD.Rectangle(SD.Point.Empty, _vb.Size));

    void ClampSelection()
    {
        int w = Math.Min(_sel.Width, _vb.Width), h = Math.Min(_sel.Height, _vb.Height);
        _sel = new SD.Rectangle(Math.Clamp(_sel.X, 0, _vb.Width - w), Math.Clamp(_sel.Y, 0, _vb.Height - h), w, h);
    }

    SD.Rectangle[] Handles()
    {
        int s = (int)Math.Round(9 * Scale), h = s / 2;
        int l = _sel.Left, t = _sel.Top, r = _sel.Right, b = _sel.Bottom, cx = (l + r) / 2, cy = (t + b) / 2;
        return
        [
            new(l - h, t - h, s, s), new(cx - h, t - h, s, s), new(r - h, t - h, s, s),
            new(l - h, cy - h, s, s), new(r - h, cy - h, s, s),
            new(l - h, b - h, s, s), new(cx - h, b - h, s, s), new(r - h, b - h, s, s),
        ];
    }

    int HitHandle(SD.Point p)
    {
        if (_sel.Width <= 0) return -1;
        var hs = Handles();
        for (int i = 0; i < hs.Length; i++) { var r = hs[i]; r.Inflate(3, 3); if (r.Contains(p)) return i; }
        return -1;
    }

    SD.Rectangle ResizeBy(SD.Rectangle r, int handle, int dx, int dy)
    {
        int l = r.Left, t = r.Top, rr = r.Right, b = r.Bottom;
        if (handle is 0 or 3 or 5) l += dx;
        if (handle is 2 or 4 or 7) rr += dx;
        if (handle is 0 or 1 or 2) t += dy;
        if (handle is 5 or 6 or 7) b += dy;
        var n = SD.Rectangle.FromLTRB(Math.Min(l, rr), Math.Min(t, b), Math.Max(l, rr), Math.Max(t, b));
        n.Intersect(new SD.Rectangle(SD.Point.Empty, _vb.Size));
        if (n.Width < 1) n.Width = 1;
        if (n.Height < 1) n.Height = 1;
        return n;
    }

    SD.Rectangle MonitorOf(SD.Rectangle formRect)
    {
        var center = new SD.Point(formRect.X + formRect.Width / 2 + _vb.X, formRect.Y + formRect.Height / 2 + _vb.Y);
        var b = WF.Screen.FromPoint(center).Bounds;
        return new SD.Rectangle(b.X - _vb.X, b.Y - _vb.Y, b.Width, b.Height);
    }

    void LayoutButtons()
    {
        _buttons.Clear();
        if (_opt.Mode != OverlayMode.Region || _sel.Width <= 0 || _sel.Height <= 0) return;
        var defs = _opt.Purpose == OverlayPurpose.Capture
            ? new (OverlayAction, string, bool)[]
            {
                (OverlayAction.Capture, "✓  " + Loc.T("Capturar"), true), (OverlayAction.Edit, Loc.T("Editar"), false), (OverlayAction.Copy, Loc.T("Copiar"), false),
                (OverlayAction.SaveAs, Loc.T("Guardar como…"), false), (OverlayAction.Ocr, "OCR", false), (OverlayAction.Pin, Loc.T("Fijar"), false),
                (OverlayAction.Scroll, Loc.T("Extendida"), false), (OverlayAction.Record, "●  " + Loc.T("Grabar"), false), (OverlayAction.Cancel, "✕", false),
            }
            : [(OverlayAction.Accept, "✓  " + Loc.T(_opt.AcceptLabel), true), (OverlayAction.Cancel, "✕  " + Loc.T("Cancelar"), false)];
        double sc = Native.ScaleAt(_sel.X + _sel.Width / 2 + _vb.X, _sel.Y + _sel.Height / 2 + _vb.Y);
        using var font = new SD.Font("Segoe UI", (float)(12.5 * sc), SD.FontStyle.Regular, SD.GraphicsUnit.Pixel);
        using var g = CreateGraphics();
        int pad = (int)(11 * sc), gap = (int)(4 * sc), height = (int)(32 * sc), margin = (int)(8 * sc);
        var sizes = defs.Select(d => (int)Math.Ceiling(g.MeasureString(d.Item2, font).Width) + pad * 2).ToArray();
        int total = sizes.Sum() + gap * (defs.Length - 1) + margin;
        var mon = MonitorOf(_sel);
        int x = Math.Clamp(_sel.Right - total, mon.Left + margin, Math.Max(mon.Left + margin, mon.Right - total - margin));
        int y = _sel.Bottom + margin;
        if (y + height + margin > mon.Bottom) y = _sel.Top - height - margin - (int)(22 * sc);
        if (y < mon.Top + margin) y = Math.Max(mon.Top + margin, _sel.Bottom - height - margin);
        for (int i = 0; i < defs.Length; i++)
        {
            _buttons.Add((defs[i].Item1, defs[i].Item2, defs[i].Item3, new SD.Rectangle(x, y, sizes[i], height)));
            x += sizes[i] + gap;
        }
    }

    // ---------- Pintado ----------

    protected override void OnPaint(WF.PaintEventArgs e)
    {
        var g = e.Graphics;
        g.CompositingMode = SD2.CompositingMode.SourceCopy;
        g.DrawImageUnscaled(_dim, 0, 0);
        bool has = _sel.Width > 0 && _sel.Height > 0;
        if (has) g.DrawImage(_screen, _sel, _sel, SD.GraphicsUnit.Pixel);
        g.CompositingMode = SD2.CompositingMode.SourceOver;
        double sc = Scale;
        using var font = new SD.Font("Segoe UI", (float)(12.5 * sc), SD.FontStyle.Regular, SD.GraphicsUnit.Pixel);
        using var bold = new SD.Font("Segoe UI", (float)(12.5 * sc), SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);

        switch (_opt.Mode)
        {
            case OverlayMode.Region: PaintRegion(g, font, bold, has, sc); break;
            case OverlayMode.Window: PaintWindow(g, font); break;
            case OverlayMode.ColorPicker: PaintLoupe(g, font, sc, true); PaintHint(g, font, Loc.T("Haz clic para copiar el color · Esc para salir")); break;
            case OverlayMode.Ruler: PaintRuler(g, font, sc); break;
        }
    }

    void PaintRegion(SD.Graphics g, SD.Font font, SD.Font bold, bool has, double sc)
    {
        if (!has)
        {
            using var guide = new SD.Pen(SD.Color.FromArgb(150, Accent), 1);
            g.DrawLine(guide, 0, _mouse.Y, Width, _mouse.Y);
            g.DrawLine(guide, _mouse.X, 0, _mouse.X, Height);
            PaintLoupe(g, font, sc, false);
            PaintHint(g, font, Loc.T("Arrastra para seleccionar una región · Esc para cancelar"));
            return;
        }
        using (var pen = new SD.Pen(Accent, Math.Max(1f, (float)sc))) g.DrawRectangle(pen, _sel.X, _sel.Y, _sel.Width - 1, _sel.Height - 1);
        using (var hb = new SD.SolidBrush(SD.Color.White))
        using (var hp = new SD.Pen(Accent, 1))
            foreach (var h in Handles()) { g.FillRectangle(hb, h); g.DrawRectangle(hp, h); }

        string dims = $"{_sel.Width} × {_sel.Height} px";
        var size = g.MeasureString(dims, bold);
        int lx = _sel.X, ly = _sel.Y - (int)size.Height - (int)(8 * sc);
        if (ly < MonitorOf(_sel).Top + 2) ly = _sel.Y + (int)(6 * sc);
        var lab = new SD.RectangleF(lx, ly, size.Width + (float)(12 * sc), size.Height + (float)(4 * sc));
        using (var bg = new SD.SolidBrush(SD.Color.FromArgb(235, 20, 23, 28))) FillRound(g, bg, SD.Rectangle.Round(lab), (int)(5 * sc));
        g.DrawString(dims, bold, SD.Brushes.White, lab.X + (float)(6 * sc), lab.Y + (float)(2 * sc));

        if (_drag == Drag.Creating || _drag == Drag.Resizing) PaintLoupe(g, font, sc, false);
        if (_drag == Drag.None)
        {
            if (_buttons.Count > 0)
            {
                var all = SD.Rectangle.FromLTRB(_buttons[0].Bounds.Left, _buttons[0].Bounds.Top, _buttons[^1].Bounds.Right, _buttons[0].Bounds.Bottom);
                all.Inflate((int)(5 * sc), (int)(5 * sc));
                using var bar = new SD.SolidBrush(SD.Color.FromArgb(245, 24, 27, 33));
                using var barPen = new SD.Pen(SD.Color.FromArgb(70, 78, 92));
                FillRound(g, bar, all, (int)(9 * sc), barPen);
            }
            foreach (var b in _buttons)
            {
                bool hover = b.Bounds.Contains(_mouse);
                var fill = b.Primary ? (hover ? SD.Color.FromArgb(60, 205, 255) : Accent) : hover ? SD.Color.FromArgb(58, 64, 76) : SD.Color.FromArgb(38, 42, 50);
                using var bb = new SD.SolidBrush(fill);
                FillRound(g, bb, b.Bounds, (int)(6 * sc));
                using var fmt = new SD.StringFormat { Alignment = SD.StringAlignment.Center, LineAlignment = SD.StringAlignment.Center, FormatFlags = SD.StringFormatFlags.NoWrap };
                using var tb = new SD.SolidBrush(b.Primary ? SD.Color.FromArgb(6, 20, 30) : SD.Color.FromArgb(236, 239, 244));
                g.DrawString(b.Label, b.Primary ? bold : font, tb, b.Bounds, fmt);
            }
        }
    }

    void PaintWindow(SD.Graphics g, SD.Font font)
    {
        if (_hoverWindow != null)
        {
            var r = ToForm(_hoverWindow.Bounds);
            g.CompositingMode = SD2.CompositingMode.SourceCopy;
            g.DrawImage(_screen, r, r, SD.GraphicsUnit.Pixel);
            g.CompositingMode = SD2.CompositingMode.SourceOver;
            using var pen = new SD.Pen(Accent, 3);
            g.DrawRectangle(pen, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
            PaintHint(g, font, $"{_hoverWindow.Title}  ·  {r.Width} × {r.Height} px");
        }
        else PaintHint(g, font, Loc.T("Haz clic en la ventana que quieres capturar · Esc para cancelar"));
    }

    void PaintRuler(SD.Graphics g, SD.Font font, double sc)
    {
        using var guide = new SD.Pen(SD.Color.FromArgb(190, Accent), 1) { DashStyle = SD2.DashStyle.Dash };
        g.DrawLine(guide, 0, _mouse.Y, Width, _mouse.Y);
        g.DrawLine(guide, _mouse.X, 0, _mouse.X, Height);
        string info = $"X {_mouse.X + _vb.X}   Y {_mouse.Y + _vb.Y}";
        if (_hasRuler)
        {
            using var pen = new SD.Pen(SD.Color.FromArgb(255, 69, 58), Math.Max(2f, (float)(2 * sc)));
            g.SmoothingMode = SD2.SmoothingMode.AntiAlias;
            g.DrawLine(pen, _rulerA, _rulerB);
            using var dot = new SD.SolidBrush(SD.Color.White);
            foreach (var p in new[] { _rulerA, _rulerB }) g.FillEllipse(dot, p.X - 3, p.Y - 3, 6, 6);
            g.SmoothingMode = SD2.SmoothingMode.None;
            int dx = Math.Abs(_rulerB.X - _rulerA.X), dy = Math.Abs(_rulerB.Y - _rulerA.Y);
            info = $"ΔX {dx} px   ΔY {dy} px   {Loc.T("Distancia")} {Math.Sqrt(dx * dx + dy * dy):0.#} px";
        }
        var size = g.MeasureString(info, font);
        int x = _mouse.X + (int)(16 * sc), y = _mouse.Y + (int)(16 * sc);
        var mon = MonitorOf(new SD.Rectangle(_mouse, new SD.Size(1, 1)));
        if (x + size.Width + 12 > mon.Right) x = _mouse.X - (int)size.Width - (int)(24 * sc);
        if (y + size.Height + 8 > mon.Bottom) y = _mouse.Y - (int)size.Height - (int)(20 * sc);
        using var bg = new SD.SolidBrush(SD.Color.FromArgb(240, 20, 23, 28));
        FillRound(g, bg, new SD.Rectangle(x, y, (int)size.Width + 12, (int)size.Height + 6), 5);
        g.DrawString(info, font, SD.Brushes.White, x + 6, y + 3);
        if (!_hasRuler) PaintHint(g, font, Loc.T("Arrastra para medir (Shift = recta) · Esc para salir"));
    }

    SD.Color ColorUnderMouse()
    {
        int x = Math.Clamp(_mouse.X, 0, _screen.Width - 1), y = Math.Clamp(_mouse.Y, 0, _screen.Height - 1);
        return _screen.GetPixel(x, y);
    }

    void PaintLoupe(SD.Graphics g, SD.Font font, double sc, bool showColorAlways)
    {
        const int srcN = 15;
        int cell = (int)Math.Round(8 * sc), size = srcN * cell;
        int textH = (int)(font.GetHeight(g) * 2 + 10 * sc);
        int x = _mouse.X + (int)(22 * sc), y = _mouse.Y + (int)(22 * sc);
        var mon = MonitorOf(new SD.Rectangle(_mouse, new SD.Size(1, 1)));
        if (x + size + 4 > mon.Right) x = _mouse.X - size - (int)(22 * sc);
        if (y + size + textH + 4 > mon.Bottom) y = _mouse.Y - size - textH - (int)(22 * sc);
        var dst = new SD.Rectangle(x, y, size, size);
        var src = new SD.Rectangle(_mouse.X - srcN / 2, _mouse.Y - srcN / 2, srcN, srcN);
        using (var bg = new SD.SolidBrush(SD.Color.FromArgb(20, 23, 28))) g.FillRectangle(bg, x - 2, y - 2, size + 4, size + textH + 4);
        var oldI = g.InterpolationMode; var oldP = g.PixelOffsetMode;
        g.InterpolationMode = SD2.InterpolationMode.NearestNeighbor; g.PixelOffsetMode = SD2.PixelOffsetMode.Half;
        g.DrawImage(_screen, dst, src, SD.GraphicsUnit.Pixel);
        g.InterpolationMode = oldI; g.PixelOffsetMode = oldP;
        using (var grid = new SD.Pen(SD.Color.FromArgb(40, 255, 255, 255)))
            for (int i = 1; i < srcN; i++) { g.DrawLine(grid, x + i * cell, y, x + i * cell, y + size); g.DrawLine(grid, x, y + i * cell, x + size, y + i * cell); }
        using (var center = new SD.Pen(Accent, Math.Max(1f, (float)sc))) g.DrawRectangle(center, x + (srcN / 2) * cell, y + (srcN / 2) * cell, cell, cell);
        using (var border = new SD.Pen(SD.Color.FromArgb(90, 98, 112))) g.DrawRectangle(border, x - 2, y - 2, size + 3, size + textH + 3);
        var c = ColorUnderMouse();
        string l1 = $"{_mouse.X + _vb.X}, {_mouse.Y + _vb.Y}", l2 = $"#{c.R:X2}{c.G:X2}{c.B:X2}  ·  {c.R}, {c.G}, {c.B}";
        float ty = y + size + (float)(4 * sc);
        g.DrawString(l1, font, SD.Brushes.Gainsboro, x + 2, ty);
        using (var sw = new SD.SolidBrush(c)) g.FillRectangle(sw, x + size - (int)(16 * sc), (int)ty + 2, (int)(14 * sc), (int)(12 * sc));
        g.DrawString(l2, font, SD.Brushes.White, x + 2, ty + font.GetHeight(g));
    }

    void PaintHint(SD.Graphics g, SD.Font font, string text)
    {
        var mon = MonitorOf(new SD.Rectangle(_mouse, new SD.Size(1, 1)));
        var size = g.MeasureString(text, font);
        int w = (int)size.Width + 28, h = (int)size.Height + 14;
        var r = new SD.Rectangle(mon.Left + (mon.Width - w) / 2, mon.Top + 28, w, h);
        if (r.Contains(_mouse)) r.Y = mon.Bottom - h - 60;
        using var bg = new SD.SolidBrush(SD.Color.FromArgb(235, 20, 23, 28));
        FillRound(g, bg, r, h / 2);
        g.DrawString(text, font, SD.Brushes.White, r.X + 14, r.Y + 7);
    }

    static void FillRound(SD.Graphics g, SD.Brush brush, SD.Rectangle r, int radius, SD.Pen? pen = null)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        radius = Math.Max(1, Math.Min(radius, Math.Min(r.Width, r.Height) / 2));
        using var path = new SD2.GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        var old = g.SmoothingMode; g.SmoothingMode = SD2.SmoothingMode.AntiAlias;
        g.FillPath(brush, path);
        if (pen != null) g.DrawPath(pen, path);
        g.SmoothingMode = old;
    }

    // ---------- Resultado ----------

    void Finish(OverlayAction action)
    {
        if (_closing) return;
        _closing = true;
        if (action == OverlayAction.ColorPicked)
            Result = new OverlayResult { Action = action, Color = ColorUnderMouse(), ScreenRect = new SD.Rectangle(_mouse.X + _vb.X, _mouse.Y + _vb.Y, 1, 1) };
        else if (action != OverlayAction.Cancel && _sel.Width > 0 && _sel.Height > 0)
        {
            var r = SD.Rectangle.Intersect(_sel, new SD.Rectangle(SD.Point.Empty, _vb.Size));
            var crop = _screen.Clone(r, SD.Imaging.PixelFormat.Format32bppRgb);
            Result = new OverlayResult { Action = action, ScreenRect = new SD.Rectangle(r.X + _vb.X, r.Y + _vb.Y, r.Width, r.Height), Image = crop };
        }
        else Result = new OverlayResult { Action = OverlayAction.Cancel };
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _screen.Dispose(); _dim.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>Muestra el selector de forma modal y devuelve el resultado.</summary>
    public static OverlayResult Show(OverlayOptions options)
    {
        using var f = new OverlayForm(options);
        f.ShowDialog();
        return f.Result;
    }

    // Acceso para pruebas de integración
    internal void TestSelect(SD.Rectangle formRect) { _sel = formRect; LayoutButtons(); Invalidate(); }
    internal void TestFinish(OverlayAction a) => Finish(a);
    internal IReadOnlyList<OverlayAction> TestButtons => _buttons.Select(b => b.Action).ToList();
}

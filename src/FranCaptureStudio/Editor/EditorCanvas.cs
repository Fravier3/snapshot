using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FranCaptureStudio.Editor;

public enum EditorTool { Select, Crop, Pen, Line, Arrow, Rect, Ellipse, Text, Step, Highlight, Callout, Pixelate, Blur, Redact }

/// <summary>
/// Lienzo del editor. Mide en píxeles de imagen (la ventana aplica el zoom con un LayoutTransform).
/// Gestiona el dibujo, la selección, el movimiento y el redimensionado de anotaciones.
/// </summary>
public sealed class EditorCanvas : FrameworkElement
{
    public EditorState State { get; private set; } = new() { Base = Empty(), Annotations = [] };
    public EditorHistory History { get; } = new();
    public AnnotationRenderer Renderer { get; } = new();
    public Annotation? Selected { get; private set; }
    public EditorTool Tool { get; set; } = EditorTool.Select;
    public Color CurrentColor { get; set; } = Color.FromRgb(255, 59, 48);
    public double CurrentThickness { get; set; } = 4;
    public double CurrentFontSize { get; set; } = 22;
    public bool CurrentFilled { get; set; }
    public int CurrentStrength { get; set; } = 12;
    public double Zoom { get; set; } = 1;            // para dibujar tiradores de tamaño constante en pantalla
    public Rect? CropRect { get; private set; }

    public event Action? Changed;                     // el documento cambió (marca como modificado)
    public event Action? SelectionChanged;
    public event Action<Point?>? PointerMoved;
    public event Action<Annotation, bool>? TextEditRequested;  // (anotación, esNueva)
    public event Action? CropChanged;

    Annotation? _draft;
    Point _start, _last;
    enum DragMode { None, Draw, Move, HandleA, HandleB, HandleTail, Corner, Crop }
    DragMode _drag;
    int _corner;
    bool _pushedForDrag;

    static BitmapSource Empty() => Services.Img.FromPixels(new byte[4 * 4 * 4], 4, 4);

    public EditorCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    public int ImageWidth => State.Base.PixelWidth;
    public int ImageHeight => State.Base.PixelHeight;

    protected override Size MeasureOverride(Size availableSize) => new(ImageWidth, ImageHeight);
    protected override Size ArrangeOverride(Size finalSize) => new(ImageWidth, ImageHeight);

    // ---------- Documento ----------

    public void Load(EditorState state, bool resetHistory = true)
    {
        State = state;
        if (resetHistory) History.Clear();
        Select(null);
        CropRect = null;
        InvalidateMeasure(); InvalidateVisual();
    }

    void PushUndo() => History.Push(State);

    void Touch()
    {
        InvalidateVisual();
        Changed?.Invoke();
    }

    /// <summary>Aplica un cambio registrándolo en el historial.</summary>
    public void Mutate(Action change)
    {
        PushUndo();
        change();
        Touch();
    }

    public void ReplaceBase(BitmapSource newBase, Func<Annotation, bool>? transform = null)
    {
        PushUndo();
        var anns = State.Annotations;
        if (transform != null) anns = anns.Where(transform).ToList();
        State = new EditorState { Base = newBase, Annotations = anns };
        CropRect = null;
        if (Selected != null && !anns.Contains(Selected)) Select(null);
        InvalidateMeasure(); Touch();
    }

    /// <summary>Funde las anotaciones en la imagen (necesario antes de rotar, voltear o redimensionar).</summary>
    public BitmapSource FlattenNow() => Renderer.Flatten(State);

    public void Undo() { if (History.Undo(State) is { } s) Restore(s); }
    public void Redo() { if (History.Redo(State) is { } s) Restore(s); }

    void Restore(EditorState s)
    {
        State = s; CropRect = null; _draft = null; _drag = DragMode.None;
        Select(null);
        InvalidateMeasure(); Touch();
    }

    public void Select(Annotation? a)
    {
        if (Selected == a) return;
        Selected = a;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void Add(Annotation a, bool select = true)
    {
        PushUndo();
        State.Annotations.Add(a);
        if (select) Select(a);
        Touch();
    }

    public void DeleteSelected()
    {
        if (Selected == null) return;
        var s = Selected;
        Mutate(() => State.Annotations.Remove(s));
        Select(null);
    }

    public void DuplicateSelected()
    {
        if (Selected == null) return;
        var d = Selected.Duplicate();
        Add(d);
    }

    public void BringSelectedToFront()
    {
        if (Selected == null) return;
        var s = Selected;
        Mutate(() => { State.Annotations.Remove(s); State.Annotations.Add(s); });
    }

    public int NextStepNumber() => State.Annotations.Where(a => a.Kind == AnnKind.Step).Select(a => a.Number).DefaultIfEmpty(0).Max() + 1;

    public void ApplyCrop()
    {
        if (CropRect is not { } r) return;
        var ir = new Int32Rect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
        if (ir.Width < 2 || ir.Height < 2) { CancelCrop(); return; }
        var cropped = Services.Img.Crop(State.Base, ir);
        PushUndo();
        var offset = new Vector(-ir.X, -ir.Y);
        var anns = State.Annotations.Select(a => { var c = a.Clone(); c.Move(offset); return c; }).ToList();
        State = new EditorState { Base = cropped, Annotations = anns };
        CropRect = null; Select(null);
        InvalidateMeasure(); Touch(); CropChanged?.Invoke();
    }

    public void CancelCrop()
    {
        CropRect = null; InvalidateVisual(); CropChanged?.Invoke();
    }

    // ---------- Pintado ----------

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ImageWidth, ImageHeight);
        dc.DrawImage(State.Base, full);
        dc.PushClip(new RectangleGeometry(full));
        foreach (var a in State.Annotations) Renderer.Draw(dc, a, State.Base);
        if (_draft != null) Renderer.Draw(dc, _draft, State.Base);
        dc.Pop();

        double px = 1 / Math.Max(0.05, Zoom);
        if (CropRect is { } cr)
        {
            var shade = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(cr));
            dc.DrawGeometry(shade, null, outside);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(0, 184, 255)), 1.5 * px), cr);
        }
        if (Selected != null && State.Annotations.Contains(Selected))
        {
            var accent = new SolidColorBrush(Color.FromRgb(0, 184, 255));
            var dash = new Pen(accent, 1.2 * px) { DashStyle = new DashStyle([4, 3], 0) };
            var b = Selected.Bounds;
            if (!Selected.IsLine && !b.IsEmpty) { b.Inflate(3 * px, 3 * px); dc.DrawRectangle(null, dash, b); }
            foreach (var h in HandlePoints(Selected))
                dc.DrawEllipse(Brushes.White, new Pen(accent, 1.5 * px), h, 5 * px, 5 * px);
        }
    }

    IEnumerable<Point> HandlePoints(Annotation a)
    {
        if (a.IsLine) { yield return a.A; yield return a.B; }
        else if (a.IsBox)
        {
            var r = a.Bounds;
            yield return r.TopLeft; yield return r.TopRight; yield return r.BottomLeft; yield return r.BottomRight;
            if (a.Kind == AnnKind.Callout) yield return a.Tail;
        }
    }

    // ---------- Interacción ----------

    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, ImageWidth), Math.Clamp(p.Y, 0, ImageHeight));

    double Tol => 7 / Math.Max(0.05, Zoom);

    static double DistToSegment(Point p, Point a, Point b)
    {
        var ab = b - a; var ap = p - a;
        double len2 = ab.LengthSquared;
        double t = len2 < 1e-9 ? 0 : Math.Clamp(Vector.Multiply(ap, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    public Annotation? HitTest(Point p)
    {
        for (int i = State.Annotations.Count - 1; i >= 0; i--)
        {
            var a = State.Annotations[i];
            double tol = Tol + a.Thickness / 2;
            bool hit = a.Kind switch
            {
                AnnKind.Line or AnnKind.Arrow => DistToSegment(p, a.A, a.B) <= tol,
                AnnKind.Pen => a.Points.Count == 1 ? (p - a.Points[0]).Length <= tol
                    : Enumerable.Range(0, Math.Max(0, a.Points.Count - 1)).Any(k => DistToSegment(p, a.Points[k], a.Points[k + 1]) <= tol),
                AnnKind.Rect or AnnKind.Ellipse when !a.Filled => Near(a.Bounds, p, tol),
                _ => Inflate(a.Bounds, Tol / 2).Contains(p),
            };
            if (hit) return a;
        }
        return null;
    }

    static Rect Inflate(Rect r, double d) { if (r.IsEmpty) return r; r.Inflate(d, d); return r; }

    static bool Near(Rect r, Point p, double tol)
    {
        if (!Inflate(r, tol).Contains(p)) return false;
        var inner = r; inner.Inflate(-Math.Min(tol + 6, r.Width / 2), -Math.Min(tol + 6, r.Height / 2));
        return inner.Width < 12 || inner.Height < 12 || !inner.Contains(p);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        _start = _last = p; _pushedForDrag = false;

        // Tiradores de la anotación seleccionada (en cualquier herramienta)
        if (Selected != null)
        {
            var hs = HandlePoints(Selected).ToList();
            for (int i = 0; i < hs.Count; i++)
            {
                if ((hs[i] - p).Length > Tol + 3) continue;
                _drag = Selected.IsLine ? (i == 0 ? DragMode.HandleA : DragMode.HandleB) : (i == 4 ? DragMode.HandleTail : DragMode.Corner);
                _corner = i;
                CaptureMouse();
                return;
            }
        }

        switch (Tool)
        {
            case EditorTool.Select:
            {
                var hit = HitTest(p);
                Select(hit);
                if (hit != null)
                {
                    if (e.ClickCount == 2 && (hit.HasText || hit.Kind == AnnKind.Step)) { RequestEdit(hit); return; }
                    _drag = DragMode.Move; CaptureMouse();
                }
                break;
            }
            case EditorTool.Crop:
                CropRect = null; _drag = DragMode.Crop; CaptureMouse(); CropChanged?.Invoke();
                break;
            case EditorTool.Text:
            {
                var hit = HitTest(p);
                if (hit is { HasText: true }) { Select(hit); TextEditRequested?.Invoke(hit, false); return; }
                var a = NewAnn(AnnKind.Text, Clamp(p));
                TextEditRequested?.Invoke(a, true);
                break;
            }
            case EditorTool.Step:
            {
                var a = NewAnn(AnnKind.Step, Clamp(p));
                a.Number = NextStepNumber();
                Add(a);
                break;
            }
            default:
            {
                var kind = Tool switch
                {
                    EditorTool.Pen => AnnKind.Pen, EditorTool.Line => AnnKind.Line, EditorTool.Arrow => AnnKind.Arrow, EditorTool.Rect => AnnKind.Rect,
                    EditorTool.Ellipse => AnnKind.Ellipse, EditorTool.Highlight => AnnKind.Highlight, EditorTool.Callout => AnnKind.Callout,
                    EditorTool.Pixelate => AnnKind.Pixelate, EditorTool.Blur => AnnKind.Blur, _ => AnnKind.Redact,
                };
                _draft = NewAnn(kind, Clamp(p));
                if (kind == AnnKind.Pen) _draft.Points.Add(Clamp(p));
                Select(null);
                _drag = DragMode.Draw; CaptureMouse();
                break;
            }
        }
    }

    void RequestEdit(Annotation a)
    {
        if (a.Kind == AnnKind.Step)
        {
            var txt = Views.Ui.Prompt("Número de paso", "Número:", a.Number.ToString(), Window.GetWindow(this));
            if (txt != null && int.TryParse(txt.Trim(), out var n) && n >= 0) Mutate(() => a.Number = n);
        }
        else TextEditRequested?.Invoke(a, false);
    }

    Annotation NewAnn(AnnKind kind, Point at) => new()
    {
        Kind = kind, A = at, B = at, Tail = at,
        Color = kind == AnnKind.Highlight ? Color.FromRgb(255, 221, 0) : kind == AnnKind.Redact ? Colors.Black : CurrentColor,
        Thickness = CurrentThickness, FontSize = CurrentFontSize, Filled = CurrentFilled, Strength = CurrentStrength,
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        PointerMoved?.Invoke(new Rect(0, 0, ImageWidth, ImageHeight).Contains(p) ? p : null);
        if (_drag == DragMode.None) { UpdateCursor(p); return; }
        var c = Clamp(p);
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (_drag)
        {
            case DragMode.Draw when _draft != null:
                if (_draft.Kind == AnnKind.Pen) { if ((c - _draft.Points[^1]).Length >= 1.2) _draft.Points.Add(c); }
                else if (_draft.IsLine && shift) _draft.B = SnapAngle(_draft.A, c);
                else if (shift && _draft.Kind is AnnKind.Rect or AnnKind.Ellipse)
                {
                    double s = Math.Max(Math.Abs(c.X - _draft.A.X), Math.Abs(c.Y - _draft.A.Y));
                    _draft.B = new Point(_draft.A.X + Math.Sign(c.X - _draft.A.X) * s, _draft.A.Y + Math.Sign(c.Y - _draft.A.Y) * s);
                }
                else _draft.B = c;
                InvalidateVisual();
                break;
            case DragMode.Crop:
                CropRect = new Rect(Clamp(_start), c);
                InvalidateVisual(); CropChanged?.Invoke();
                break;
            case DragMode.Move when Selected != null:
                EnsureDragUndo();
                Selected.Move(p - _last);
                Touch();
                break;
            case DragMode.HandleA when Selected != null: EnsureDragUndo(); Selected.A = shift ? SnapAngle(Selected.B, c) : c; Touch(); break;
            case DragMode.HandleB when Selected != null: EnsureDragUndo(); Selected.B = shift ? SnapAngle(Selected.A, c) : c; Touch(); break;
            case DragMode.HandleTail when Selected != null: EnsureDragUndo(); Selected.Tail = c; Touch(); break;
            case DragMode.Corner when Selected != null:
            {
                EnsureDragUndo();
                var r = Selected.Bounds;
                // la esquina opuesta permanece fija
                Point fixedPt = _corner switch { 0 => r.BottomRight, 1 => r.BottomLeft, 2 => r.TopRight, _ => r.TopLeft };
                Selected.A = fixedPt; Selected.B = c;
                var nr = new Rect(fixedPt, c);
                _corner = c.X <= fixedPt.X ? (c.Y <= fixedPt.Y ? 0 : 2) : (c.Y <= fixedPt.Y ? 1 : 3);
                if (nr.Width < 1 || nr.Height < 1) Selected.B = new Point(fixedPt.X + (c.X < fixedPt.X ? -1 : 1), fixedPt.Y + (c.Y < fixedPt.Y ? -1 : 1));
                Touch();
                break;
            }
        }
        _last = p;
    }

    void EnsureDragUndo()
    {
        if (_pushedForDrag) return;
        PushUndo(); _pushedForDrag = true;
    }

    static Point SnapAngle(Point origin, Point p)
    {
        var v = p - origin;
        double ang = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return origin + new Vector(Math.Cos(ang), Math.Sin(ang)) * v.Length;
    }

    void UpdateCursor(Point p)
    {
        if (Selected != null && HandlePoints(Selected).Any(h => (h - p).Length <= Tol + 3)) { Cursor = Cursors.SizeAll; return; }
        Cursor = Tool switch
        {
            EditorTool.Select => HitTest(p) != null ? Cursors.SizeAll : Cursors.Arrow,
            EditorTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured) ReleaseMouseCapture();
        var mode = _drag; _drag = DragMode.None;
        if (mode == DragMode.Draw && _draft != null)
        {
            var a = _draft; _draft = null;
            bool tiny = a.Kind == AnnKind.Pen ? false : (a.B - a.A).Length < 3;
            if (tiny) { InvalidateVisual(); return; }
            if (a.IsBox) { var r = new Rect(a.A, a.B); a.A = r.TopLeft; a.B = r.BottomRight; }
            if (a.Kind == AnnKind.Callout)
            {
                var r = a.Bounds;
                a.Tail = new Point(r.X + r.Width * 0.25, r.Bottom + Math.Min(40, Math.Max(18, r.Height * 0.5)));
                Add(a);
                TextEditRequested?.Invoke(a, false);
                return;
            }
            Add(a);
        }
        else if (mode == DragMode.Crop)
        {
            if (CropRect is { } r && (r.Width < 3 || r.Height < 3)) CropRect = null;
            InvalidateVisual(); CropChanged?.Invoke();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        PointerMoved?.Invoke(null);
    }

    /// <summary>Mueve la selección con las flechas del teclado.</summary>
    public bool Nudge(Vector v)
    {
        if (Selected == null) return false;
        var s = Selected;
        Mutate(() => s.Move(v));
        return true;
    }
}

using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FranCapture.Core.Imaging;
using FranCaptureStudio.Services;

namespace FranCaptureStudio.Editor;

public enum AnnKind { Pen, Line, Arrow, Rect, Ellipse, Text, Step, Highlight, Callout, Image, Pixelate, Blur, Redact }

/// <summary>Anotación editable. Las coordenadas están en píxeles de la imagen.</summary>
public sealed class Annotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AnnKind Kind { get; set; }
    public Point A { get; set; }
    public Point B { get; set; }
    public Point Tail { get; set; }                 // punta del cuadro de diálogo
    public List<Point> Points { get; set; } = [];   // trazo de lápiz
    public Color Color { get; set; } = Colors.Red;
    public double Thickness { get; set; } = 4;
    public bool Filled { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 22;
    public int Number { get; set; } = 1;
    public double Opacity { get; set; } = 1;
    public int Strength { get; set; } = 12;         // tamaño de bloque (pixelado) o radio (desenfoque)
    public BitmapSource? Image { get; set; }
    internal Size Measured;                         // tamaño calculado al dibujar (texto / paso)

    public bool IsBox => Kind is AnnKind.Rect or AnnKind.Ellipse or AnnKind.Highlight or AnnKind.Callout or AnnKind.Image or AnnKind.Pixelate or AnnKind.Blur or AnnKind.Redact;
    public bool IsLine => Kind is AnnKind.Line or AnnKind.Arrow;
    public bool HasText => Kind is AnnKind.Text or AnnKind.Callout;

    public Rect Bounds
    {
        get
        {
            switch (Kind)
            {
                case AnnKind.Pen:
                    if (Points.Count == 0) return Rect.Empty;
                    double x0 = Points.Min(p => p.X), y0 = Points.Min(p => p.Y), x1 = Points.Max(p => p.X), y1 = Points.Max(p => p.Y);
                    return new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
                case AnnKind.Text: return new Rect(A, Measured.IsEmpty || Measured.Width < 1 ? new Size(40, FontSize * 1.3) : Measured);
                case AnnKind.Step: { double d = StepDiameter; return new Rect(A.X - d / 2, A.Y - d / 2, d, d); }
                default: return new Rect(A, B);
            }
        }
    }

    public double StepDiameter => Math.Max(22, FontSize * 1.7);

    public void Move(Vector v)
    {
        A += v; B += v; Tail += v;
        for (int i = 0; i < Points.Count; i++) Points[i] += v;
    }

    public Annotation Clone()
    {
        var c = (Annotation)MemberwiseClone();
        c.Points = [.. Points];
        return c;
    }

    public Annotation Duplicate()
    {
        var c = Clone();
        c.Id = Guid.NewGuid();
        c.Move(new Vector(18, 18));
        return c;
    }
}

/// <summary>Estado del editor en un instante: imagen base (inmutable) y lista de anotaciones.</summary>
public sealed class EditorState
{
    public required BitmapSource Base { get; init; }
    public required List<Annotation> Annotations { get; init; }
    public EditorState Snapshot() => new() { Base = Base, Annotations = Annotations.Select(a => a.Clone()).ToList() };
}

/// <summary>Historial de deshacer/rehacer basado en instantáneas (las imágenes son inmutables y se comparten).</summary>
public sealed class EditorHistory
{
    readonly List<EditorState> _undo = [], _redo = [];
    const int Max = 80;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(EditorState current)
    {
        _undo.Add(current.Snapshot());
        if (_undo.Count > Max) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public EditorState? Undo(EditorState current)
    {
        if (_undo.Count == 0) return null;
        _redo.Add(current.Snapshot());
        var s = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        return s;
    }

    public EditorState? Redo(EditorState current)
    {
        if (_redo.Count == 0) return null;
        _undo.Add(current.Snapshot());
        var s = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        return s;
    }

    public void Clear() { _undo.Clear(); _redo.Clear(); }
}

/// <summary>Dibuja anotaciones. El mismo código se usa en pantalla y al exportar, así que el resultado es idéntico.</summary>
public sealed class AnnotationRenderer
{
    readonly Dictionary<Guid, (Int32Rect Rect, int Strength, BitmapSource Base, BitmapSource Result)> _effects = [];
    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static FormattedText Format(string text, double size, Brush brush, double maxWidth = 0)
    {
        var ft = new FormattedText(string.IsNullOrEmpty(text) ? " " : text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, Math.Max(6, size), brush, 1.0);
        if (maxWidth > 10) ft.MaxTextWidth = maxWidth;
        return ft;
    }

    static Color Contrast(Color c) => (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) > 150 ? Colors.Black : Colors.White;

    public void Draw(DrawingContext dc, Annotation a, BitmapSource baseImage)
    {
        var brush = new SolidColorBrush(a.Color);
        var pen = new Pen(brush, Math.Max(1, a.Thickness)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        switch (a.Kind)
        {
            case AnnKind.Pen:
                if (a.Points.Count == 1) dc.DrawEllipse(brush, null, a.Points[0], a.Thickness / 2, a.Thickness / 2);
                else if (a.Points.Count > 1)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open()) { c.BeginFigure(a.Points[0], false, false); c.PolyLineTo(a.Points.Skip(1).ToList(), true, true); }
                    dc.DrawGeometry(null, pen, g);
                }
                break;
            case AnnKind.Line:
                dc.DrawLine(pen, a.A, a.B);
                break;
            case AnnKind.Arrow:
            {
                var v = a.B - a.A;
                double len = v.Length;
                if (len < 0.5) break;
                v.Normalize();
                double head = Math.Min(len, Math.Max(12, a.Thickness * 4.2));
                var n = new Vector(-v.Y, v.X);
                var basePt = a.B - v * head;
                dc.DrawLine(pen, a.A, basePt + v * (head * 0.25));
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(a.B, true, true);
                    c.LineTo(basePt + n * head * 0.48, true, true);
                    c.LineTo(basePt - n * head * 0.48, true, true);
                }
                dc.DrawGeometry(brush, new Pen(brush, 1) { LineJoin = PenLineJoin.Round }, g);
                break;
            }
            case AnnKind.Rect:
                dc.DrawRectangle(a.Filled ? new SolidColorBrush(Color.FromArgb(70, a.Color.R, a.Color.G, a.Color.B)) : null, new Pen(brush, Math.Max(1, a.Thickness)) { LineJoin = PenLineJoin.Miter }, a.Bounds);
                break;
            case AnnKind.Ellipse:
            {
                var r = a.Bounds;
                dc.DrawEllipse(a.Filled ? new SolidColorBrush(Color.FromArgb(70, a.Color.R, a.Color.G, a.Color.B)) : null, pen, new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                break;
            }
            case AnnKind.Highlight:
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(96, a.Color.R, a.Color.G, a.Color.B)), null, a.Bounds);
                break;
            case AnnKind.Redact:
                // Censura sólida: al exportar, los píxeles originales se sustituyen por completo.
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(a.Color.R, a.Color.G, a.Color.B)), null, Snap(a.Bounds));
                break;
            case AnnKind.Text:
            {
                var ft = Format(a.Text, a.FontSize, brush);
                a.Measured = new Size(ft.WidthIncludingTrailingWhitespace, ft.Height);
                var geo = ft.BuildGeometry(a.A);
                var outline = Contrast(a.Color);
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(170, outline.R, outline.G, outline.B)), Math.Max(2, a.FontSize / 7)) { LineJoin = PenLineJoin.Round }, geo);
                dc.DrawGeometry(brush, null, geo);
                break;
            }
            case AnnKind.Step:
            {
                double d = a.StepDiameter;
                a.Measured = new Size(d, d);
                dc.DrawEllipse(brush, new Pen(Brushes.White, Math.Max(1.5, d / 14)), a.A, d / 2, d / 2);
                var ft = Format(a.Number.ToString(), d * 0.52, new SolidColorBrush(Contrast(a.Color)));
                dc.DrawText(ft, new Point(a.A.X - ft.Width / 2, a.A.Y - ft.Height / 2));
                break;
            }
            case AnnKind.Callout:
            {
                var r = a.Bounds;
                if (r.Width < 2 || r.Height < 2) break;
                double rad = Math.Min(10, Math.Min(r.Width, r.Height) / 3);
                var body = new RectangleGeometry(r, rad, rad);
                Geometry shape = body;
                if (!r.Contains(a.Tail))
                {
                    var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
                    var dir = a.Tail - center; dir.Normalize();
                    var n = new Vector(-dir.Y, dir.X);
                    double w = Math.Min(Math.Min(r.Width, r.Height) * 0.3, 22);
                    var tail = new StreamGeometry();
                    using (var c = tail.Open()) { c.BeginFigure(a.Tail, true, true); c.LineTo(center + n * w, true, true); c.LineTo(center - n * w, true, true); }
                    shape = new CombinedGeometry(GeometryCombineMode.Union, body, tail);
                }
                var textColor = Contrast(a.Color);
                dc.DrawGeometry(brush, new Pen(new SolidColorBrush(Color.FromArgb(120, textColor.R, textColor.G, textColor.B)), 1), shape);
                var ft = Format(a.Text, a.FontSize, new SolidColorBrush(textColor), r.Width - 16);
                dc.PushClip(new RectangleGeometry(r));
                dc.DrawText(ft, new Point(r.X + 8, r.Y + Math.Max(4, (r.Height - ft.Height) / 2)));
                dc.Pop();
                break;
            }
            case AnnKind.Image:
                if (a.Image != null)
                {
                    dc.PushOpacity(a.Opacity);
                    dc.DrawImage(a.Image, a.Bounds);
                    dc.Pop();
                }
                break;
            case AnnKind.Pixelate:
            case AnnKind.Blur:
            {
                var fx = Effect(a, baseImage);
                if (fx.Image != null) dc.DrawImage(fx.Image, new Rect(fx.Rect.X, fx.Rect.Y, fx.Rect.Width, fx.Rect.Height));
                break;
            }
        }
    }

    static Rect Snap(Rect r) => new(Math.Floor(r.X), Math.Floor(r.Y), Math.Ceiling(r.Width), Math.Ceiling(r.Height));

    (BitmapSource? Image, Int32Rect Rect) Effect(Annotation a, BitmapSource baseImage)
    {
        var b = a.Bounds;
        int x = (int)Math.Floor(Math.Max(0, b.X)), y = (int)Math.Floor(Math.Max(0, b.Y));
        int w = (int)Math.Ceiling(Math.Min(baseImage.PixelWidth, b.Right)) - x, h = (int)Math.Ceiling(Math.Min(baseImage.PixelHeight, b.Bottom)) - y;
        if (w < 1 || h < 1) return (null, default);
        var rect = new Int32Rect(x, y, w, h);
        if (_effects.TryGetValue(a.Id, out var e) && e.Rect.Equals(rect) && e.Strength == a.Strength && ReferenceEquals(e.Base, baseImage)) return (e.Result, rect);
        var px = new byte[w * h * 4];
        baseImage.CopyPixels(rect, px, w * 4, 0);
        if (a.Kind == AnnKind.Pixelate) PixelOps.Pixelate(px, w, h, Math.Max(4, a.Strength));
        else PixelOps.BoxBlur(px, w, h, Math.Max(2, a.Strength));
        var result = Img.FromPixels(px, w, h);
        _effects[a.Id] = (rect, a.Strength, baseImage, result);
        return (result, rect);
    }

    /// <summary>Imagen final aplanada: base + anotaciones. Lo censurado, pixelado o desenfocado queda irreversible.</summary>
    public BitmapSource Flatten(EditorState state)
    {
        int w = state.Base.PixelWidth, h = state.Base.PixelHeight;
        return Img.Draw(w, h, dc =>
        {
            dc.DrawImage(state.Base, new Rect(0, 0, w, h));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            foreach (var a in state.Annotations) Draw(dc, a, state.Base);
            dc.Pop();
        });
    }
}

/// <summary>Proyecto editable (.fcsproj): zip con la imagen base y las anotaciones en JSON.</summary>
public static class EditorProject
{
    sealed class AnnDto
    {
        public AnnKind Kind { get; set; }
        public double[] A { get; set; } = [0, 0];
        public double[] B { get; set; } = [0, 0];
        public double[] Tail { get; set; } = [0, 0];
        public double[] Points { get; set; } = [];
        public string Color { get; set; } = "#FFFF0000";
        public double Thickness { get; set; }
        public bool Filled { get; set; }
        public string Text { get; set; } = "";
        public double FontSize { get; set; }
        public int Number { get; set; }
        public double Opacity { get; set; } = 1;
        public int Strength { get; set; } = 12;
        public string? ImageEntry { get; set; }
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static void Save(string path, EditorState state)
    {
        var tmp = path + ".tmp~";
        using (var fs = File.Create(tmp))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "base.png", Img.Encode(state.Base, "png"));
            var dtos = new List<AnnDto>();
            int n = 0;
            foreach (var a in state.Annotations)
            {
                var d = new AnnDto
                {
                    Kind = a.Kind, A = [a.A.X, a.A.Y], B = [a.B.X, a.B.Y], Tail = [a.Tail.X, a.Tail.Y],
                    Points = a.Points.SelectMany(p => new[] { p.X, p.Y }).ToArray(), Color = a.Color.ToString(CultureInfo.InvariantCulture),
                    Thickness = a.Thickness, Filled = a.Filled, Text = a.Text, FontSize = a.FontSize, Number = a.Number, Opacity = a.Opacity, Strength = a.Strength,
                };
                if (a.Image != null) { d.ImageEntry = $"img{n++}.png"; WriteEntry(zip, d.ImageEntry, Img.Encode(a.Image, "png")); }
                dtos.Add(d);
            }
            WriteEntry(zip, "annotations.json", JsonSerializer.SerializeToUtf8Bytes(dtos, Json));
        }
        File.Move(tmp, path, true);
    }

    static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        using var s = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        s.Write(data, 0, data.Length);
    }

    static byte[] ReadEntry(ZipArchive zip, string name)
    {
        var e = zip.GetEntry(name) ?? throw new InvalidDataException("El proyecto está incompleto: falta " + name);
        if (e.Length > 400_000_000) throw new InvalidDataException("El proyecto contiene una entrada demasiado grande.");
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    public static EditorState Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var baseImg = Img.Load(new MemoryStream(ReadEntry(zip, "base.png")));
        var dtos = JsonSerializer.Deserialize<List<AnnDto>>(ReadEntry(zip, "annotations.json"), Json) ?? [];
        var list = new List<Annotation>();
        foreach (var d in dtos)
        {
            static Point P(double[] v) => v.Length >= 2 ? new Point(v[0], v[1]) : default;
            var a = new Annotation
            {
                Kind = d.Kind, A = P(d.A), B = P(d.B), Tail = P(d.Tail), Thickness = d.Thickness, Filled = d.Filled, Text = d.Text ?? "",
                FontSize = d.FontSize, Number = d.Number, Opacity = d.Opacity, Strength = d.Strength,
            };
            try { a.Color = (Color)ColorConverter.ConvertFromString(d.Color); } catch { }
            for (int i = 0; i + 1 < d.Points.Length; i += 2) a.Points.Add(new Point(d.Points[i], d.Points[i + 1]));
            if (d.ImageEntry != null && d.ImageEntry.IndexOfAny(['/', '\\', '.']) == d.ImageEntry.LastIndexOf('.'))
                a.Image = Img.Load(new MemoryStream(ReadEntry(zip, d.ImageEntry)));
            list.Add(a);
        }
        return new EditorState { Base = baseImg, Annotations = list };
    }
}

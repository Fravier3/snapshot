using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FranCapture.Core.Diagnostics;
using FranCapture.Core.Imaging;
using FranCapture.Core.Localization;
using FranCaptureStudio.Capture;
using FranCaptureStudio.Services;
using FranCaptureStudio.Views;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FranCaptureStudio.Tools;

internal static class W
{
    public static Button Btn(string text, Action click, string style = "SecondaryButton")
    {
        var b = new Button { Content = Loc.T(text), Style = Ui.S(style), Margin = new Thickness(0, 0, 8, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    public static TextBlock Label(string text, string style = "Muted") => new() { Text = Loc.T(text), Style = Ui.S(style), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

    public static Border Bar(params UIElement[] children)
    {
        var wp = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) wp.Children.Add(c);
        var b = new Border { Padding = new Thickness(14, 9, 14, 9), BorderThickness = new Thickness(0, 0, 0, 1), Child = wp };
        b.SetResourceReference(Border.BackgroundProperty, "HeaderBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return b;
    }

    public static BitmapSource? PickImage(Window owner)
    {
        var f = Ui.OpenImageFile(owner);
        if (f == null) return null;
        try { return Img.Load(f); } catch (Exception ex) { Ui.Error(Loc.T("No se pudo abrir la imagen:") + "\n" + ex.Message, owner); return null; }
    }

    public static BitmapSource? CaptureRegion(Window owner)
    {
        owner.Hide();
        try
        {
            Thread.Sleep(220);
            using var res = OverlayForm.Show(new OverlayOptions { Purpose = OverlayPurpose.SelectOnly, AcceptLabel = "Usar esta región" });
            return res.Image != null ? Img.FromGdi(res.Image) : null;
        }
        finally { owner.Show(); owner.Activate(); }
    }
}

/// <summary>Comparador: lado a lado, deslizador antes/después y resaltado de diferencias.</summary>
public sealed class CompareWindow : StudioWindow
{
    BitmapSource? _a, _b;
    readonly Grid _view = new();
    readonly TextBlock _info = W.Label("Elige dos imágenes para comparar.");
    readonly ComboBox _mode = new() { Width = 190, Margin = new Thickness(0, 0, 8, 0) };
    readonly Slider _slider = new() { Minimum = 0, Maximum = 1, Value = 0.5, Width = 160, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };

    public CompareWindow(BitmapSource? a = null, BitmapSource? b = null)
    {
        _a = a; _b = b;
        Title = Loc.T("Comparador de capturas"); Width = 1100; Height = 720; MinWidth = 700; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        foreach (var m in new[] { "Lado a lado", "Deslizador antes/después", "Resaltar diferencias" }) _mode.Items.Add(Loc.T(m));
        _mode.SelectedIndex = 0;
        _mode.SelectionChanged += (_, _) => Render();
        _slider.ValueChanged += (_, _) => Render();
        var bar = W.Bar(
            W.Btn("Imagen A…", () => { if (W.PickImage(this) is { } i) { _a = i; Render(); } }),
            W.Btn("Capturar A", () => { if (W.CaptureRegion(this) is { } i) { _a = i; Render(); } }),
            W.Btn("Imagen B…", () => { if (W.PickImage(this) is { } i) { _b = i; Render(); } }),
            W.Btn("Capturar B", () => { if (W.CaptureRegion(this) is { } i) { _b = i; Render(); } }),
            _mode, _slider, _info);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        _view.SetResourceReference(Panel.BackgroundProperty, "CanvasBrush");
        dock.Children.Add(_view);
        Content = dock;
        SizeChanged += (_, _) => { if (_mode.SelectedIndex == 1) Render(); };
        Loaded += (_, _) => Render();
    }

    static Image Pic(BitmapSource src)
    {
        var i = new Image { Source = src, Stretch = Stretch.Uniform, Margin = new Thickness(10) };
        RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality);
        return i;
    }

    internal DiffResult? LastDiff;

    void Render()
    {
        _view.Children.Clear(); _view.ColumnDefinitions.Clear();
        _slider.Visibility = _mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_a == null || _b == null) { _info.Text = Loc.T("Elige dos imágenes para comparar."); if (_a != null) _view.Children.Add(Pic(_a)); else if (_b != null) _view.Children.Add(Pic(_b)); return; }
        LastDiff = ImageDiff.Compare(Img.Pixels(_a), _a.PixelWidth, _a.PixelHeight, Img.Pixels(_b), _b.PixelWidth, _b.PixelHeight);
        _info.Text = LastDiff.DifferentPixels == 0
            ? Loc.T("Las imágenes son idénticas.")
            : Loc.T("{0:N0} píxeles distintos ({1:0.##} %)", LastDiff.DifferentPixels, LastDiff.Percent) + (LastDiff.SameSize ? "" : " · " + Loc.T("tamaños diferentes"));
        switch (_mode.SelectedIndex)
        {
            case 0:
                _view.ColumnDefinitions.Add(new ColumnDefinition()); _view.ColumnDefinitions.Add(new ColumnDefinition());
                var pa = Pic(_a); var pb = Pic(_b); Grid.SetColumn(pb, 1);
                _view.Children.Add(pa); _view.Children.Add(pb);
                break;
            case 1:
            {
                // B debajo; A encima recortada hasta la posición del deslizador
                var pb2 = Pic(_b); var pa2 = Pic(_a);
                _view.Children.Add(pb2); _view.Children.Add(pa2);
                double w = Math.Max(1, _view.ActualWidth), h = Math.Max(1, _view.ActualHeight);
                pa2.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, w * _slider.Value - 10), h));
                var line = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(w * _slider.Value - 1, 0, 0, 0), Background = Ui.B("AccentBrush") };
                _view.Children.Add(line);
                break;
            }
            default:
                _view.Children.Add(Pic(Img.FromPixels(LastDiff.Highlight, _b.PixelWidth, _b.PixelHeight)));
                break;
        }
    }
}

/// <summary>Une varias imágenes en vertical u horizontal.</summary>
public sealed class CombineWindow : StudioWindow
{
    readonly List<BitmapSource> _images = [];
    readonly ListBox _list = new() { Width = 230 };
    readonly Image _preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(12) };
    readonly ComboBox _orientation = new() { Width = 120, Margin = new Thickness(0, 0, 8, 0) };
    readonly ComboBox _sizing = new() { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
    readonly Slider _gap = new() { Minimum = 0, Maximum = 80, Value = 12, Width = 110, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 2 };
    BitmapSource? _result;

    public CombineWindow(IEnumerable<BitmapSource>? initial = null)
    {
        Title = Loc.T("Combinador de imágenes"); Width = 1080; Height = 720; MinWidth = 720; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (initial != null) _images.AddRange(initial);
        _orientation.Items.Add(Loc.T("Vertical")); _orientation.Items.Add(Loc.T("Horizontal")); _orientation.SelectedIndex = 0;
        _sizing.Items.Add(Loc.T("Tamaño original")); _sizing.Items.Add(Loc.T("Igualar ancho/alto")); _sizing.SelectedIndex = 1;
        _orientation.SelectionChanged += (_, _) => Rebuild(); _sizing.SelectionChanged += (_, _) => Rebuild(); _gap.ValueChanged += (_, _) => Rebuild();
        var bar = W.Bar(
            W.Btn("Agregar imágenes…", AddFiles, "PrimaryButton"), W.Btn("Capturar región", () => { if (W.CaptureRegion(this) is { } i) { _images.Add(i); Rebuild(); } }),
            W.Btn("Subir", () => MoveSel(-1)), W.Btn("Bajar", () => MoveSel(1)), W.Btn("Quitar", () => { if (_list.SelectedIndex >= 0) { _images.RemoveAt(_list.SelectedIndex); Rebuild(); } }),
            _orientation, _sizing, W.Label("Separación"), _gap,
            new Border { Width = 14 }, W.Btn("Copiar", () => { if (_result != null) ClipboardService.SetImage(_result); }),
            W.Btn("Guardar como…", () => { if (_result != null) CaptureCoordinator.SaveAsDialog(_result, null, this); }),
            W.Btn("Abrir en editor", () => { if (_result != null) Editor.EditorWindow.Open(_result, null); }));
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); dock.Children.Add(bar);
        DockPanel.SetDock(_list, Dock.Left); dock.Children.Add(_list);
        var host = new Grid(); host.SetResourceReference(Panel.BackgroundProperty, "CanvasBrush"); host.Children.Add(_preview);
        dock.Children.Add(host);
        Content = dock;
        Rebuild();
    }

    void AddFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Img.OpenFilter, Multiselect = true, Title = Loc.T("Agregar imágenes") };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames) try { _images.Add(Img.Load(f)); } catch (Exception ex) { Log.Warn("Combinar: " + ex.Message); }
        Rebuild();
    }

    void MoveSel(int d)
    {
        int i = _list.SelectedIndex, j = i + d;
        if (i < 0 || j < 0 || j >= _images.Count) return;
        (_images[i], _images[j]) = (_images[j], _images[i]);
        Rebuild(); _list.SelectedIndex = j;
    }

    void Rebuild()
    {
        _list.Items.Clear();
        for (int i = 0; i < _images.Count; i++) _list.Items.Add($"{i + 1}.  {_images[i].PixelWidth} × {_images[i].PixelHeight}");
        _result = _images.Count == 0 ? null : Combine(_images, _orientation.SelectedIndex == 0, (int)_gap.Value, _sizing.SelectedIndex == 1, Colors.White);
        _preview.Source = _result;
    }

    public static BitmapSource Combine(IReadOnlyList<BitmapSource> images, bool vertical, int gap, bool equalize, Color background)
    {
        var sizes = new List<(double W, double H)>();
        double target = vertical ? images.Max(i => i.PixelWidth) : images.Max(i => i.PixelHeight);
        foreach (var img in images)
        {
            double s = equalize ? target / (vertical ? img.PixelWidth : img.PixelHeight) : 1;
            sizes.Add((Math.Round(img.PixelWidth * s), Math.Round(img.PixelHeight * s)));
        }
        int w = (int)(vertical ? sizes.Max(s => s.W) : sizes.Sum(s => s.W) + gap * (images.Count - 1));
        int h = (int)(vertical ? sizes.Sum(s => s.H) + gap * (images.Count - 1) : sizes.Max(s => s.H));
        if ((long)w * h > 250_000_000) throw new InvalidOperationException("La imagen combinada sería demasiado grande.");
        return Img.Draw(w, h, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, w, h));
            double pos = 0;
            for (int i = 0; i < images.Count; i++)
            {
                var (sw, sh) = sizes[i];
                dc.DrawImage(images[i], vertical ? new Rect((w - sw) / 2, pos, sw, sh) : new Rect(pos, (h - sh) / 2, sw, sh));
                pos += (vertical ? sh : sw) + gap;
            }
        });
    }
}

/// <summary>Conversor por lotes entre formatos de imagen.</summary>
public sealed class ConverterWindow : StudioWindow
{
    readonly ListBox _files = new() { MinHeight = 200 };
    readonly ComboBox _format = new() { Width = 100, Margin = new Thickness(0, 0, 8, 0) };
    readonly Slider _quality = new() { Minimum = 10, Maximum = 100, Value = 90, Width = 110, VerticalAlignment = VerticalAlignment.Center };
    readonly CheckBox _strip = new() { Content = Loc.T("Eliminar metadatos"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0) };
    readonly ProgressBar _progress = new() { Height = 6, Margin = new Thickness(14, 0, 14, 10), Minimum = 0, Maximum = 1 };
    readonly TextBlock _status = new() { Margin = new Thickness(14, 8, 14, 6), TextWrapping = TextWrapping.Wrap };
    string? _outDir;

    public ConverterWindow()
    {
        Title = Loc.T("Conversor de imágenes"); Width = 760; Height = 520; MinWidth = 560; MinHeight = 360; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        foreach (var f in Img.WritableFormats) _format.Items.Add(f.ToUpperInvariant());
        _format.SelectedIndex = 1;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        _status.Text = Loc.T("Formatos de entrada: PNG, JPG, BMP, GIF, TIFF y WebP. Salida: PNG, JPG, WebP, BMP, GIF y TIFF.");
        var bar = W.Bar(W.Btn("Agregar archivos…", AddFiles, "PrimaryButton"), W.Btn("Vaciar lista", () => _files.Items.Clear()), W.Label("Convertir a"), _format, W.Label("Calidad"), _quality, _strip,
            W.Btn("Carpeta de salida…", () => { _outDir = Ui.PickFolder(_outDir, "Carpeta de salida") ?? _outDir; if (_outDir != null) _status.Text = Loc.T("Salida:") + " " + _outDir; }),
            W.Btn("Convertir", () => _ = ConvertAsync(), "PrimaryButton"));
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); dock.Children.Add(bar);
        DockPanel.SetDock(_progress, Dock.Bottom); dock.Children.Add(_progress);
        DockPanel.SetDock(_status, Dock.Bottom); dock.Children.Add(_status);
        dock.Children.Add(_files);
        Content = dock;
    }

    void AddFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Img.OpenFilter, Multiselect = true };
        if (dlg.ShowDialog(this) == true) foreach (var f in dlg.FileNames) if (!_files.Items.Contains(f)) _files.Items.Add(f);
    }

    async Task ConvertAsync()
    {
        var files = _files.Items.Cast<string>().ToList();
        if (files.Count == 0) { _status.Text = Loc.T("Agrega al menos un archivo."); return; }
        string ext = ((string)_format.SelectedItem).ToLowerInvariant();
        int quality = (int)_quality.Value; bool strip = _strip.IsChecked == true; string? outDir = _outDir;
        IsEnabled = false;
        int ok = 0; var errors = new List<string>();
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                var f = files[i];
                _status.Text = Loc.T("Convirtiendo {0} de {1}…", i + 1, files.Count);
                _progress.Value = (double)i / files.Count;
                try { await Task.Run(() => ConvertFile(f, outDir ?? Path.GetDirectoryName(f)!, ext, quality, strip)); ok++; }
                catch (Exception ex) { errors.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
            }
        }
        finally { IsEnabled = true; _progress.Value = 1; }
        _status.Text = Loc.T("{0} archivos convertidos.", ok) + (errors.Count > 0 ? " " + Loc.T("Errores:") + " " + string.Join(" | ", errors) : "");
    }

    /// <summary>Convierte un archivo; nunca sobrescribe el original ni otros archivos existentes.</summary>
    public static string ConvertFile(string input, string outDir, string ext, int quality, bool stripMetadata)
    {
        BitmapMetadata? meta = null;
        if (!stripMetadata && ext is "jpg" or "tiff")
        {
            try
            {
                using var fs = File.OpenRead(input);
                var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (dec.Frames[0].Metadata is BitmapMetadata m && m.Format == (ext == "jpg" ? "jpg" : "tiff")) meta = m.Clone();
            }
            catch { }
        }
        var img = Img.Load(input);
        var output = FranCapture.Core.Storage.FileNaming.UniquePath(outDir, Path.GetFileNameWithoutExtension(input), ext);
        Img.Save(img, output, quality, meta);
        return output;
    }
}

/// <summary>
/// Eliminación de fondo local. Método 1: por color uniforme (sin modelos). Método 2 (opcional): modelo U²-Net en formato
/// ONNX colocado por el usuario en la carpeta de modelos; si no existe, la opción simplemente no está disponible.
/// </summary>
public sealed class BackgroundRemovalWindow : StudioWindow
{
    readonly BitmapSource _source;
    BitmapSource? _result;
    readonly Image _preview = new() { Stretch = Stretch.Uniform, Margin = new Thickness(14) };
    readonly Slider _tolerance = new() { Minimum = 2, Maximum = 120, Value = 28, Width = 140, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _status = W.Label("");

    public static string? ModelPath =>
        Directory.Exists(AppPaths.ModelsDir) ? Directory.GetFiles(AppPaths.ModelsDir, "*.onnx").OrderBy(f => f).FirstOrDefault() : null;

    /// <summary>Muestra el diálogo y devuelve la imagen sin fondo, o null si se canceló.</summary>
    public static BitmapSource? Run(BitmapSource image, Window? owner)
    {
        var w = new BackgroundRemovalWindow(image) { Owner = owner };
        return w.ShowDialog() == true ? w._result : null;
    }

    BackgroundRemovalWindow(BitmapSource source)
    {
        _source = source;
        Title = Loc.T("Eliminar fondo"); Width = 920; Height = 680; MinWidth = 640; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var model = ModelPath;
        var ai = W.Btn("Usar modelo local (U²-Net)", () => _ = RunModelAsync());
        ai.IsEnabled = model != null;
        ai.ToolTip = model != null ? model : Loc.T("Modelo no instalado. Copia un archivo u2netp.onnx o u2net.onnx en la carpeta de modelos.");
        _tolerance.ValueChanged += (_, _) => ByColor();
        var bar = W.Bar(W.Label("Fondo uniforme · tolerancia"), _tolerance, new Border { Width = 10 }, ai, W.Btn("Carpeta de modelos", () => Shell.OpenFile(AppPaths.ModelsDir)), _status);
        var ok = W.Btn("Aplicar", () => { if (_result != null) DialogResult = true; }, "PrimaryButton");
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 10, 6, 12) };
        bottom.Children.Add(W.Btn("Guardar PNG…", () => { if (_result != null) CaptureCoordinator.SaveAsDialog(_result, null, this); }));
        bottom.Children.Add(ok); bottom.Children.Add(W.Btn("Cancelar", () => DialogResult = false));
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); dock.Children.Add(bar);
        DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);
        var host = new Grid { Background = Checker() }; host.Children.Add(_preview);
        dock.Children.Add(host);
        Content = dock;
        Loaded += (_, _) => ByColor();
    }

    static Brush Checker()
    {
        var g = new DrawingGroup();
        using (var dc = g.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(200, 200, 200)), null, new Rect(0, 0, 16, 16));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(240, 240, 240)), null, new Rect(0, 0, 8, 8));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(240, 240, 240)), null, new Rect(8, 8, 8, 8));
        }
        return new DrawingBrush(g) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
    }

    void ByColor()
    {
        _result = RemoveByColor(_source, (int)_tolerance.Value, out int removed);
        _preview.Source = _result;
        _status.Text = Loc.T("{0:N0} píxeles de fondo eliminados", removed);
    }

    public static BitmapSource RemoveByColor(BitmapSource source, int tolerance, out int removed)
    {
        var px = Img.Pixels(source);
        removed = PixelOps.RemoveBackgroundByColor(px, source.PixelWidth, source.PixelHeight, tolerance);
        return Img.FromPixels(px, source.PixelWidth, source.PixelHeight);
    }

    async Task RunModelAsync()
    {
        var model = ModelPath;
        if (model == null) return;
        IsEnabled = false; _status.Text = Loc.T("Procesando con el modelo local…");
        try
        {
            var src = _source;
            var px = Img.Pixels(src);
            var mask = await Task.Run(() => RunU2Net(model, src));
            PixelOps.ApplyAlphaMask(px, mask);
            _result = Img.FromPixels(px, src.PixelWidth, src.PixelHeight);
            _preview.Source = _result;
            _status.Text = Loc.T("Fondo eliminado con el modelo local.");
        }
        catch (Exception ex)
        {
            Log.Error("Modelo de fondo", ex);
            _status.Text = Loc.T("El modelo no pudo ejecutarse:") + " " + ex.Message;
        }
        finally { IsEnabled = true; }
    }

    /// <summary>Inferencia U²-Net (entrada 1×3×320×320 normalizada; la primera salida es el mapa de prominencia).</summary>
    public static byte[] RunU2Net(string modelPath, BitmapSource image)
    {
        const int N = 320;
        var small = Img.Pixels(Img.Resize(image, N, N));
        var tensor = new DenseTensor<float>([1, 3, N, N]);
        float[] mean = [0.485f, 0.456f, 0.406f], std = [0.229f, 0.224f, 0.225f];
        float max = 1;
        for (int i = 0; i < N * N; i++) max = Math.Max(max, Math.Max(small[i * 4], Math.Max(small[i * 4 + 1], small[i * 4 + 2])));
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                int i = (y * N + x) * 4;
                tensor[0, 0, y, x] = (small[i + 2] / max - mean[0]) / std[0];
                tensor[0, 1, y, x] = (small[i + 1] / max - mean[1]) / std[1];
                tensor[0, 2, y, x] = (small[i] / max - mean[2]) / std[2];
            }
        using var session = new InferenceSession(modelPath);
        var inputName = session.InputMetadata.Keys.First();
        using var outputs = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        var data = outputs.First().AsEnumerable<float>().ToArray();
        if (data.Length < N * N) throw new InvalidDataException("El modelo no es compatible (se esperaba una salida de 320×320).");
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < N * N; i++) { lo = Math.Min(lo, data[i]); hi = Math.Max(hi, data[i]); }
        float range = Math.Max(1e-6f, hi - lo);
        var gray = new byte[N * N * 4];
        for (int i = 0; i < N * N; i++) { byte v = (byte)Math.Clamp((data[i] - lo) / range * 255f, 0, 255); gray[i * 4] = gray[i * 4 + 1] = gray[i * 4 + 2] = v; gray[i * 4 + 3] = 255; }
        var full = Img.Pixels(Img.Resize(Img.FromPixels(gray, N, N), image.PixelWidth, image.PixelHeight));
        var mask = new byte[image.PixelWidth * image.PixelHeight];
        for (int i = 0; i < mask.Length; i++) mask[i] = full[i * 4];
        return mask;
    }
}

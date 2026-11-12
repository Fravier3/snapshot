using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FranCapture.Core.Localization;
using FranCaptureStudio.Capture;
using FranCaptureStudio.Interop;
using FranCaptureStudio.Services;
using FranCaptureStudio.Views;
using SD = System.Drawing;

namespace FranCaptureStudio.Tools;

/// <summary>Imagen fijada en pantalla: siempre visible, con tamaño y opacidad ajustables.</summary>
public sealed class PinnedImageWindow : Window
{
    readonly BitmapSource _image;
    double _scale = 1;

    public static PinnedImageWindow Pin(BitmapSource image)
    {
        var w = new PinnedImageWindow(image);
        w.Show();
        return w;
    }

    PinnedImageWindow(BitmapSource image)
    {
        _image = image;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight; Title = "Imagen fijada";
        var img = new Image { Source = image, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0, 184, 255)), BorderThickness = new Thickness(1.5), Child = img };
        Loaded += (_, _) =>
        {
            // tamaño inicial 1:1 en píxeles físicos, limitado a la pantalla
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            var wa = Ui.WorkAreaAt(Ui.CursorPos());
            _scale = Math.Min(1, Math.Min(wa.Width * 0.8 / image.PixelWidth, wa.Height * 0.8 / image.PixelHeight));
            Apply(img, dpi);
        };
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Close(); else try { DragMove(); } catch { } };
        MouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.08 : -0.08), 0.15, 1);
            else { _scale = Math.Clamp(_scale * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.1, 6); Apply(img, VisualTreeHelper.GetDpi(this).DpiScaleX); }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        var menu = new ContextMenu();
        void Item(string text, Action a) { var mi = new MenuItem { Header = Loc.T(text) }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
        Item("Copiar", () => ClipboardService.SetImage(_image));
        Item("Guardar como…", () => CaptureCoordinator.SaveAsDialog(_image, null, this));
        Item("Abrir en el editor", () => Editor.EditorWindow.Open(_image, null));
        menu.Items.Add(new Separator());
        foreach (var o in new[] { 100, 75, 50, 25 }) Item(Loc.T("Opacidad") + $" {o} %", () => Opacity = o / 100.0);
        Item("Tamaño original", () => { _scale = 1; Apply(img, VisualTreeHelper.GetDpi(this).DpiScaleX); });
        menu.Items.Add(new Separator());
        Item("Cerrar", Close);
        ContextMenu = menu;
        ToolTip = Loc.T("Arrastra para mover · rueda: tamaño · Ctrl+rueda: opacidad · doble clic: cerrar");
        ToolTipService.SetInitialShowDelay(this, 1200);
    }

    void Apply(Image img, double dpi)
    {
        img.Width = Math.Max(24, _image.PixelWidth * _scale / dpi);
        img.Height = Math.Max(24, _image.PixelHeight * _scale / dpi);
    }
}

public static class QrTools
{
    public static string? Decode(BitmapSource image)
    {
        try
        {
            var px = Img.Pixels(image);
            var source = new ZXing.RGBLuminanceSource(px, image.PixelWidth, image.PixelHeight, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32);
            var reader = new ZXing.BarcodeReaderGeneric { AutoRotate = true };
            reader.Options.TryHarder = true; reader.Options.TryInverted = true;
            reader.Options.PossibleFormats = [ZXing.BarcodeFormat.QR_CODE, ZXing.BarcodeFormat.DATA_MATRIX, ZXing.BarcodeFormat.AZTEC, ZXing.BarcodeFormat.PDF_417];
            return reader.Decode(source)?.Text;
        }
        catch { return null; }
    }

    public static BitmapSource Generate(string text, int pixelSize = 600)
    {
        var hints = new Dictionary<ZXing.EncodeHintType, object>
        {
            [ZXing.EncodeHintType.CHARACTER_SET] = "UTF-8",
            [ZXing.EncodeHintType.MARGIN] = 2,
            [ZXing.EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
        };
        var matrix = new ZXing.QrCode.QRCodeWriter().encode(text, ZXing.BarcodeFormat.QR_CODE, pixelSize, pixelSize, hints);
        int w = matrix.Width, h = matrix.Height;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = matrix[x, y] ? (byte)0 : (byte)255;
                int i = (y * w + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255;
            }
        return Img.FromPixels(px, w, h);
    }
}

public sealed class QrWindow : StudioWindow
{
    readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly Image _preview = new() { Width = 260, Height = 260, Stretch = Stretch.Uniform };
    readonly TextBox _decoded = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    BitmapSource? _qr;

    public QrWindow()
    {
        Title = Loc.T("Código QR"); Width = 760; Height = 560; MinWidth = 600; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.NearestNeighbor);
        Button B(string text, RoutedEventHandler h, string style = "SecondaryButton") { var b = new Button { Content = Loc.T(text), Style = Ui.S(style), Margin = new Thickness(0, 0, 8, 8) }; b.Click += h; return b; }

        var gen = new StackPanel();
        gen.Children.Add(new TextBlock { Text = Loc.T("Generar código QR"), Style = Ui.S("SectionTitle") });
        gen.Children.Add(new TextBlock { Text = Loc.T("Texto o enlace:"), Style = Ui.S("Muted"), Margin = new Thickness(0, 0, 0, 4) });
        gen.Children.Add(_text);
        var genButtons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        genButtons.Children.Add(B("Generar", (_, _) => Generate(), "PrimaryButton"));
        genButtons.Children.Add(B("Copiar imagen", (_, _) => { if (_qr != null) ClipboardService.SetImage(_qr); }));
        genButtons.Children.Add(B("Guardar como…", (_, _) => { if (_qr != null) CaptureCoordinator.SaveAsDialog(_qr, null, this); }));
        gen.Children.Add(genButtons);
        gen.Children.Add(new Border { Background = Brushes.White, Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(6), Child = _preview });

        var read = new StackPanel();
        read.Children.Add(new TextBlock { Text = Loc.T("Leer código QR"), Style = Ui.S("SectionTitle") });
        var readButtons = new WrapPanel();
        readButtons.Children.Add(B("Capturar región de pantalla", (_, _) => ReadFromScreen(), "PrimaryButton"));
        readButtons.Children.Add(B("Desde archivo…", (_, _) => { var f = Ui.OpenImageFile(this); if (f != null) try { Read(Img.Load(f)); } catch (Exception ex) { Ui.Error(ex.Message, this); } }));
        readButtons.Children.Add(B("Desde portapapeles", (_, _) => { var i = ClipboardService.GetImage(); if (i != null) Read(i); else _decoded.Text = Loc.T("El portapapeles no contiene una imagen."); }));
        read.Children.Add(readButtons);
        read.Children.Add(new TextBlock { Text = Loc.T("Contenido leído:"), Style = Ui.S("Muted"), Margin = new Thickness(0, 6, 0, 4) });
        read.Children.Add(_decoded);
        var after = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        after.Children.Add(B("Copiar texto", (_, _) => ClipboardService.SetText(_decoded.Text)));
        after.Children.Add(B("Abrir enlace", (_, _) => { var l = FranCapture.Core.Text.LinkDetector.Find(_decoded.Text).FirstOrDefault(); if (l != null && Ui.Confirm(Loc.T("¿Abrir este enlace en el navegador?") + "\n" + l, null, this)) Shell.OpenUrl(l); }));
        read.Children.Add(after);

        var grid = new Grid { Margin = new Thickness(20) };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(read, 2);
        grid.Children.Add(gen); grid.Children.Add(read);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = grid };
    }

    void Generate()
    {
        if (string.IsNullOrWhiteSpace(_text.Text)) return;
        try { _qr = QrTools.Generate(_text.Text.Trim()); _preview.Source = _qr; }
        catch (Exception ex) { Ui.Error(Loc.T("No se pudo generar el código (¿texto demasiado largo?):") + "\n" + ex.Message, this); }
    }

    void Read(BitmapSource img)
    {
        var text = QrTools.Decode(img);
        _decoded.Text = text ?? Loc.T("No se encontró ningún código QR en la imagen.");
    }

    void ReadFromScreen()
    {
        Hide();
        try
        {
            using var res = OverlayForm.Show(new OverlayOptions { Purpose = OverlayPurpose.SelectOnly, AcceptLabel = "Leer QR" });
            if (res.Image != null) Read(Img.FromGdi(res.Image));
        }
        finally { Show(); Activate(); }
    }

    internal void TestSetText(string t) { _text.Text = t; Generate(); }
    internal BitmapSource? TestQr => _qr;
}

/// <summary>Lupa en vivo: amplía la zona bajo el cursor. La ventana se excluye de la captura para no verse a sí misma.</summary>
public sealed class MagnifierWindow : StudioWindow
{
    readonly Image _img = new() { Stretch = Stretch.Fill };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly TextBlock _info = new() { Margin = new Thickness(8, 4, 8, 4) };
    int _zoom = 4;

    public MagnifierWindow()
    {
        Title = Loc.T("Lupa"); Width = 420; Height = 360; MinWidth = 220; MinHeight = 200; Topmost = true; HideFromCapture = true;
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.NearestNeighbor);
        var dock = new DockPanel();
        _info.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        DockPanel.SetDock(_info, Dock.Bottom);
        dock.Children.Add(_info); dock.Children.Add(_img);
        Content = dock;
        _timer.Tick += (_, _) => Update();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        MouseWheel += (_, e) => _zoom = Math.Clamp(_zoom + (e.Delta > 0 ? 1 : -1), 2, 16);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    void Update()
    {
        try
        {
            var p = Ui.CursorPos();
            int w = Math.Max(8, (int)(_img.ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX / _zoom)), h = Math.Max(8, (int)(_img.ActualHeight * VisualTreeHelper.GetDpi(this).DpiScaleY / _zoom));
            var vb = ScreenCapture.VirtualBounds;
            var r = new SD.Rectangle(Math.Clamp(p.X - w / 2, vb.Left, Math.Max(vb.Left, vb.Right - w)), Math.Clamp(p.Y - h / 2, vb.Top, Math.Max(vb.Top, vb.Bottom - h)), Math.Min(w, vb.Width), Math.Min(h, vb.Height));
            using var bmp = ScreenCapture.Capture(r, includeCursor: false);
            _img.Source = Img.FromGdi(bmp);
            var c = bmp.GetPixel(Math.Clamp(p.X - r.X, 0, r.Width - 1), Math.Clamp(p.Y - r.Y, 0, r.Height - 1));
            _info.Text = $"{_zoom}×   ·   X {p.X}  Y {p.Y}   ·   #{c.R:X2}{c.G:X2}{c.B:X2}   ·   {Loc.T("rueda: nivel de zoom")}";
        }
        catch { }
    }
}

using System.IO;
using Path = System.Windows.Shapes.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using FranCapture.Core.Diagnostics;
using FranCapture.Core.Localization;
using FranCapture.Core.Text;
using FranCaptureStudio.Capture;
using FranCaptureStudio.Services;
using FranCaptureStudio.Views;

namespace FranCaptureStudio.Editor;

public partial class EditorWindow : StudioWindow
{
    string? _savedPath;          // archivo de imagen asociado (si existe, «Guardar» lo sobrescribe)
    string? _projectPath;
    bool _dirty;
    double _zoom = 1;
    readonly string _recoveryFile = System.IO.Path.Combine(AppPaths.RecoveryDir, Guid.NewGuid().ToString("N") + ".fcsproj");
    readonly DispatcherTimer _autosave;
    bool _autosavePending;
    TextBox? _textBox;
    readonly Dictionary<EditorTool, RadioButton> _toolButtons = [];

    static readonly Color[] Palette =
    [
        Color.FromRgb(255, 59, 48), Color.FromRgb(255, 149, 0), Color.FromRgb(255, 221, 0), Color.FromRgb(52, 199, 89),
        Color.FromRgb(0, 184, 255), Color.FromRgb(88, 86, 214), Color.FromRgb(255, 255, 255), Color.FromRgb(20, 20, 22),
    ];

    static readonly (EditorTool Tool, string Tip, string Path, bool Fill)[] Tools =
    [
        (EditorTool.Select, "Seleccionar y mover (V)", "M5,3 L5,19 L9.5,15 L12.5,21 L15,20 L12,14 L18,14 Z", false),
        (EditorTool.Crop, "Recortar (C)", "M7,2 V17 H22 M2,7 H17 V22", false),
        (EditorTool.Pen, "Lápiz (P)", "M4,20 L5,15 L16,4 L20,8 L9,19 Z M14,6 L18,10", false),
        (EditorTool.Line, "Línea (L)", "M4,20 L20,4", false),
        (EditorTool.Arrow, "Flecha (A)", "M4,20 L20,4 M11,4 H20 V13", false),
        (EditorTool.Rect, "Rectángulo (R)", "M4,6 H20 V18 H4 Z", false),
        (EditorTool.Ellipse, "Círculo / elipse (E)", "M12,5 A8,7 0 1 1 11.99,5 Z", false),
        (EditorTool.Text, "Texto (T)", "M5,7 V5 H19 V7 M12,5 V20 M9,20 H15", false),
        (EditorTool.Step, "Número de paso automático (N)", "M12,3 A9,9 0 1 1 11.99,3 Z M10,9.5 L12.5,8 V17", false),
        (EditorTool.Highlight, "Resaltador (H)", "M3,21 H21 M8,15 L15,4 L20,7 L13,18 Z M8,15 L7,18 L13,18", false),
        (EditorTool.Callout, "Cuadro de diálogo (G)", "M4,5 H20 V15 H11 L7,19 V15 H4 Z", false),
        (EditorTool.Pixelate, "Pixelar (X)", "M4,4 H10 V10 H4 Z M14,14 H20 V20 H14 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z", false),
        (EditorTool.Blur, "Desenfoque localizado (U)", "M12,3 C12,3 5,11 5,15 A7,7 0 0 0 19,15 C19,11 12,3 12,3 Z", false),
        (EditorTool.Redact, "Censura sólida irreversible (K)", "M3,8 H21 V16 H3 Z", true),
    ];

    public EditorWindow()
    {
        InitializeComponent();
        BuildToolRail();
        BuildSwatches();
        Canvas.Changed += () => { MarkDirty(); RefreshUi(); };
        Canvas.SelectionChanged += OnSelectionChanged;
        Canvas.PointerMoved += p => PosText.Text = p is { } pt ? $"X {(int)pt.X}   Y {(int)pt.Y}" : "";
        Canvas.TextEditRequested += BeginTextEdit;
        Canvas.CropChanged += RefreshCrop;
        _autosave = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _autosave.Tick += (_, _) => AutosaveNow();
        _autosave.Start();
        Loaded += (_, _) => { ZoomFit(); Canvas.Focus(); };
        PreviewKeyDown += OnPreviewKeyDown;
        RefreshUi();
    }

    // ---------- Apertura ----------

    public static EditorWindow Open(BitmapSource image, string? savedPath)
    {
        var w = new EditorWindow { _savedPath = savedPath };
        w.Canvas.Load(new EditorState { Base = Img.Normalize(image), Annotations = [] });
        w.SetTitle();
        w.RefreshUi();
        w.Show();
        w.Activate();
        return w;
    }

    public static void OpenEmpty()
    {
        var clip = ClipboardService.GetImage();
        if (clip != null) { Open(clip, null); return; }
        var file = Ui.OpenImageFile();
        if (file == null) return;
        try { Open(Img.Load(file), null).StatusText.Text = file; }
        catch (Exception ex) { Ui.Error(Loc.T("No se pudo abrir la imagen:") + "\n" + ex.Message); }
    }

    public static EditorWindow? OpenProject(string path, bool asRecovered = false)
    {
        try
        {
            var state = EditorProject.Load(path);
            var w = new EditorWindow();
            if (!asRecovered) w._projectPath = path;
            w.Canvas.Load(state);
            if (asRecovered) w.MarkDirty();
            w.SetTitle();
            w.RefreshUi();
            w.Show();
            return w;
        }
        catch (Exception ex)
        {
            Log.Error("Abrir proyecto", ex);
            Ui.Error(Loc.T("No se pudo abrir el proyecto:") + "\n" + ex.Message);
            return null;
        }
    }

    /// <summary>Si la aplicación se cerró inesperadamente con ediciones sin guardar, ofrece restaurarlas.</summary>
    public static void OfferRecovery()
    {
        try
        {
            var files = Directory.GetFiles(AppPaths.RecoveryDir, "*.fcsproj");
            if (files.Length == 0) return;
            bool restore = Ui.Confirm(Loc.T("Se encontraron {0} ediciones sin guardar de una sesión anterior. ¿Quieres restaurarlas?", files.Length), "Recuperar ediciones");
            foreach (var f in files)
            {
                if (restore) OpenProject(f, asRecovered: true);
                try { File.Delete(f); } catch { }
            }
        }
        catch (Exception ex) { Log.Error("Recuperación del editor", ex); }
    }

    void SetTitle()
    {
        var name = _projectPath != null ? System.IO.Path.GetFileName(_projectPath) : _savedPath != null ? System.IO.Path.GetFileName(_savedPath) : Loc.T("Sin título");
        Title = $"{name}{(_dirty ? " •" : "")} – Fran Capture Studio";
    }

    void MarkDirty()
    {
        _autosavePending = true;
        if (_dirty) return;
        _dirty = true; SetTitle();
    }

    void MarkClean()
    {
        _dirty = false; _autosavePending = false; SetTitle();
        try { File.Delete(_recoveryFile); } catch { }
    }

    void AutosaveNow()
    {
        if (!_dirty || !_autosavePending) return;
        _autosavePending = false;
        var snap = Canvas.State.Snapshot();
        Task.Run(() => { try { EditorProject.Save(_recoveryFile, snap); } catch (Exception ex) { Log.Warn("Autoguardado: " + ex.Message); } });
    }

    // ---------- Construcción de la interfaz ----------

    void BuildToolRail()
    {
        foreach (var (tool, tip, data, fill) in Tools)
        {
            var path = new Path { Data = Geometry.Parse(data), StrokeThickness = 1.7, Width = 24, Height = 24, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            var rb = new RadioButton { Style = Ui.S("ToolToggle"), Content = path, GroupName = "tools", ToolTip = Loc.T(tip), IsChecked = tool == EditorTool.Select };
            path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = rb });
            if (fill) path.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("Foreground") { Source = rb });
            rb.Checked += (_, _) => SetTool(tool, fromButton: true);
            _toolButtons[tool] = rb;
            ToolRail.Children.Add(rb);
            if (tool is EditorTool.Crop or EditorTool.Callout) ToolRail.Children.Add(new Border { Style = Ui.S("Separator"), Margin = new Thickness(4, 5, 4, 5) });
        }
    }

    void BuildSwatches()
    {
        foreach (var c in Palette)
        {
            var b = new Button
            {
                Width = 22, Height = 22, Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand, ToolTip = c.ToString()[3..].Insert(0, "#"),
                Template = SwatchTemplate(), Background = new SolidColorBrush(c), Tag = c,
            };
            b.Click += (_, _) => SetColor(c);
            Swatches.Children.Add(b);
        }
        HighlightSwatch();
    }

    static ControlTemplate SwatchTemplate()
    {
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        f.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        f.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        f.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        return new ControlTemplate(typeof(Button)) { VisualTree = f };
    }

    void HighlightSwatch()
    {
        foreach (Button b in Swatches.Children)
        {
            bool sel = (Color)b.Tag == Canvas.CurrentColor;
            b.BorderThickness = new Thickness(sel ? 3 : 1);
            b.BorderBrush = sel ? Ui.B("AccentBrush") : Ui.B("BorderBrush");
        }
    }

    void SetTool(EditorTool tool, bool fromButton = false)
    {
        CommitText();
        if (Canvas.Tool == EditorTool.Crop && tool != EditorTool.Crop) Canvas.CancelCrop();
        Canvas.Tool = tool;
        if (!fromButton) _toolButtons[tool].IsChecked = true;
        if (tool != EditorTool.Select) Canvas.Select(null);
        Canvas.Focus();
    }

    // ---------- Propiedades ----------

    bool _syncing;

    void OnSelectionChanged()
    {
        var s = Canvas.Selected;
        if (s != null)
        {
            _syncing = true;
            ThicknessSlider.Value = Math.Clamp(s.Thickness, 1, 30);
            FontSlider.Value = Math.Clamp(s.FontSize, 10, 96);
            StrengthSlider.Value = Math.Clamp(s.Strength, 4, 40);
            FillCheck.IsChecked = s.Filled;
            _syncing = false;
        }
        RefreshUi();
    }

    void SetColor(Color c)
    {
        Canvas.CurrentColor = c;
        HighlightSwatch();
        if (Canvas.Selected is { } s && s.Kind is not (AnnKind.Pixelate or AnnKind.Blur or AnnKind.Image)) Canvas.Mutate(() => s.Color = c);
    }

    void CustomColor_Click(object sender, RoutedEventArgs e)
    {
        if (Ui.PickColor(Canvas.CurrentColor) is { } c) SetColor(c);
    }

    void Thickness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThicknessText == null) return;
        ThicknessText.Text = ((int)e.NewValue).ToString();
        if (_syncing) return;
        Canvas.CurrentThickness = e.NewValue;
        if (Canvas.Selected is { } s) Canvas.Mutate(() => s.Thickness = e.NewValue);
    }

    void Font_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontText == null) return;
        FontText.Text = ((int)e.NewValue).ToString();
        if (_syncing) return;
        Canvas.CurrentFontSize = e.NewValue;
        if (_textBox != null) _textBox.FontSize = e.NewValue;
        if (Canvas.Selected is { } s && (s.HasText || s.Kind == AnnKind.Step)) Canvas.Mutate(() => s.FontSize = e.NewValue);
    }

    void Strength_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || Canvas == null) return;
        Canvas.CurrentStrength = (int)e.NewValue;
        if (Canvas.Selected is { } s && s.Kind is AnnKind.Pixelate or AnnKind.Blur) Canvas.Mutate(() => s.Strength = (int)e.NewValue);
    }

    void Fill_Click(object sender, RoutedEventArgs e)
    {
        Canvas.CurrentFilled = FillCheck.IsChecked == true;
        if (Canvas.Selected is { } s && s.Kind is AnnKind.Rect or AnnKind.Ellipse) Canvas.Mutate(() => s.Filled = Canvas.CurrentFilled);
    }

    void Duplicate_Click(object sender, RoutedEventArgs e) => Canvas.DuplicateSelected();
    void Front_Click(object sender, RoutedEventArgs e) => Canvas.BringSelectedToFront();
    void Delete_Click(object sender, RoutedEventArgs e) => Canvas.DeleteSelected();
    void Undo_Click(object sender, RoutedEventArgs e) { CommitText(); Canvas.Undo(); }
    void Redo_Click(object sender, RoutedEventArgs e) { CommitText(); Canvas.Redo(); }

    void RefreshUi()
    {
        UndoBtn.IsEnabled = Canvas.History.CanUndo;
        RedoBtn.IsEnabled = Canvas.History.CanRedo;
        bool sel = Canvas.Selected != null;
        DuplicateBtn.IsEnabled = FrontBtn.IsEnabled = DeleteBtn.IsEnabled = sel;
        SizeText.Text = $"{Canvas.ImageWidth} × {Canvas.ImageHeight} px";
    }

    void RefreshCrop()
    {
        CropPanel.Visibility = Canvas.Tool == EditorTool.Crop && Canvas.CropRect != null ? Visibility.Visible : Visibility.Collapsed;
        if (Canvas.CropRect is { } r) CropInfo.Text = $"{(int)r.Width} × {(int)r.Height} px";
        RefreshUi();
    }

    void ApplyCrop_Click(object sender, RoutedEventArgs e) { Canvas.ApplyCrop(); SetTool(EditorTool.Select); ZoomFit(); }
    void CancelCrop_Click(object sender, RoutedEventArgs e) => Canvas.CancelCrop();

    // ---------- Texto ----------

    void BeginTextEdit(Annotation a, bool isNew)
    {
        CommitText();
        var box = new TextBox
        {
            Text = a.Text, FontSize = a.FontSize, FontFamily = new FontFamily("Segoe UI"), FontWeight = FontWeights.SemiBold,
            AcceptsReturn = true, MinWidth = 60, Padding = new Thickness(2), BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(a.Kind == AnnKind.Callout ? ((a.Color.R * 0.299 + a.Color.G * 0.587 + a.Color.B * 0.114) > 150 ? Colors.Black : Colors.White) : a.Color),
            Background = new SolidColorBrush(a.Kind == AnnKind.Callout ? a.Color : Color.FromArgb(215, 30, 33, 40)),
            BorderBrush = Ui.B("AccentBrush"), Tag = (a, isNew), TextWrapping = a.Kind == AnnKind.Callout ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Style = null,
        };
        if (a.Kind == AnnKind.Callout) { var r = a.Bounds; box.Width = Math.Max(60, r.Width); box.MinHeight = r.Height; System.Windows.Controls.Canvas.SetLeft(box, r.X); System.Windows.Controls.Canvas.SetTop(box, r.Y); }
        else { System.Windows.Controls.Canvas.SetLeft(box, a.A.X - 3); System.Windows.Controls.Canvas.SetTop(box, a.A.Y - 3); }
        box.LostKeyboardFocus += (_, _) => CommitText();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CancelText(); e.Handled = true; }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { CommitText(); e.Handled = true; }
        };
        _textBox = box;
        Overlay.Children.Add(box);
        Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); }, DispatcherPriority.Input);
        StatusText.Text = Loc.T("Escribe el texto. Ctrl+Enter o clic fuera para confirmar, Esc para cancelar.");
    }

    void CancelText()
    {
        if (_textBox == null) return;
        var box = _textBox; _textBox = null;
        Overlay.Children.Remove(box);
        Canvas.Focus();
    }

    void CommitText()
    {
        if (_textBox == null) return;
        var box = _textBox; _textBox = null;
        var (a, isNew) = ((Annotation, bool))box.Tag;
        var text = box.Text.TrimEnd();
        Overlay.Children.Remove(box);
        if (isNew)
        {
            if (text.Length > 0) { a.Text = text; a.FontSize = box.FontSize; Canvas.Add(a); }
        }
        else if (text != a.Text)
        {
            if (text.Length == 0 && a.Kind == AnnKind.Text) { Canvas.Select(a); Canvas.DeleteSelected(); }
            else Canvas.Mutate(() => a.Text = text);
        }
        StatusText.Text = "";
    }

    // ---------- Zoom ----------

    double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.05, 16);
        Canvas.Zoom = _zoom / DpiScale;
        Host.LayoutTransform = new ScaleTransform(_zoom / DpiScale, _zoom / DpiScale);
        ZoomText.Text = $"{_zoom * 100:0} %";
        RenderOptions.SetBitmapScalingMode(Canvas, _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        Canvas.InvalidateVisual();
    }

    void ZoomFit()
    {
        double vw = Math.Max(100, Scroll.ActualWidth - 60), vh = Math.Max(100, Scroll.ActualHeight - 60);
        double fit = Math.Min(vw * DpiScale / Canvas.ImageWidth, vh * DpiScale / Canvas.ImageHeight);
        SetZoom(Math.Min(1, fit));
    }

    void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);
    void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.25);
    void ZoomFit_Click(object sender, RoutedEventArgs e) => ZoomFit();
    void Zoom100_Click(object sender, RoutedEventArgs e) => SetZoom(1);

    void Scroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        SetZoom(e.Delta > 0 ? _zoom * 1.15 : _zoom / 1.15);
        e.Handled = true;
    }

    // ---------- Teclado ----------

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_textBox != null) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        e.Handled = true;
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z: if (shift) Canvas.Redo(); else Canvas.Undo(); return;
                case Key.Y: Canvas.Redo(); return;
                case Key.S: if (shift) SaveAs_Click(this, e); else Save_Click(this, e); return;
                case Key.C: Copy_Click(this, e); return;
                case Key.D: Canvas.DuplicateSelected(); return;
                case Key.V: PasteImage(); return;
                case Key.O: OpenImage(); return;
                case Key.D0 or Key.NumPad0: ZoomFit(); return;
                case Key.D1 or Key.NumPad1: SetZoom(1); return;
                case Key.OemPlus or Key.Add: SetZoom(_zoom * 1.25); return;
                case Key.OemMinus or Key.Subtract: SetZoom(_zoom / 1.25); return;
            }
            e.Handled = false;
            return;
        }
        double step = shift ? 10 : 1;
        switch (e.Key)
        {
            case Key.Delete or Key.Back: Canvas.DeleteSelected(); return;
            case Key.Escape: if (Canvas.CropRect != null) Canvas.CancelCrop(); else if (Canvas.Selected != null) Canvas.Select(null); else SetTool(EditorTool.Select); return;
            case Key.Enter: if (Canvas.CropRect != null) ApplyCrop_Click(this, e); else e.Handled = false; return;
            case Key.Left: if (!Canvas.Nudge(new Vector(-step, 0))) e.Handled = false; return;
            case Key.Right: if (!Canvas.Nudge(new Vector(step, 0))) e.Handled = false; return;
            case Key.Up: if (!Canvas.Nudge(new Vector(0, -step))) e.Handled = false; return;
            case Key.Down: if (!Canvas.Nudge(new Vector(0, step))) e.Handled = false; return;
            case Key.V: SetTool(EditorTool.Select); return;
            case Key.C: SetTool(EditorTool.Crop); return;
            case Key.P: SetTool(EditorTool.Pen); return;
            case Key.L: SetTool(EditorTool.Line); return;
            case Key.A: SetTool(EditorTool.Arrow); return;
            case Key.R: SetTool(EditorTool.Rect); return;
            case Key.E: SetTool(EditorTool.Ellipse); return;
            case Key.T: SetTool(EditorTool.Text); return;
            case Key.N: SetTool(EditorTool.Step); return;
            case Key.H: SetTool(EditorTool.Highlight); return;
            case Key.G: SetTool(EditorTool.Callout); return;
            case Key.X: SetTool(EditorTool.Pixelate); return;
            case Key.U: SetTool(EditorTool.Blur); return;
            case Key.K: SetTool(EditorTool.Redact); return;
        }
        e.Handled = false;
    }

    // ---------- Menús ----------

    void OpenMenu(object sender, RoutedEventArgs e)
    {
        CommitText();
        var btn = (Button)sender;
        var menu = new ContextMenu { PlacementTarget = btn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string text, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = Loc.T(text), IsEnabled = enabled };
            mi.Click += (_, _) => { try { action(); } catch (Exception ex) { Log.Error(text, ex); Ui.Error(ex.Message, this); } };
            menu.Items.Add(mi);
        }
        void Sep() => menu.Items.Add(new Separator());

        if (btn == FileMenuBtn)
        {
            Item("Abrir imagen…  (Ctrl+O)", OpenImage);
            Item("Pegar imagen del portapapeles  (Ctrl+V)", PasteImage);
            Sep();
            Item("Abrir proyecto editable…", OpenProjectDialog);
            Item("Guardar proyecto editable…", () => SaveProject());
            Sep();
            Item("Guardar  (Ctrl+S)", () => Save());
            Item("Guardar como…  (Ctrl+Shift+S)", () => SaveAs());
            Item("Abrir carpeta del archivo", () => { if (_savedPath != null) Shell.RevealInExplorer(_savedPath); }, _savedPath != null);
        }
        else if (btn == ImageMenuBtn)
        {
            Item("Redimensionar…", ResizeImage);
            Item("Rotar 90° a la izquierda", () => Transform(i => Img.Rotate(i, 270)));
            Item("Rotar 90° a la derecha", () => Transform(i => Img.Rotate(i, 90)));
            Item("Voltear horizontalmente", () => Transform(i => Img.Flip(i, true)));
            Item("Voltear verticalmente", () => Transform(i => Img.Flip(i, false)));
            Sep();
            Item("Agregar borde…", AddBorder);
            Item("Agregar sombra", () => Transform(i => Img.AddShadow(i)));
            Item("Agregar marca de agua…", AddWatermark);
            Sep();
            Item("Insertar imagen…", InsertImage);
            Item("Eliminar fondo…", RemoveBackground);
        }
        else if (btn == PrivacyMenuBtn)
        {
            Item("Ocultar datos sensibles automáticamente (correos, teléfonos, tarjetas, IP)", () => _ = HideSensitiveAsync());
            Sep();
            Item("Herramienta: censura sólida (recomendada)", () => SetTool(EditorTool.Redact));
            Item("Herramienta: pixelar", () => SetTool(EditorTool.Pixelate));
            Item("Herramienta: desenfoque", () => SetTool(EditorTool.Blur));
            Sep();
            Item("¿Qué método protege mejor?", () => Ui.Info("La censura sólida sustituye los píxeles por un color opaco y es el único método que garantiza que el contenido no puede recuperarse.\n\nEl pixelado y el desenfoque ocultan a simple vista, pero con texto grande o patrones previsibles podrían revertirse parcialmente: no los uses para contraseñas, claves o datos bancarios.\n\nAl exportar, la imagen se aplana y el archivo no conserva lo que había debajo ni metadatos. El proyecto editable (.fcsproj) sí conserva la imagen original: no lo compartas si contiene datos privados.", this));
        }
        else
        {
            Item("Fijar en pantalla", () => FranCaptureStudio.Tools.PinnedImageWindow.Pin(Flatten()));
            Item("Extraer texto (OCR)", () => _ = Studio.RunOcrAsync(Flatten()));
            Item("Agregar al reporte de errores", AddToReport);
            Item("Leer código QR de la imagen", ReadQr);
        }
        menu.IsOpen = true;
    }

    BitmapSource Flatten() { CommitText(); return Canvas.FlattenNow(); }

    /// <summary>Rotar, voltear, etc.: las anotaciones se funden con la imagen (se puede deshacer).</summary>
    void Transform(Func<BitmapSource, BitmapSource> op)
    {
        var flat = Flatten();
        Canvas.ReplaceBase(op(flat), _ => false);
        ZoomFit();
    }

    void OpenImage()
    {
        var file = Ui.OpenImageFile(this);
        if (file == null || !ConfirmDiscard()) return;
        try
        {
            Canvas.Load(new EditorState { Base = Img.Load(file), Annotations = [] });
            _savedPath = null; _projectPath = null; MarkClean(); RefreshUi(); ZoomFit();
        }
        catch (Exception ex) { Ui.Error(Loc.T("No se pudo abrir la imagen:") + "\n" + ex.Message, this); }
    }

    void PasteImage()
    {
        var img = ClipboardService.GetImage();
        if (img == null) { StatusText.Text = Loc.T("El portapapeles no contiene una imagen."); return; }
        Canvas.Add(new Annotation { Kind = AnnKind.Image, Image = img, A = new Point(10, 10), B = new Point(10 + img.PixelWidth, 10 + img.PixelHeight) });
        SetTool(EditorTool.Select);
    }

    void InsertImage()
    {
        var file = Ui.OpenImageFile(this);
        if (file == null) return;
        var img = Img.Load(file);
        double scale = Math.Min(1, Math.Min(Canvas.ImageWidth * 0.6 / img.PixelWidth, Canvas.ImageHeight * 0.6 / img.PixelHeight));
        double w = img.PixelWidth * scale, h = img.PixelHeight * scale;
        var a = new Annotation { Kind = AnnKind.Image, Image = img, A = new Point((Canvas.ImageWidth - w) / 2, (Canvas.ImageHeight - h) / 2) };
        a.B = new Point(a.A.X + w, a.A.Y + h);
        SetTool(EditorTool.Select);
        Canvas.Add(a);
    }

    void ResizeImage()
    {
        var txt = Ui.Prompt("Redimensionar", Loc.T("Nuevo tamaño en píxeles (ancho x alto) o porcentaje (por ejemplo 50%). Tamaño actual: {0} x {1}", Canvas.ImageWidth, Canvas.ImageHeight), $"{Canvas.ImageWidth} x {Canvas.ImageHeight}", this);
        if (txt == null) return;
        txt = txt.Trim().ToLowerInvariant();
        int w, h;
        if (txt.EndsWith('%') && double.TryParse(txt.TrimEnd('%').Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct) && pct > 0)
        { w = (int)Math.Round(Canvas.ImageWidth * pct / 100); h = (int)Math.Round(Canvas.ImageHeight * pct / 100); }
        else
        {
            var parts = txt.Split(['x', '×', '*', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && int.TryParse(parts[0], out w)) h = (int)Math.Round(w * (double)Canvas.ImageHeight / Canvas.ImageWidth);
            else if (parts.Length != 2 || !int.TryParse(parts[0], out w) || !int.TryParse(parts[1], out h)) { Ui.Error("Formato no válido. Usa «ancho x alto» o un porcentaje.", this); return; }
        }
        if (w < 1 || h < 1 || w > 20000 || h > 20000) { Ui.Error("El tamaño debe estar entre 1 y 20000 píxeles.", this); return; }
        Transform(i => Img.Resize(i, w, h));
    }

    void AddBorder()
    {
        var txt = Ui.Prompt("Agregar borde", "Grosor del borde en píxeles (se usará el color seleccionado):", "8", this);
        if (txt == null || !int.TryParse(txt.Trim(), out var t) || t < 1 || t > 400) return;
        var color = Canvas.CurrentColor;
        Transform(i => Img.AddBorder(i, t, color));
    }

    void AddWatermark()
    {
        var txt = Ui.Prompt("Marca de agua", "Texto de la marca de agua:", Studio.Settings.WatermarkText, this);
        if (string.IsNullOrWhiteSpace(txt)) return;
        Studio.Settings.WatermarkText = txt.Trim(); Studio.SaveSettings();
        double size = Math.Clamp(Math.Min(Canvas.ImageWidth, Canvas.ImageHeight) / 22.0, 12, 60);
        var ft = AnnotationRenderer.Format(txt.Trim(), size, Brushes.White);
        // Se agrega como texto editable en la esquina inferior derecha.
        Canvas.Add(new Annotation
        {
            Kind = AnnKind.Text, Text = txt.Trim(), FontSize = size, Color = Color.FromArgb(200, 255, 255, 255),
            A = new Point(Math.Max(4, Canvas.ImageWidth - ft.Width - size), Math.Max(4, Canvas.ImageHeight - ft.Height - size * 0.6)),
        });
        SetTool(EditorTool.Select);
    }

    void RemoveBackground()
    {
        var result = FranCaptureStudio.Tools.BackgroundRemovalWindow.Run(Flatten(), this);
        if (result != null) { Canvas.ReplaceBase(result, _ => false); ZoomFit(); }
    }

    async Task HideSensitiveAsync()
    {
        try
        {
            StatusText.Text = Loc.T("Buscando datos sensibles con OCR local…");
            var ocr = await OcrService.RecognizeAsync(Canvas.State.Base, Studio.Settings.OcrLanguage);
            int count = 0;
            var added = new List<Annotation>();
            foreach (var line in ocr.Lines)
            {
                foreach (var (first, last, _) in SensitiveData.FindInWords(line.Words.Select(w => w.Text).ToList()))
                {
                    var r = Rect.Empty;
                    for (int i = first; i <= last; i++) r.Union(line.Words[i].Bounds);
                    r.Inflate(3, 3);
                    added.Add(new Annotation { Kind = AnnKind.Redact, Color = Colors.Black, A = r.TopLeft, B = r.BottomRight });
                    count++;
                }
            }
            if (count > 0) Canvas.Mutate(() => Canvas.State.Annotations.AddRange(added));
            StatusText.Text = count > 0
                ? Loc.T("Se ocultaron {0} datos con censura sólida. Revisa la imagen: la detección automática puede omitir información.", count)
                : Loc.T("No se detectaron datos sensibles automáticamente. Revisa la imagen y usa la censura sólida manualmente si hace falta.");
        }
        catch (Exception ex)
        {
            Log.Error("Ocultar datos sensibles", ex);
            StatusText.Text = Loc.T("No se pudo ejecutar el OCR:") + " " + ex.Message;
        }
    }

    void AddToReport()
    {
        var path = EnsureSavedForExport();
        if (path == null) return;
        Studio.ShowMain("qa");
        Studio.Main?.QaPage.AddAttachment(path);
    }

    void ReadQr()
    {
        var text = FranCaptureStudio.Tools.QrTools.Decode(Flatten());
        if (text == null) { Ui.Info("No se encontró ningún código QR en la imagen.", this); return; }
        ClipboardService.SetText(text);
        Ui.Info(Loc.T("Contenido del código QR (copiado al portapapeles):") + "\n\n" + text, this);
    }

    // ---------- Guardado ----------

    string? EnsureSavedForExport()
    {
        if (_dirty || _savedPath == null) return Save() ? _savedPath : null;
        return _savedPath;
    }

    void Save_Click(object sender, RoutedEventArgs e) => Save();
    void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAs();

    /// <summary>Escribe la imagen final en disco y confirma (o informa del error).</summary>
    public bool Save()
    {
        var img = Flatten();
        try
        {
            string path;
            if (_savedPath != null) path = _savedPath;
            else if (Studio.Settings.AskWhereToSave) return SaveAs();
            else path = Studio.Storage.NextImagePath();
            Studio.Storage.SaveImage(img, path);
            if (!File.Exists(path)) throw new IOException("El archivo no se creó.");
            _savedPath = path;
            Studio.Library.AddFile(path);
            MarkClean();
            StatusText.Text = Loc.T("Guardado en") + " " + path;
            Studio.Notify(Loc.T("Imagen guardada"), System.IO.Path.GetFileName(path), () => Shell.RevealInExplorer(path));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Guardar imagen", ex);
            Ui.Error(Loc.T("No se pudo guardar el archivo:") + "\n" + ex.Message, this);
            return false;
        }
    }

    public bool SaveAs()
    {
        var path = CaptureCoordinator.SaveAsDialog(Flatten(), null, this); // diálogo nativo de Windows
        if (path == null) return false;
        _savedPath = path;
        MarkClean();
        StatusText.Text = Loc.T("Guardado en") + " " + path;
        return true;
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        bool ok = ClipboardService.SetImage(Flatten());
        StatusText.Text = Loc.T(ok ? "Imagen copiada al portapapeles." : "No se pudo acceder al portapapeles.");
    }

    bool SaveProject()
    {
        CommitText();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Guardar proyecto editable"), Filter = "Proyecto de Fran Capture Studio|*.fcsproj", AddExtension = true,
            FileName = System.IO.Path.GetFileNameWithoutExtension(_projectPath ?? _savedPath ?? "Proyecto") + ".fcsproj",
            InitialDirectory = _projectPath != null ? System.IO.Path.GetDirectoryName(_projectPath) : Studio.Storage.CapturesFolder,
        };
        if (dlg.ShowDialog(this) != true) return false;
        try
        {
            EditorProject.Save(dlg.FileName, Canvas.State);
            _projectPath = dlg.FileName;
            MarkClean();
            StatusText.Text = Loc.T("Proyecto guardado en") + " " + dlg.FileName;
            return true;
        }
        catch (Exception ex) { Ui.Error(Loc.T("No se pudo guardar el proyecto:") + "\n" + ex.Message, this); return false; }
    }

    void OpenProjectDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Abrir proyecto editable"), Filter = "Proyecto de Fran Capture Studio|*.fcsproj" };
        if (dlg.ShowDialog(this) == true) OpenProject(dlg.FileName);
    }

    bool ConfirmDiscard()
    {
        if (!_dirty) return true;
        var r = MessageBox.Show(this, Loc.T("Hay cambios sin guardar. ¿Quieres guardarlos antes de continuar?"), "Fran Capture Studio", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (r == MessageBoxResult.Cancel) return false;
        return r != MessageBoxResult.Yes || Save();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        CommitText();
        if (!Studio.IsExiting && !ConfirmDiscard()) { e.Cancel = true; return; }
        if (Studio.IsExiting && _dirty) { try { EditorProject.Save(_recoveryFile, Canvas.State); } catch { } } // se ofrecerá recuperar al volver a abrir
        else try { File.Delete(_recoveryFile); } catch { }
        _autosave.Stop();
        base.OnClosing(e);
    }

    // Acceso para pruebas de integración
    internal EditorCanvas TestCanvas => Canvas;
    internal bool IsDirty => _dirty;
    internal string? SavedPath => _savedPath;
}

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FranCapture.Core.Diagnostics;
using FranCapture.Core.Localization;
using FranCapture.Core.Storage;
using FranCapture.Core.Video;
using FranCaptureStudio.Editor;
using FranCaptureStudio.Recording;
using FranCaptureStudio.Services;

namespace FranCaptureStudio.Views;

public sealed class VideoOverlay
{
    public required Annotation Ann { get; init; }
    public double Start { get; set; }
    public double End { get; set; }
    public override string ToString() =>
        $"{(Ann.Kind == AnnKind.Text ? "«" + (Ann.Text.Length > 18 ? Ann.Text[..18] + "…" : Ann.Text) + "»" : Loc.T("Flecha"))}  {TimeSpan.FromSeconds(Start):m\\:ss\\.f} – {TimeSpan.FromSeconds(End):m\\:ss\\.f}";
}

/// <summary>Capa sobre el video: muestra texto y flechas en su intervalo y permite dibujarlas o moverlas.</summary>
public sealed class VideoOverlayLayer : FrameworkElement
{
    public List<VideoOverlay> Items { get; } = [];
    public double Time { get; set; }
    public bool ArrowMode { get; set; }
    public VideoOverlay? Selected { get; set; }
    public event Action<Annotation>? ArrowDrawn;
    public event Action? ItemMoved;
    readonly AnnotationRenderer _renderer = new();
    static readonly System.Windows.Media.Imaging.BitmapSource Dummy = Img.FromPixels(new byte[16], 2, 2);
    Annotation? _draft; VideoOverlay? _moving; Point _last;

    public VideoOverlayLayer() { ClipToBounds = true; }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // recibir el ratón en toda la superficie
        foreach (var o in Items.Where(o => Time >= o.Start - 0.001 && Time <= o.End + 0.001)) _renderer.Draw(dc, o.Ann, Dummy);
        if (_draft != null) _renderer.Draw(dc, _draft, Dummy);
    }

    VideoOverlay? Hit(Point p) => Items.LastOrDefault(o => Time >= o.Start && Time <= o.End &&
        (o.Ann.Kind == AnnKind.Text ? o.Ann.Bounds.Contains(p) : Dist(p, o.Ann.A, o.Ann.B) < 14));

    static double Dist(Point p, Point a, Point b)
    {
        var ab = b - a; double l2 = ab.LengthSquared;
        double t = l2 < 1e-9 ? 0 : Math.Clamp(Vector.Multiply(p - a, ab) / l2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this); _last = p;
        if (ArrowMode) { _draft = new Annotation { Kind = AnnKind.Arrow, A = p, B = p, Color = Color.FromRgb(255, 59, 48), Thickness = Math.Max(4, ActualHeight / 110) }; CaptureMouse(); }
        else if (Hit(p) is { } h) { _moving = h; Selected = h; CaptureMouse(); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_draft != null) { _draft.B = p; InvalidateVisual(); }
        else if (_moving != null) { _moving.Ann.Move(p - _last); _last = p; InvalidateVisual(); }
        else Cursor = ArrowMode ? Cursors.Cross : Hit(p) != null ? Cursors.SizeAll : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_draft != null)
        {
            var d = _draft; _draft = null;
            if ((d.B - d.A).Length > 10) ArrowDrawn?.Invoke(d);
            InvalidateVisual();
        }
        if (_moving != null) { _moving = null; ItemMoved?.Invoke(); }
    }
}

/// <summary>Línea de tiempo: segmentos conservados/eliminados, divisiones y cabezal. Clic o arrastre para desplazarse.</summary>
public sealed class TimelineControl : FrameworkElement
{
    public EditTimeline? Model { get; set; }
    public double Position { get; set; }
    public List<VideoOverlay> Overlays { get; set; } = [];
    public event Action<double>? Seek;

    public TimelineControl() { Focusable = false; Cursor = Cursors.Hand; }

    double X(double t) => Model == null || Model.Duration <= 0 ? 0 : t / Model.Duration * ActualWidth;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var bg = Ui.B("Surface2Brush"); var border = Ui.B("BorderBrush"); var accent = Ui.B("AccentBrush"); var text2 = Ui.B("Text2Brush"); var danger = Ui.B("DangerBrush");
        double top = 16, barH = h - top - 6;
        dc.DrawRoundedRectangle(bg, new Pen(border, 1), new Rect(0, top, w, barH), 5, 5);
        if (Model == null || Model.Duration <= 0) return;
        var kept = accent.Clone(); kept.Opacity = 0.42;
        var removed = danger.Clone(); removed.Opacity = 0.16;
        int current = Model.IndexAt(Position);
        for (int i = 0; i < Model.Segments.Count; i++)
        {
            var s = Model.Segments[i];
            var r = new Rect(X(s.Start), top + 1, Math.Max(0, X(s.End) - X(s.Start)), barH - 2);
            dc.DrawRectangle(s.Removed ? removed : kept, null, r);
            if (s.Removed && r.Width > 6)
            {
                var pen = new Pen(danger, 1);
                dc.PushClip(new RectangleGeometry(r));
                for (double x = r.X - r.Height; x < r.Right; x += 9) dc.DrawLine(pen, new Point(x, r.Bottom), new Point(x + r.Height, r.Y));
                dc.Pop();
            }
            if (i == current) dc.DrawRectangle(null, new Pen(accent, 1.5), r);
            if (i > 0) dc.DrawLine(new Pen(Ui.B("TextBrush"), 1.5), new Point(r.X, top), new Point(r.X, top + barH));
        }
        // marcas de tiempo
        double step = NiceStep(Model.Duration, w);
        var face = new Typeface("Segoe UI");
        for (double t = 0; t <= Model.Duration + 1e-6; t += step)
        {
            double x = X(t);
            dc.DrawLine(new Pen(border, 1), new Point(x, top - 3), new Point(x, top));
            var ft = new FormattedText(TimeSpan.FromSeconds(t).ToString(Model.Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss"), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 10, text2, 1.0);
            if (x + ft.Width < w) dc.DrawText(ft, new Point(x + 2, 0));
        }
        // superposiciones
        foreach (var o in Overlays)
            dc.DrawRectangle(Brushes.Gold, null, new Rect(X(o.Start), top + barH - 5, Math.Max(2, X(o.End) - X(o.Start)), 3));
        double px = X(Position);
        dc.DrawLine(new Pen(Ui.B("TextBrush"), 2), new Point(px, top - 4), new Point(px, h));
        dc.DrawEllipse(Ui.B("TextBrush"), null, new Point(px, top - 4), 4, 4);
    }

    static double NiceStep(double duration, double width)
    {
        double target = duration / Math.Max(2, width / 90);
        foreach (var s in new double[] { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800, 3600 }) if (s >= target) return s;
        return 3600;
    }

    void SeekTo(Point p)
    {
        if (Model == null || ActualWidth <= 0) return;
        Seek?.Invoke(Math.Clamp(p.X / ActualWidth, 0, 1) * Model.Duration);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { CaptureMouse(); SeekTo(e.GetPosition(this)); }
    protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) SeekTo(e.GetPosition(this)); }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { if (IsMouseCaptured) ReleaseMouseCapture(); }
}

public partial class VideoEditorWindow : StudioWindow
{
    readonly string _file;
    MediaInfo _info = new();
    EditTimeline _timeline = new(0);
    readonly Stack<EditTimeline> _undo = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    bool _playing, _previewOk = true, _ready;
    double _pos;
    CancellationTokenSource? _exportCts;

    public static VideoEditorWindow Open(string file)
    {
        var w = new VideoEditorWindow(file);
        w.Show(); w.Activate();
        return w;
    }

    public VideoEditorWindow(string file)
    {
        _file = file;
        InitializeComponent();
        FileText.Text = Path.GetFileName(file);
        Timeline.Seek += t => SeekTo(t);
        OverlayLayer.ArrowDrawn += a => { AddOverlay(a); OverlayLayer.ArrowMode = false; ArrowBtn.Content = Loc.T("+ Flecha"); };
        OverlayLayer.ItemMoved += RefreshOverlays;
        _timer.Tick += (_, _) => Tick();
        PreviewKeyDown += OnKey;
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => { _timer.Stop(); try { Player.Close(); } catch { } _exportCts?.Cancel(); };
        if (Path.GetExtension(file).Equals(".gif", StringComparison.OrdinalIgnoreCase)) FormatBox.SelectedIndex = 2;
        GifFpsSlider.Value = Studio.Settings.GifFps;
    }

    async Task LoadAsync()
    {
        try
        {
            if (!FfmpegRunner.IsAvailable) { StatusText.Text = Loc.T(FfmpegRunner.MissingMessage); ExportBtn.IsEnabled = false; return; }
            _info = await FfmpegRunner.ProbeAsync(_file);
            if (!_info.HasVideo || _info.DurationSeconds <= 0) { StatusText.Text = Loc.T("No se pudo leer el video."); ExportBtn.IsEnabled = false; return; }
            _timeline = new EditTimeline(_info.DurationSeconds);
            Timeline.Model = _timeline; Timeline.Overlays = OverlayLayer.Items;
            Stage.Width = _info.Width; Stage.Height = _info.Height;
            InfoText.Text = $"{_info.Width} × {_info.Height} · {_info.Fps:0.##} fps · {FileNaming.FormatSize(new FileInfo(_file).Length)}" + (_info.HasAudio ? "" : " · " + Loc.T("sin audio"));
            MuteCheck.IsEnabled = _info.HasAudio;
            _ready = true;
            Player.Source = new Uri(_file);
            Player.Play(); Player.Pause();
            RefreshAll();
            _timer.Start();
        }
        catch (Exception ex) { Log.Error("Abrir video", ex); StatusText.Text = ex.Message; }
    }

    void Player_MediaOpened(object sender, RoutedEventArgs e) { Player.Position = TimeSpan.Zero; }
    void Player_MediaEnded(object sender, RoutedEventArgs e) { Pause(); }

    void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _previewOk = false;
        PreviewNote.Visibility = Visibility.Visible;
        PreviewNote.Text = Loc.T("Windows no puede reproducir este formato en la vista previa (habitual con WebM). El recorte, la división y la exportación siguen funcionando.");
    }

    // ---------- Reproducción ----------

    void Tick()
    {
        if (!_ready) return;
        if (_playing && _previewOk)
        {
            _pos = Player.Position.TotalSeconds;
            // saltar los segmentos eliminados durante la vista previa
            var seg = _timeline.Segments[_timeline.IndexAt(_pos)];
            if (seg.Removed)
            {
                var next = _timeline.Segments.FirstOrDefault(s => !s.Removed && s.Start >= seg.End - 1e-6);
                if (next == null) { Pause(); SeekTo(seg.Start); return; }
                Player.Position = TimeSpan.FromSeconds(next.Start + 0.01); _pos = next.Start;
            }
            if (_pos >= _timeline.Duration - 0.03) Pause();
            RefreshPosition();
        }
    }

    void SeekTo(double t)
    {
        _pos = Math.Clamp(t, 0, _timeline.Duration);
        if (_previewOk) Player.Position = TimeSpan.FromSeconds(_pos);
        RefreshPosition();
    }

    void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    void TogglePlay()
    {
        if (!_ready || !_previewOk) return;
        if (_playing) { Pause(); return; }
        if (_pos >= _timeline.Duration - 0.05) SeekTo(0);
        Player.SpeedRatio = Speed; Player.IsMuted = MuteCheck.IsChecked == true;
        Player.Play(); _playing = true; PlayBtn.Content = "";
    }

    void Pause()
    {
        if (_previewOk) Player.Pause();
        _playing = false; PlayBtn.Content = "";
    }

    double Speed => double.Parse((string)((ComboBoxItem)SpeedBox.SelectedItem).Tag, CultureInfo.InvariantCulture);

    void Speed_Changed(object sender, SelectionChangedEventArgs e) { if (_ready) { Player.SpeedRatio = Speed; RefreshAll(); } }
    void Mute_Click(object sender, RoutedEventArgs e) => Player.IsMuted = MuteCheck.IsChecked == true;

    static string Fmt(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(@"mm\:ss\.f");

    void RefreshPosition()
    {
        TimeText.Text = $"{Fmt(_pos)} / {Fmt(_timeline.Duration)}";
        Timeline.Position = _pos; Timeline.InvalidateVisual();
        OverlayLayer.Time = _pos; OverlayLayer.InvalidateVisual();
    }

    void RefreshAll()
    {
        RefreshPosition();
        double outDur = _timeline.KeptDuration / Speed;
        ResultText.Text = Loc.T("Duración resultante: {0}", Fmt(outDur));
    }

    // ---------- Edición ----------

    void PushUndo() => _undo.Push(_timeline.Clone());
    void Apply() { Timeline.Model = _timeline; RefreshAll(); }

    void TrimStart_Click(object sender, RoutedEventArgs e) { PushUndo(); _timeline.TrimStart(_pos); Apply(); }
    void TrimEnd_Click(object sender, RoutedEventArgs e) { PushUndo(); _timeline.TrimEnd(_pos); Apply(); }

    void Split_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        if (!_timeline.Split(_pos)) { _undo.Pop(); StatusText.Text = Loc.T("Coloca el cabezal lejos de un corte existente para dividir."); }
        Apply();
    }

    void ToggleSegment_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        int i = _timeline.IndexAt(_pos);
        _timeline.SetRemoved(i, !_timeline.Segments[i].Removed);
        Apply();
    }

    void UndoEdit_Click(object sender, RoutedEventArgs e) { if (_undo.Count > 0) { _timeline = _undo.Pop(); Apply(); } }
    void Reset_Click(object sender, RoutedEventArgs e) { PushUndo(); _timeline.Reset(); Apply(); }

    void AddOverlay(Annotation a)
    {
        var o = new VideoOverlay { Ann = a, Start = _pos, End = Math.Min(_timeline.Duration, _pos + 3) };
        OverlayLayer.Items.Add(o);
        RefreshOverlays();
        OverlayBox.SelectedItem = o;
    }

    void RefreshOverlays()
    {
        var sel = OverlayBox.SelectedItem;
        OverlayBox.Items.Clear();
        foreach (var o in OverlayLayer.Items) OverlayBox.Items.Add(o);
        if (sel != null && OverlayBox.Items.Contains(sel)) OverlayBox.SelectedItem = sel;
        RefreshPosition();
    }

    void AddText_Click(object sender, RoutedEventArgs e)
    {
        var text = Ui.Prompt("Agregar texto", "Texto que aparecerá durante 3 segundos desde la posición actual (después puedes moverlo y ajustar su intervalo):", "", this);
        if (string.IsNullOrWhiteSpace(text)) return;
        double size = Math.Max(18, _info.Height / 18.0);
        var ft = AnnotationRenderer.Format(text.Trim(), size, Brushes.White);
        AddOverlay(new Annotation { Kind = AnnKind.Text, Text = text.Trim(), FontSize = size, Color = Colors.White, A = new Point(Math.Max(8, (_info.Width - ft.Width) / 2), Math.Max(8, _info.Height * 0.82 - ft.Height / 2)) });
    }

    void AddArrow_Click(object sender, RoutedEventArgs e)
    {
        OverlayLayer.ArrowMode = !OverlayLayer.ArrowMode;
        ArrowBtn.Content = Loc.T(OverlayLayer.ArrowMode ? "Dibuja la flecha…" : "+ Flecha");
    }

    void OverlayBox_Changed(object sender, SelectionChangedEventArgs e) => OverlayLayer.Selected = OverlayBox.SelectedItem as VideoOverlay;

    void OverlayStart_Click(object sender, RoutedEventArgs e)
    {
        if (OverlayBox.SelectedItem is not VideoOverlay o) return;
        o.Start = Math.Min(_pos, o.End - 0.1); RefreshOverlays();
    }

    void OverlayEnd_Click(object sender, RoutedEventArgs e)
    {
        if (OverlayBox.SelectedItem is not VideoOverlay o) return;
        o.End = Math.Max(_pos, o.Start + 0.1); RefreshOverlays();
    }

    void OverlayRemove_Click(object sender, RoutedEventArgs e)
    {
        if (OverlayBox.SelectedItem is VideoOverlay o) { OverlayLayer.Items.Remove(o); RefreshOverlays(); }
    }

    void Format_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (GifPanel == null) return;
        bool gif = FormatBox.SelectedIndex == 2;
        GifPanel.Visibility = gif ? Visibility.Visible : Visibility.Collapsed;
        QualityBox.IsEnabled = ResolutionBox.IsEnabled = !gif;
    }

    void GifOpt_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (GifFpsText != null) GifFpsText.Text = ((int)e.NewValue).ToString(); }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox || !_ready) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0, shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        double frame = 1 / Math.Max(1, _info.Fps > 0 ? _info.Fps : 30);
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: SeekTo(_pos - (ctrl ? frame : shift ? 5 : 1)); break;
            case Key.Right: SeekTo(_pos + (ctrl ? frame : shift ? 5 : 1)); break;
            case Key.Home: SeekTo(0); break;
            case Key.End: SeekTo(_timeline.Duration); break;
            case Key.I: TrimStart_Click(this, e); break;
            case Key.O: TrimEnd_Click(this, e); break;
            case Key.S when !ctrl: Split_Click(this, e); break;
            case Key.Delete: ToggleSegment_Click(this, e); break;
            case Key.Z when ctrl: UndoEdit_Click(this, e); break;
            default: e.Handled = false; break;
        }
    }

    // ---------- Portada y exportación ----------

    async void Cover_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cover = Path.ChangeExtension(_file, null) + ".portada.png";
            var r = await FfmpegRunner.RunAsync(Ffmpeg.ExtractFrameArgs(_file, _pos, cover));
            if (r.ExitCode != 0 || !File.Exists(cover)) throw new IOException(r.Error.Trim());
            Studio.Library.RaiseChanged();
            StatusText.Text = Loc.T("Portada guardada: se usa como miniatura en la biblioteca.") + " " + cover;
        }
        catch (Exception ex) { StatusText.Text = Loc.T("No se pudo guardar la portada:") + " " + ex.Message; }
    }

    void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

    async void Export_Click(object sender, RoutedEventArgs e)
    {
        string ext = FormatBox.SelectedIndex switch { 1 => "webm", 2 => "gif", _ => "mp4" };
        var kept = _timeline.KeptRanges();
        if (kept.Count == 0) { Ui.Error("No queda ningún segmento que exportar. Restaura alguno.", this); return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Exportar"), Filter = ext == "gif" ? "GIF|*.gif" : ext == "webm" ? "WebM|*.webm" : "MP4 (H.264)|*.mp4", AddExtension = true,
            FileName = Path.GetFileNameWithoutExtension(_file) + "_editado." + ext,
            InitialDirectory = ext == "gif" ? Studio.Storage.GifsFolder : Studio.Storage.VideosFolder,
        };
        if (dlg.ShowDialog(this) != true) return;
        if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(_file), StringComparison.OrdinalIgnoreCase)) { Ui.Error("Elige un nombre distinto al del video original.", this); return; }
        Pause();
        await ExportAsync(dlg.FileName);
    }

    /// <summary>Exporta sin bloquear la interfaz, con progreso y posibilidad de cancelar.</summary>
    public async Task<bool> ExportAsync(string output)
    {
        string ext = Path.GetExtension(output).TrimStart('.').ToLowerInvariant();
        var tempFiles = new List<string>();
        _exportCts = new CancellationTokenSource();
        ExportBtn.IsEnabled = false; Progress.Visibility = CancelBtn.Visibility = Visibility.Visible; Progress.Value = 0;
        StatusText.Text = Loc.T("Exportando…");
        try
        {
            var specs = new List<OverlaySpec>();
            var renderer = new AnnotationRenderer();
            var dummy = Img.FromPixels(new byte[16], 2, 2);
            foreach (var o in OverlayLayer.Items)
            {
                if (_timeline.MapInterval(o.Start, o.End) is not { } iv) continue;
                var png = Path.Combine(AppPaths.TempDir, Guid.NewGuid().ToString("N") + ".png");
                Img.Save(Img.Draw(_info.Width, _info.Height, dc => renderer.Draw(dc, o.Ann, dummy)), png);
                tempFiles.Add(png);
                specs.Add(new OverlaySpec(png, iv.Start, iv.End));
            }
            VideoEncoder enc = ext == "gif" ? VideoEncoder.Mpeg4 : await EncoderSelector.GetAsync(Studio.Settings.HardwareAcceleration, ext == "webm");
            int scale = int.Parse((string)((ComboBoxItem)ResolutionBox.SelectedItem).Tag);
            double speed = Speed;
            double total = Math.Max(0.1, _timeline.KeptDuration / speed);
            var opts = new EditExportOptions
            {
                Input = _file, Output = output, Segments = _timeline.KeptRanges(), Speed = speed, RemoveAudio = MuteCheck.IsChecked == true, InputHasAudio = _info.HasAudio,
                Overlays = specs, Encoder = enc, Quality = (string)((ComboBoxItem)QualityBox.SelectedItem).Tag, Width = _info.Width, Height = _info.Height,
                Fps = ext == "gif" ? GifFpsSlider.Value : (_info.Fps > 0 ? _info.Fps : 30), ScaleHeight = ext != "gif" && scale > 0 ? scale : null,
                GifMaxWidth = int.Parse((string)((ComboBoxItem)GifWidthBox.SelectedItem).Tag),
            };
            var r = await FfmpegRunner.RunAsync(Ffmpeg.EditExportArgs(opts), _exportCts.Token, line =>
            {
                if (Ffmpeg.ParseProgressSeconds(line) is { } sec) Dispatcher.BeginInvoke(() => Progress.Value = Math.Clamp(sec / total, 0, 1));
            });
            if (r.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            {
                try { File.Delete(output); } catch { }
                throw new IOException(Loc.T("FFmpeg no pudo exportar el video.") + "\n" + r.Error.Trim());
            }
            Studio.Library.AddFile(output);
            StatusText.Text = Loc.T("Exportado:") + $" {output} ({FileNaming.FormatSize(new FileInfo(output).Length)})";
            Studio.Notify(Loc.T("Exportación completada"), Path.GetFileName(output), () => Shell.RevealInExplorer(output));
            return true;
        }
        catch (OperationCanceledException)
        {
            try { File.Delete(output); } catch { }
            StatusText.Text = Loc.T("Exportación cancelada.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Exportar video", ex);
            StatusText.Text = Loc.T("Error al exportar.");
            if (IsVisible) Ui.Error(ex.Message, this);
            return false;
        }
        finally
        {
            foreach (var f in tempFiles) try { File.Delete(f); } catch { }
            ExportBtn.IsEnabled = true; Progress.Visibility = CancelBtn.Visibility = Visibility.Collapsed;
            _exportCts = null;
        }
    }

    // Acceso para pruebas de integración
    internal EditTimeline TestTimeline => _timeline;
    internal Task TestLoadAsync() => LoadAsync();
    internal void TestAddText(string text) => AddOverlay(new Annotation { Kind = AnnKind.Text, Text = text, FontSize = 28, Color = Colors.White, A = new Point(20, 20) });
}

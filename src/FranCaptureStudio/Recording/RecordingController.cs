using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FranCapture.Core.Automation;
using FranCapture.Core.Diagnostics;
using FranCapture.Core.Localization;
using FranCapture.Core.Video;
using FranCaptureStudio.Capture;
using FranCaptureStudio.Interop;
using FranCaptureStudio.Services;
using FranCaptureStudio.Views;
using SD = System.Drawing;

namespace FranCaptureStudio.Recording;

public sealed class RecordingTarget
{
    public SD.Rectangle? Region { get; init; }
    public IntPtr Window { get; init; }
    public int? MonitorIndex { get; init; }
    public bool AllScreens { get; init; }
    public bool Gif { get; init; }
}

/// <summary>Controla el ciclo de vida de una grabación desde la interfaz: cuenta regresiva, barra flotante, finalización y recuperación.</summary>
public sealed class RecordingController
{
    RecordingSession? _session;
    RecordingBarWindow? _bar;
    WebcamWindow? _cam;
    bool _starting, _stopping;
    CancellationTokenSource? _startCts;

    public bool IsActive => _session != null || _starting;
    public bool IsPaused => _session?.IsPaused == true;
    public TimeSpan Elapsed => _session?.Elapsed ?? TimeSpan.Zero;
    public RecordingSession? Session => _session;
    public event Action? StateChanged;
    public event Action<string>? Completed;

    public static RecordingOptions OptionsFromSettings(bool gif, IntPtr followWindow = default)
    {
        var s = Studio.Settings;
        return new RecordingOptions
        {
            Fps = gif ? Math.Min(30, Math.Max(s.GifFps, 10)) : s.RecordFps,
            Quality = s.RecordQuality,
            ScaleHeight = s.RecordResolution == "original" ? null : int.Parse(s.RecordResolution),
            Format = gif ? "gif" : s.VideoFormat,
            Cursor = s.RecordCursor, HighlightClicks = s.HighlightClicks, ShowKeys = s.ShowKeys,
            Mic = s.RecordMic && !gif, SystemAudio = s.RecordSystemAudio && !gif, MicDeviceId = s.MicDeviceId,
            HardwareAcceleration = s.HardwareAcceleration, GifFps = s.GifFps, GifMaxWidth = s.GifMaxWidth, FollowWindow = followWindow,
        };
    }

    public async Task StartWithRegionPickerAsync(bool gif)
    {
        if (IsActive || Studio.Capture.IsBusy) return;
        using var res = OverlayForm.Show(new OverlayOptions { Purpose = OverlayPurpose.SelectOnly, AcceptLabel = gif ? "Grabar GIF" : "Grabar esta región" });
        if (res.Action == OverlayAction.Cancel) return;
        await StartAsync(new RecordingTarget { Region = res.ScreenRect, Gif = gif });
    }

    public async Task StartWithWindowPickerAsync()
    {
        if (IsActive || Studio.Capture.IsBusy) return;
        var windows = ScreenCapture.TopLevelWindows();
        using var res = OverlayForm.Show(new OverlayOptions { Mode = OverlayMode.Window });
        if (res.Action == OverlayAction.Cancel) return;
        var win = windows.FirstOrDefault(w => SD.Rectangle.Intersect(w.Bounds, ScreenCapture.VirtualBounds) == res.ScreenRect);
        await StartAsync(new RecordingTarget { Region = res.ScreenRect, Window = win?.Handle ?? IntPtr.Zero });
    }

    public async Task StartAsync(RecordingTarget target)
    {
        if (IsActive) return;
        if (!FfmpegRunner.IsAvailable) { Ui.Error(FfmpegRunner.MissingMessage); return; }
        _starting = true;
        _startCts = new CancellationTokenSource();
        StateChanged?.Invoke();
        try
        {
            SD.Rectangle rect;
            if (target.Region is { } r) rect = r;
            else if (target.Window != IntPtr.Zero && Native.TryGetWindowBounds(target.Window, out var wr)) rect = new SD.Rectangle(wr.Left, wr.Top, wr.Width, wr.Height);
            else if (target.AllScreens) rect = ScreenCapture.VirtualBounds;
            else
            {
                var mons = ScreenCapture.Monitors();
                rect = target.MonitorIndex is { } mi && mi >= 0 && mi < mons.Count ? mons[mi].Bounds : Ui.MonitorAt(Ui.CursorPos());
            }
            var s = Studio.Settings;
            if (s.ShowWebcam && !target.Gif && !string.IsNullOrEmpty(s.WebcamDevice))
            {
                _cam = new WebcamWindow(s.WebcamDevice);
                _cam.Show();
                var wa = Ui.WorkAreaAt(new SD.Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
                Ui.PlaceIn(_cam, SD.Rectangle.Intersect(rect, wa) is { Width: > 0 } inter ? inter : wa, 0.97, 0.95);
            }
            var session = new RecordingSession(rect, OptionsFromSettings(target.Gif, target.Window));
            // Elegir el codificador mientras corre la cuenta regresiva.
            var encoderTask = EncoderSelector.GetAsync(session.Options.HardwareAcceleration, session.Options.Format == "webm");
            await CountdownWindow.RunAsync(s.Countdown, Ui.MonitorAt(new SD.Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2)), _startCts.Token);
            await encoderTask;
            session.Failed += msg => Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                Studio.Notify(Loc.T("Grabación interrumpida"), Loc.T(msg) + " " + Loc.T("Se intentará conservar lo grabado."), error: true);
                await StopAsync();
            });
            await session.StartAsync();
            _session = session;
            _bar = new RecordingBarWindow(this);
            _bar.Show();
            var mon = Ui.WorkAreaAt(new SD.Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
            Ui.PlaceIn(_bar, mon, 0.5, 0.985);
            if (s.Sounds) Sounds.RecordStart();
        }
        catch (OperationCanceledException) { CloseAux(); }
        catch (Exception ex)
        {
            Log.Error("Iniciar grabación", ex);
            CloseAux();
            Ui.Error(Loc.T("No se pudo iniciar la grabación:") + "\n" + Loc.T(ex.Message));
        }
        finally
        {
            _starting = false;
            StateChanged?.Invoke();
        }
    }

    void CloseAux()
    {
        try { _bar?.ForceClose(); } catch { }
        try { _cam?.Close(); } catch { }
        _bar = null; _cam = null;
    }

    public void TogglePause()
    {
        if (_session == null || _stopping) return;
        if (_session.IsPaused) _session.Resume(); else _session.Pause();
        StateChanged?.Invoke();
    }

    public async Task<string?> StopAsync(bool discard = false)
    {
        if (_starting && _session == null) { _startCts?.Cancel(); return null; }
        if (_session == null || _stopping) return null;
        _stopping = true;
        var session = _session;
        _bar?.SetBusy(Loc.T(discard ? "Descartando…" : "Finalizando…"));
        string? output = null;
        try
        {
            await session.StopCaptureAsync();
            if (Studio.Settings.Sounds) Sounds.RecordStop();
            if (discard) { session.Discard(); return null; }
            var path = Studio.Storage.NextVideoPath(session.Options.Format);
            output = await session.FinalizeAsync(path);
            Studio.Library.AddFile(output);
            Log.Info($"Grabación finalizada ({session.Elapsed:mm\\:ss}, {session.FramesWritten} fotogramas, {session.Encoder?.FfmpegName})");
            Completed?.Invoke(output);
            var plan = AutomationPlan.For(AutomationTrigger.Recording, Studio.Settings.Automations);
            if (plan.OpenVideoEditor && session.Options.Format != "gif") VideoEditorWindow.Open(output);
            else Studio.Notify(Loc.T(session.Options.Format == "gif" ? "GIF guardado" : "Grabación guardada"), Path.GetFileName(output), () => Shell.RevealInExplorer(output));
        }
        catch (Exception ex)
        {
            Log.Error("Finalizar grabación", ex);
            Ui.Error(Loc.T("No se pudo finalizar la grabación:") + "\n" + ex.Message);
        }
        finally
        {
            _session = null; _stopping = false;
            CloseAux();
            StateChanged?.Invoke();
        }
        return output;
    }

    /// <summary>Al salir de la aplicación con una grabación en curso: cerrarla y guardarla sin interfaz.</summary>
    public void StopForExit()
    {
        var session = _session;
        if (session == null) return;
        _session = null;
        try
        {
            Task.Run(session.StopCaptureAsync).Wait(TimeSpan.FromSeconds(20));
            var path = Studio.Storage.NextVideoPath(session.Options.Format);
            if (Task.Run(() => session.FinalizeAsync(path)).Wait(TimeSpan.FromSeconds(90))) Studio.Library.AddFile(path, notify: false);
        }
        catch (Exception ex) { Log.Error("Guardar grabación al salir", ex); } // los temporales quedan para recuperación
        CloseAux();
    }

    /// <summary>Ofrece recuperar grabaciones que quedaron a medias por un cierre inesperado.</summary>
    public async void OfferRecovery()
    {
        try
        {
            foreach (var (dir, m) in RecordingSession.FindOrphans())
            {
                if (!Ui.Confirm(Loc.T("Se encontró una grabación interrumpida del {0}. ¿Quieres recuperarla?", m.Started.ToString("g")) + "\n" + Loc.T("Si eliges «No», se eliminarán sus archivos temporales."), "Recuperar grabación"))
                {
                    try { Directory.Delete(dir, true); } catch { }
                    continue;
                }
                try
                {
                    var ext = m.Format is "webm" or "gif" ? m.Format : "mp4";
                    var name = "Recuperado_" + m.Started.ToString("yyyy-MM-dd_HH-mm-ss");
                    var path = FranCapture.Core.Storage.FileNaming.UniquePath(ext == "gif" ? Studio.Storage.GifsFolder : Studio.Storage.VideosFolder, name, ext);
                    await RecordingSession.FinalizeTempAsync(dir, path, ext, m.GifFps, m.GifMaxWidth);
                    Studio.Library.AddFile(path);
                    Studio.Notify(Loc.T("Grabación recuperada"), Path.GetFileName(path), () => Shell.RevealInExplorer(path));
                }
                catch (Exception ex)
                {
                    Log.Error("Recuperar grabación", ex);
                    Ui.Error(Loc.T("No se pudo recuperar la grabación:") + "\n" + ex.Message);
                }
            }
        }
        catch (Exception ex) { Log.Error("Recuperación", ex); }
    }
}

/// <summary>Barra flotante de grabación. Se excluye de la captura con SetWindowDisplayAffinity.</summary>
public sealed class RecordingBarWindow : Window
{
    readonly RecordingController _ctl;
    readonly TextBlock _time, _dot, _pauseIcon, _mic, _sys, _status;
    readonly Button _pause, _stop, _discard;
    readonly DispatcherTimer _timer;
    bool _force;

    static TextBlock Glyph(string g, double size = 15) => new()
    {
        Text = g, FontFamily = (FontFamily)Application.Current.Resources["IconFont"], FontSize = size, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White,
    };

    Button Btn(TextBlock icon, string tip, Action click)
    {
        var b = new Button { Content = icon, Style = Ui.S("IconButton"), ToolTip = Loc.T(tip), Foreground = Brushes.White, Margin = new Thickness(2, 0, 2, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    public RecordingBarWindow(RecordingController ctl)
    {
        _ctl = ctl;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize; ShowActivated = false;
        Title = "Fran Capture Studio – grabación";
        _dot = new TextBlock { Text = "●", Foreground = new SolidColorBrush(Color.FromRgb(255, 69, 58)), FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
        _time = new TextBlock { Text = "00:00", Foreground = Brushes.White, FontSize = 15, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Consolas, Segoe UI"), VerticalAlignment = VerticalAlignment.Center, MinWidth = 58 };
        _pauseIcon = Glyph("");
        _pause = Btn(_pauseIcon, "Pausar / reanudar", ctl.TogglePause);
        _stop = Btn(Glyph(""), "Detener y guardar", () => _ = ctl.StopAsync());
        _discard = Btn(Glyph(""), "Descartar grabación", () => { if (Ui.Confirm("¿Descartar esta grabación? No se guardará.")) _ = ctl.StopAsync(discard: true); });
        _mic = Glyph("", 14); _mic.Margin = new Thickness(8, 0, 4, 0);
        _sys = Glyph("", 14); _sys.Margin = new Thickness(4, 0, 6, 0);
        _status = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0), Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Children = { _dot, _time, _pause, _stop, _discard, _mic, _sys, _status } };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(24, 27, 33)), BorderBrush = new SolidColorBrush(Color.FromRgb(64, 70, 82)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 6, 8, 6), Margin = new Thickness(6), Child = panel,
        };
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = Ui.Handle(this);
        Native.ExcludeFromCapture(h); // la barra queda fuera del video siempre que Windows lo permita
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
    }

    void Refresh()
    {
        var s = _ctl.Session;
        if (s == null) return;
        var t = s.Elapsed;
        _time.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");
        bool paused = s.IsPaused;
        _pauseIcon.Text = paused ? "" : "";
        _dot.Opacity = paused ? 0.35 : (DateTime.Now.Millisecond < 500 ? 1 : 0.55);
        _mic.Opacity = s.MicActive ? 1 : 0.28; _mic.ToolTip = Loc.T(s.MicActive ? "Micrófono: grabando" : "Micrófono: desactivado");
        _sys.Opacity = s.SystemAudioActive ? 1 : 0.28; _sys.ToolTip = Loc.T(s.SystemAudioActive ? "Audio del sistema: grabando" : "Audio del sistema: desactivado");
    }

    public void SetBusy(string text)
    {
        _pause.IsEnabled = _stop.IsEnabled = _discard.IsEnabled = false;
        _status.Text = text; _status.Visibility = Visibility.Visible;
    }

    public void ForceClose() { _force = true; _timer.Stop(); Close(); }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_force) e.Cancel = true; // solo se cierra al terminar la grabación
        base.OnClosing(e);
    }
}

/// <summary>Vista de cámara web flotante (se graba como parte de la pantalla). Los fotogramas los entrega FFmpeg/DirectShow.</summary>
public sealed class WebcamWindow : Window
{
    const int Size = 240;
    readonly WriteableBitmap _bmp = new(Size, Size, 96, 96, PixelFormats.Bgra32, null);
    Process? _proc;
    volatile bool _closed;

    public static async Task<List<string>> ListDevicesAsync()
    {
        if (!FfmpegRunner.IsAvailable) return [];
        try { return Ffmpeg.ParseDshowVideoDevices((await FfmpegRunner.RunAsync(Ffmpeg.ListDshowArgs)).Error); }
        catch (Exception ex) { Log.Warn("Enumerar cámaras: " + ex.Message); return []; }
    }

    public WebcamWindow(string device)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false;
        Width = 200; Height = 200; ResizeMode = ResizeMode.NoResize; ShowActivated = false; Title = "Cámara";
        var ellipse = new System.Windows.Shapes.Ellipse
        {
            Fill = new ImageBrush(_bmp) { Stretch = Stretch.UniformToFill }, Stroke = new SolidColorBrush(Color.FromRgb(0, 184, 255)), StrokeThickness = 3,
        };
        Content = ellipse;
        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        MouseWheel += (_, e) => { Width = Height = Math.Clamp(Width + (e.Delta > 0 ? 20 : -20), 100, 480); };
        ToolTip = Loc.T("Arrastra para mover · rueda para cambiar el tamaño");
        Loaded += (_, _) => StartCapture(device);
    }

    void StartCapture(string device)
    {
        try
        {
            _proc = Process.Start(FfmpegRunner.StartInfo(Ffmpeg.WebcamArgs(device, Size)))!;
            _ = _proc.StandardError.ReadToEndAsync();
            var stream = _proc.StandardOutput.BaseStream;
            var thread = new Thread(() =>
            {
                var buf = new byte[Size * Size * 4];
                try
                {
                    while (!_closed)
                    {
                        int read = 0;
                        while (read < buf.Length) { int n = stream.Read(buf, read, buf.Length - read); if (n <= 0) return; read += n; }
                        var frame = (byte[])buf.Clone();
                        Dispatcher.BeginInvoke(() => { if (!_closed) _bmp.WritePixels(new Int32Rect(0, 0, Size, Size), frame, Size * 4, 0); });
                    }
                }
                catch { }
            }) { IsBackground = true, Name = "FCS-Cámara" };
            thread.Start();
        }
        catch (Exception ex) { Log.Warn("Cámara: " + ex.Message); }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        _proc?.Dispose();
        base.OnClosed(e);
    }
}

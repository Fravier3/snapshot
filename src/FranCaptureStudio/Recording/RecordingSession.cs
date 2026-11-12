using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FranCapture.Core.Diagnostics;
using FranCapture.Core.Video;
using FranCaptureStudio.Capture;
using FranCaptureStudio.Interop;
using FranCaptureStudio.Services;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using SD = System.Drawing;
using SD2 = System.Drawing.Drawing2D;
using SDI = System.Drawing.Imaging;

namespace FranCaptureStudio.Recording;

public sealed class RecordingOptions
{
    public int Fps { get; init; } = 30;
    public string Quality { get; init; } = "high";
    public int? ScaleHeight { get; init; }
    public string Format { get; init; } = "mp4";      // mp4 | webm | gif
    public bool Cursor { get; init; } = true;
    public bool HighlightClicks { get; init; } = true;
    public bool ShowKeys { get; init; }
    public bool Mic { get; init; }
    public bool SystemAudio { get; init; }
    public string MicDeviceId { get; init; } = "";
    public bool HardwareAcceleration { get; init; } = true;
    public int GifFps { get; init; } = 12;
    public int GifMaxWidth { get; init; } = 800;
    public IntPtr FollowWindow { get; init; }
}

public sealed class SessionManifest
{
    public string Format { get; set; } = "mp4";
    public DateTime Started { get; set; }
    public int GifFps { get; set; } = 12;
    public int GifMaxWidth { get; set; } = 800;
    public int ProcessId { get; set; }
}

/// <summary>Selección del codificador: prueba real de cada candidato (hardware primero si se permite) y la recuerda.</summary>
public static class EncoderSelector
{
    static readonly Dictionary<string, VideoEncoder> Cache = [];

    public static async Task<VideoEncoder> GetAsync(bool hardwareAllowed, bool webm)
    {
        string key = (webm ? "webm" : "h264") + (hardwareAllowed ? "-hw" : "-sw");
        lock (Cache) if (Cache.TryGetValue(key, out var cached)) return cached;
        var candidates = webm ? VideoEncoder.WebmCandidates : VideoEncoder.H264Candidates(hardwareAllowed);
        foreach (var enc in candidates)
        {
            try
            {
                var r = await FfmpegRunner.RunAsync(Ffmpeg.ProbeEncoderArgs(enc));
                if (r.ExitCode == 0)
                {
                    Log.Info($"Codificador seleccionado: {enc.FfmpegName}");
                    lock (Cache) Cache[key] = enc;
                    return enc;
                }
            }
            catch (Exception ex) { Log.Warn($"Prueba de {enc.FfmpegName}: {ex.Message}"); }
        }
        throw new InvalidOperationException(webm
            ? "No hay ningún codificador WebM (VP9/VP8) disponible en este equipo."
            : "No hay ningún codificador de video disponible en este equipo.");
    }
}

/// <summary>Pista de audio WASAPI escrita en WAV y alineada con el reloj de la grabación (rellena silencios y respeta pausas).</summary>
public sealed class AudioTrack : IDisposable
{
    readonly WasapiCapture _capture;
    readonly WaveFileWriter _writer;
    readonly Func<double> _clock;
    readonly object _gate = new();
    long _written;
    bool _stopped;
    public string Path { get; }
    public volatile bool Paused;
    public bool Failed { get; private set; }

    AudioTrack(WasapiCapture capture, string path, Func<double> clockSeconds)
    {
        _capture = capture; Path = path; _clock = clockSeconds;
        _writer = new WaveFileWriter(path, capture.WaveFormat);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => { if (e.Exception != null) { Failed = true; Log.Warn("Audio detenido: " + e.Exception.Message); } };
    }

    public static AudioTrack? TryCreateMic(string deviceId, string path, Func<double> clock)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            MMDevice? dev = null;
            if (!string.IsNullOrEmpty(deviceId)) try { dev = en.GetDevice(deviceId); } catch { }
            dev ??= en.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console) ? en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console) : null;
            if (dev == null || dev.State != DeviceState.Active) return null;
            return new AudioTrack(new WasapiCapture(dev), path, clock);
        }
        catch (Exception ex) { Log.Warn("Micrófono no disponible: " + ex.Message); return null; }
    }

    public static AudioTrack? TryCreateLoopback(string path, Func<double> clock)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
            return new AudioTrack(new WasapiLoopbackCapture(), path, clock);
        }
        catch (Exception ex) { Log.Warn("Audio del sistema no disponible: " + ex.Message); return null; }
    }

    public static List<(string Id, string Name)> Microphones()
    {
        var list = new List<(string, string)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) list.Add((d.ID, d.FriendlyName));
        }
        catch (Exception ex) { Log.Warn("Enumerar micrófonos: " + ex.Message); }
        return list;
    }

    public void Start() => _capture.StartRecording();

    long Expected()
    {
        var f = _capture.WaveFormat;
        long bytes = (long)(_clock() * f.AverageBytesPerSecond);
        return bytes - bytes % f.BlockAlign;
    }

    void Pad(long upTo)
    {
        long gap = upTo - _written;
        if (gap <= 0) return;
        var zeros = new byte[Math.Min(gap, 1 << 16)];
        while (gap > 0)
        {
            int n = (int)Math.Min(gap, zeros.Length);
            _writer.Write(zeros, 0, n);
            _written += n; gap -= n;
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (_stopped || Paused || e.BytesRecorded <= 0) return;
            var f = _capture.WaveFormat;
            long expectedEnd = Expected();
            long tolerance = f.AverageBytesPerSecond / 8;
            // El bucle de retorno no entrega datos durante el silencio: rellenar para no perder sincronía.
            long padTo = expectedEnd - e.BytesRecorded;
            padTo -= padTo % f.BlockAlign;
            if (padTo - _written > tolerance) Pad(padTo);
            // Nunca adelantarse al reloj más de medio segundo (p. ej. datos acumulados tras una pausa).
            if (_written + e.BytesRecorded > expectedEnd + f.AverageBytesPerSecond / 2) return;
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _written += e.BytesRecorded;
        }
    }

    public void Stop()
    {
        try { _capture.StopRecording(); } catch { }
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            try { Pad(Expected()); _writer.Flush(); } catch { }
            try { _writer.Dispose(); } catch { }
        }
    }

    public void Dispose()
    {
        Stop();
        try { _capture.Dispose(); } catch { }
    }
}

/// <summary>Teclas a mostrar en tutoriales. Solo atajos (con Ctrl/Alt/Win) y teclas especiales: nunca texto tecleado.</summary>
public sealed class KeyDisplay : IDisposable
{
    IntPtr _hook;
    Native.LowLevelProc? _proc;
    string _text = "";
    long _until;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = Callback;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Warn("No se pudo instalar el visor de teclas.");
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN))
        {
            var k = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var label = Describe((int)k.vkCode);
            if (label != null) { _text = label; _until = Environment.TickCount64 + 1400; }
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Devuelve el texto a mostrar o null si la tecla no debe mostrarse (caracteres normales).</summary>
    public static string? Describe(int vk, bool? ctrl = null, bool? alt = null, bool? shift = null, bool? win = null)
    {
        if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5) return null;
        bool c = ctrl ?? Down(Native.VK_CONTROL), a = alt ?? Down(Native.VK_MENU), s = shift ?? Down(Native.VK_SHIFT), w = win ?? (Down(Native.VK_LWIN) || Down(Native.VK_RWIN));
        bool special = vk is (>= 0x70 and <= 0x87) or 0x1B or 0x0D or 0x09 or 0x2E or 0x2C or 0x08 or (>= 0x21 and <= 0x28);
        if (!(c || a || w) && !special) return null; // texto normal: no se muestra ni se guarda
        var parts = new List<string>();
        if (c) parts.Add("Ctrl");
        if (a) parts.Add("Alt");
        if (s) parts.Add("Shift");
        if (w) parts.Add("Win");
        parts.Add(FranCapture.Core.Hotkeys.HotkeyGesture.KeyName(vk));
        return string.Join(" + ", parts);
    }

    public string? Current => Environment.TickCount64 < _until ? _text : null;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        _proc = null;
    }
}

/// <summary>
/// Sesión de grabación: un hilo captura fotogramas a ritmo constante (según el reloj activo) y los envía a FFmpeg
/// por stdin; el audio se graba aparte en WAV. El video temporal es Matroska, recuperable si el proceso se interrumpe.
/// </summary>
public sealed class RecordingSession
{
    public SD.Rectangle Region { get; private set; }
    public RecordingOptions Options { get; }
    public string TempDir { get; }
    public string TempVideo => Path.Combine(TempDir, "video.mkv");
    public VideoEncoder? Encoder { get; private set; }
    public bool MicActive => _mic != null;
    public bool SystemAudioActive => _sys != null;
    public bool IsPaused => _paused;
    public TimeSpan Elapsed => _clock.Elapsed;
    public long FramesWritten => Interlocked.Read(ref _frames);
    public string? Error { get; private set; }
    public event Action<string>? Failed;

    readonly Stopwatch _clock = new();
    Process? _ffmpeg;
    Thread? _thread;
    volatile bool _stop, _paused;
    long _frames;
    AudioTrack? _mic, _sys;
    KeyDisplay? _keys;
    Task<string>? _stderr;

    public RecordingSession(SD.Rectangle region, RecordingOptions options)
    {
        var r = SD.Rectangle.Intersect(region, ScreenCapture.VirtualBounds);
        r.Width &= ~1; r.Height &= ~1; // H.264 4:2:0 exige dimensiones pares
        if (r.Width < 16 || r.Height < 16) throw new ArgumentException("La región a grabar es demasiado pequeña.");
        Region = r; Options = options;
        TempDir = Path.Combine(AppPaths.RecordingTempDir, DateTime.Now.ToString("yyyyMMdd_HHmmss_") + Guid.NewGuid().ToString("N")[..6]);
    }

    public async Task StartAsync()
    {
        if (!FfmpegRunner.IsAvailable) throw new FileNotFoundException(FfmpegRunner.MissingMessage);
        bool webm = Options.Format == "webm";
        Encoder = await EncoderSelector.GetAsync(Options.HardwareAcceleration, webm);
        Directory.CreateDirectory(TempDir);
        File.WriteAllText(Path.Combine(TempDir, "session.json"), JsonSerializer.Serialize(new SessionManifest
        {
            Format = Options.Format, Started = DateTime.Now, GifFps = Options.GifFps, GifMaxWidth = Options.GifMaxWidth, ProcessId = Environment.ProcessId,
        }));
        int fps = Options.Format == "gif" ? Math.Min(Options.Fps, 30) : Options.Fps;
        var args = Ffmpeg.RecordArgs(Region.Width, Region.Height, fps, Encoder, Options.Quality, Options.ScaleHeight, TempVideo);
        _ffmpeg = Process.Start(FfmpegRunner.StartInfo(args, redirectInput: true))!;
        _stderr = _ffmpeg.StandardError.ReadToEndAsync();
        _ = _ffmpeg.StandardOutput.ReadToEndAsync();
        try { _ffmpeg.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }

        double Clock() => _clock.Elapsed.TotalSeconds;
        if (Options.Format != "gif")
        {
            if (Options.Mic) _mic = AudioTrack.TryCreateMic(Options.MicDeviceId, Path.Combine(TempDir, "mic.wav"), Clock);
            if (Options.SystemAudio) _sys = AudioTrack.TryCreateLoopback(Path.Combine(TempDir, "system.wav"), Clock);
        }
        if (Options.ShowKeys) { _keys = new KeyDisplay(); _keys.Install(); }

        _clock.Start();
        try { _mic?.Start(); } catch (Exception ex) { Log.Warn("Micrófono: " + ex.Message); _mic?.Dispose(); _mic = null; }
        try { _sys?.Start(); } catch (Exception ex) { Log.Warn("Audio del sistema: " + ex.Message); _sys?.Dispose(); _sys = null; }
        _thread = new Thread(() => CaptureLoop(fps)) { IsBackground = true, Name = "FCS-Grabación", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        Log.Info($"Grabación iniciada {Region.Width}x{Region.Height}@{fps} con {Encoder.FfmpegName}");
    }

    public void Pause()
    {
        if (_paused) return;
        _paused = true;
        _clock.Stop();
        if (_mic != null) _mic.Paused = true;
        if (_sys != null) _sys.Paused = true;
    }

    public void Resume()
    {
        if (!_paused) return;
        if (_mic != null) _mic.Paused = false;
        if (_sys != null) _sys.Paused = false;
        _clock.Start();
        _paused = false;
    }

    void CaptureLoop(int fps)
    {
        using var bmp = new SD.Bitmap(Region.Width, Region.Height, SDI.PixelFormat.Format32bppRgb);
        using var g = SD.Graphics.FromImage(bmp);
        var input = _ffmpeg!.StandardInput.BaseStream;
        var rect = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
        var buffer = new byte[bmp.Width * bmp.Height * 4];
        long clickUntil = 0; SD.Point clickAt = default;
        double scale = Native.ScaleAt(Region.X + Region.Width / 2, Region.Y + Region.Height / 2);
        try
        {
            while (!_stop)
            {
                if (_paused) { Thread.Sleep(15); continue; }
                long target = (long)(_clock.Elapsed.TotalSeconds * fps);
                if (_frames > target) { Thread.Sleep(1); continue; }

                var origin = Region.Location;
                if (Options.FollowWindow != IntPtr.Zero && Native.IsWindow(Options.FollowWindow) && Native.TryGetWindowBounds(Options.FollowWindow, out var wr))
                {
                    var vb = ScreenCapture.VirtualBounds;
                    origin = new SD.Point(Math.Clamp(wr.Left, vb.Left, Math.Max(vb.Left, vb.Right - Region.Width)), Math.Clamp(wr.Top, vb.Top, Math.Max(vb.Top, vb.Bottom - Region.Height)));
                }
                try { g.CopyFromScreen(origin.X, origin.Y, 0, 0, Region.Size, SD.CopyPixelOperation.SourceCopy); }
                catch (Exception ex)
                {
                    // P. ej. escritorio bloqueado o cambio de monitores: se repite el último fotograma y se sigue.
                    Log.Warn("Fotograma no capturado: " + ex.Message);
                    Thread.Sleep(50);
                }

                if (Options.HighlightClicks)
                {
                    bool down = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0 || (Native.GetAsyncKeyState(Native.VK_RBUTTON) & 0x8000) != 0;
                    if (down && Native.GetCursorPos(out var cp)) { clickAt = new SD.Point(cp.X - origin.X, cp.Y - origin.Y); clickUntil = Environment.TickCount64 + 280; }
                    long left = clickUntil - Environment.TickCount64;
                    if (left > 0)
                    {
                        g.SmoothingMode = SD2.SmoothingMode.AntiAlias;
                        float rad = (float)(20 * scale);
                        int alpha = (int)Math.Clamp(left * 150 / 280, 30, 150);
                        using var br = new SD.SolidBrush(SD.Color.FromArgb(alpha, 255, 214, 10));
                        using var pen = new SD.Pen(SD.Color.FromArgb(Math.Min(255, alpha + 80), 255, 190, 0), (float)(2 * scale));
                        g.FillEllipse(br, clickAt.X - rad, clickAt.Y - rad, rad * 2, rad * 2);
                        g.DrawEllipse(pen, clickAt.X - rad, clickAt.Y - rad, rad * 2, rad * 2);
                        g.SmoothingMode = SD2.SmoothingMode.None;
                    }
                }
                if (Options.Cursor) ScreenCapture.DrawCursor(g, origin);
                if (_keys?.Current is { } keyText) DrawKeys(g, keyText, bmp.Width, bmp.Height, scale);

                var data = bmp.LockBits(rect, SDI.ImageLockMode.ReadOnly, SDI.PixelFormat.Format32bppRgb);
                try
                {
                    if (data.Stride == bmp.Width * 4) System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
                    else for (int y = 0; y < bmp.Height; y++) System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, buffer, y * bmp.Width * 4, bmp.Width * 4);
                }
                finally { bmp.UnlockBits(data); }

                // Si la captura va retrasada, se repite el fotograma para mantener la duración real (sin desfase de audio).
                long behind = Math.Clamp(target - _frames, 0, fps * 2);
                for (long i = 0; i <= behind && !_stop; i++)
                {
                    input.Write(buffer, 0, buffer.Length);
                    Interlocked.Increment(ref _frames);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            if (!_stop)
            {
                Error = "El codificador de video se detuvo inesperadamente.";
                Log.Error("Grabación interrumpida", ex);
                _stop = true;
                Failed?.Invoke(Error);
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error("Bucle de captura", ex);
            _stop = true;
            Failed?.Invoke(Error);
        }
    }

    static void DrawKeys(SD.Graphics g, string text, int w, int h, double scale)
    {
        using var font = new SD.Font("Segoe UI", (float)(20 * scale), SD.FontStyle.Bold, SD.GraphicsUnit.Pixel);
        var size = g.MeasureString(text, font);
        float bw = size.Width + (float)(28 * scale), bh = size.Height + (float)(14 * scale);
        float x = (w - bw) / 2, y = h - bh - (float)(36 * scale);
        if (y < 0 || bw > w) return;
        g.SmoothingMode = SD2.SmoothingMode.AntiAlias;
        using var path = new SD2.GraphicsPath();
        float d = (float)(14 * scale);
        path.AddArc(x, y, d, d, 180, 90); path.AddArc(x + bw - d, y, d, d, 270, 90);
        path.AddArc(x + bw - d, y + bh - d, d, d, 0, 90); path.AddArc(x, y + bh - d, d, d, 90, 90);
        path.CloseFigure();
        using var bg = new SD.SolidBrush(SD.Color.FromArgb(215, 18, 20, 25));
        g.FillPath(bg, path);
        g.DrawString(text, font, SD.Brushes.White, x + (float)(14 * scale), y + (float)(7 * scale));
        g.SmoothingMode = SD2.SmoothingMode.None;
    }

    /// <summary>Detiene la captura y deja el video temporal cerrado correctamente. No genera el archivo final.</summary>
    public async Task StopCaptureAsync()
    {
        _stop = true;
        _clock.Stop();
        _keys?.Dispose(); _keys = null;
        await Task.Run(() => _thread?.Join(5000));
        _mic?.Stop(); _sys?.Stop();
        if (_ffmpeg != null)
        {
            try { _ffmpeg.StandardInput.BaseStream.Flush(); _ffmpeg.StandardInput.Close(); } catch { }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await _ffmpeg.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { _ffmpeg.Kill(true); } catch { } }
            if (_ffmpeg.HasExited && _ffmpeg.ExitCode != 0 && _stderr != null)
                Log.Warn("FFmpeg (grabación): " + (await _stderr).Trim());
            _ffmpeg.Dispose(); _ffmpeg = null;
        }
    }

    /// <summary>Genera el archivo final (mezcla audio y video, o convierte a GIF) y elimina los temporales.</summary>
    public async Task<string> FinalizeAsync(string outputPath)
    {
        var result = await FinalizeTempAsync(TempDir, outputPath, Options.Format, Options.GifFps, Options.GifMaxWidth);
        _mic?.Dispose(); _sys?.Dispose();
        return result;
    }

    public void Discard()
    {
        _mic?.Dispose(); _sys?.Dispose();
        try { Directory.Delete(TempDir, true); } catch { }
    }

    /// <summary>Convierte una carpeta temporal de sesión (también una recuperada tras un cierre inesperado) en el archivo final.</summary>
    public static async Task<string> FinalizeTempAsync(string tempDir, string outputPath, string format, int gifFps, int gifMaxWidth)
    {
        var video = Path.Combine(tempDir, "video.mkv");
        if (!File.Exists(video) || new FileInfo(video).Length < 1024) throw new IOException("La grabación no contiene video.");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        ProcessResult r;
        if (format == "gif") r = await FfmpegRunner.RunAsync(Ffmpeg.GifArgs(video, outputPath, gifFps, gifMaxWidth));
        else
        {
            var audio = new[] { "mic.wav", "system.wav" }.Select(f => Path.Combine(tempDir, f)).Where(f => File.Exists(f) && new FileInfo(f).Length > 4096).ToList();
            r = await FfmpegRunner.RunAsync(Ffmpeg.MuxArgs(video, audio, outputPath));
            if (r.ExitCode != 0 && audio.Count > 0)
            {
                // Si una pista de audio quedó dañada, entregar al menos el video.
                Log.Warn("Mezcla con audio fallida, se reintenta sin audio: " + r.Error.Trim());
                r = await FfmpegRunner.RunAsync(Ffmpeg.MuxArgs(video, [], outputPath));
            }
        }
        if (r.ExitCode != 0 || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new IOException("No se pudo generar el archivo final. Los temporales se conservan en: " + tempDir + "\n" + r.Error.Trim());
        try { Directory.Delete(tempDir, true); } catch { }
        return outputPath;
    }

    /// <summary>Sesiones temporales que quedaron sin finalizar (de un proceso que ya no existe).</summary>
    public static List<(string Dir, SessionManifest Manifest)> FindOrphans()
    {
        var list = new List<(string, SessionManifest)>();
        try
        {
            foreach (var dir in Directory.GetDirectories(AppPaths.RecordingTempDir))
            {
                var video = Path.Combine(dir, "video.mkv");
                if (!File.Exists(video)) { try { if (Directory.GetCreationTime(dir) < DateTime.Now.AddDays(-1)) Directory.Delete(dir, true); } catch { } continue; }
                var m = new SessionManifest { Started = Directory.GetCreationTime(dir) };
                try { m = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(Path.Combine(dir, "session.json"))) ?? m; } catch { }
                bool alive = false;
                try { if (m.ProcessId != 0) { using var p = Process.GetProcessById(m.ProcessId); alive = !p.HasExited && p.ProcessName.Contains("FranCapture", StringComparison.OrdinalIgnoreCase); } } catch { }
                if (m.ProcessId == Environment.ProcessId) alive = true;
                if (!alive) list.Add((dir, m));
            }
        }
        catch (Exception ex) { Log.Warn("Buscar grabaciones interrumpidas: " + ex.Message); }
        return list;
    }
}

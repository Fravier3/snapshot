using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using FranCapture.Core.Diagnostics;

namespace FranCaptureStudio.Services;

public static class AppPaths
{
    public const string AppName = "FranCaptureStudio";
    /// <summary>La variable FCS_DATA_DIR permite aislar los datos (se usa en las pruebas automatizadas).</summary>
    static readonly string? Override = Environment.GetEnvironmentVariable("FCS_DATA_DIR");

    public static string DataDir => Ensure(Override != null ? Path.Combine(Override, "Roaming") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName));
    public static string LocalDir => Ensure(Override != null ? Path.Combine(Override, "Local") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LibraryDb => Path.Combine(DataDir, "library.db");
    public static string ModelsDir => Ensure(Path.Combine(DataDir, "models"));
    public static string ThumbsDir => Ensure(Path.Combine(LocalDir, "thumbs"));
    public static string RecoveryDir => Ensure(Path.Combine(LocalDir, "recovery"));
    public static string RecordingTempDir => Ensure(Path.Combine(LocalDir, "recordings"));
    public static string TempDir => Ensure(Path.Combine(LocalDir, "temp"));
    public static string LogFile => Path.Combine(LocalDir, "logs", "app.log");
    public static string AppDir => AppContext.BaseDirectory;

    public static string DefaultCapturesFolder => Override != null ? Path.Combine(Override, "Capturas") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Fran Capture Studio");
    public static string DefaultVideosFolder => Override != null ? Path.Combine(Override, "Videos") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Fran Capture Studio");
    public static string DefaultGifsFolder => Path.Combine(DefaultCapturesFolder, "GIF");

    static string Ensure(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    public static void ClearTemp()
    {
        foreach (var d in new[] { TempDir, ThumbsDir })
            try { foreach (var f in Directory.GetFiles(d)) try { File.Delete(f); } catch { } } catch { }
    }
}

public sealed record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>Ejecución del FFmpeg incluido (compilación LGPL, ejecutable independiente).</summary>
public static class FfmpegRunner
{
    static string? _path;
    public static string? ExePath
    {
        get
        {
            if (_path != null && File.Exists(_path)) return _path;
            var candidates = new List<string>
            {
                Path.Combine(AppPaths.AppDir, "ffmpeg", "ffmpeg.exe"),
                Path.Combine(AppPaths.AppDir, "ffmpeg.exe"),
            };
            var env = Environment.GetEnvironmentVariable("FCS_FFMPEG");
            if (!string.IsNullOrEmpty(env)) candidates.Insert(0, env);
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                try { candidates.Add(Path.Combine(dir.Trim(), "ffmpeg.exe")); } catch { }
            _path = candidates.FirstOrDefault(File.Exists);
            return _path;
        }
    }

    public static bool IsAvailable => ExePath != null;
    public const string MissingMessage = "No se encontró el componente FFmpeg (ffmpeg\\ffmpeg.exe). Reinstala Fran Capture Studio para restaurarlo.";

    public static ProcessStartInfo StartInfo(string args, bool redirectInput = false) => new()
    {
        FileName = ExePath ?? throw new FileNotFoundException(MissingMessage),
        Arguments = args,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        RedirectStandardInput = redirectInput,
        StandardErrorEncoding = Encoding.UTF8,
        StandardOutputEncoding = Encoding.UTF8,
    };

    public static ProcessResult Run(string args, int timeoutMs = 120000)
    {
        using var p = Process.Start(StartInfo(args))!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return new ProcessResult(-1, "", "Tiempo de espera agotado"); }
        return new ProcessResult(p.ExitCode, outp.Result, err.Result);
    }

    public static async Task<ProcessResult> RunAsync(string args, CancellationToken ct = default, Action<string>? onOutputLine = null)
    {
        using var p = Process.Start(StartInfo(args))!;
        var errTask = p.StandardError.ReadToEndAsync(CancellationToken.None);
        var sb = new StringBuilder();
        var outTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(CancellationToken.None)) != null)
            {
                sb.AppendLine(line);
                try { onOutputLine?.Invoke(line); } catch { }
            }
        }, CancellationToken.None);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        await outTask;
        return new ProcessResult(p.ExitCode, sb.ToString(), await errTask);
    }

    public static async Task<FranCapture.Core.Video.MediaInfo> ProbeAsync(string file)
    {
        var r = await RunAsync($"-hide_banner -i {FranCapture.Core.Video.Ffmpeg.Q(file)}");
        return FranCapture.Core.Video.Ffmpeg.ParseMediaInfo(r.Error);
    }
}

public static class ClipboardService
{
    public static bool SetImage(BitmapSource img)
    {
        var data = new DataObject();
        data.SetImage(img);
        // PNG adicional para conservar transparencia en aplicaciones que lo admiten
        var png = new MemoryStream(Img.Encode(img, "png"));
        data.SetData("PNG", png);
        return Try(() => Clipboard.SetDataObject(data, true));
    }

    public static bool SetText(string text) => Try(() => Clipboard.SetDataObject(text ?? "", true));

    public static bool SetFiles(IEnumerable<string> files)
    {
        var col = new System.Collections.Specialized.StringCollection();
        col.AddRange(files.ToArray());
        return Try(() => Clipboard.SetFileDropList(col));
    }

    public static BitmapSource? GetImage()
    {
        try { return Clipboard.ContainsImage() ? Img.Normalize(Clipboard.GetImage()) : null; } catch { return null; }
    }

    static bool Try(Action a)
    {
        for (int i = 0; i < 8; i++)
        {
            try { a(); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(40); } // portapapeles ocupado por otra aplicación
            catch (Exception ex) { Log.Error("Portapapeles", ex); return false; }
        }
        return false;
    }
}

/// <summary>Garantiza una sola instancia y reenvía los argumentos a la instancia en ejecución.</summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    readonly string _pipeName;
    public bool IsFirst { get; }
    CancellationTokenSource? _cts;
    public event Action<string[]>? ArgumentsReceived;

    public SingleInstance(string id)
    {
        // Hash estable (FNV-1a): string.GetHashCode cambia en cada proceso y rompería la detección de la primera instancia.
        string suffix = "";
        if (Environment.GetEnvironmentVariable("FCS_DATA_DIR") is { Length: > 0 } d)
        {
            uint h = 2166136261;
            foreach (char c in d.ToUpperInvariant()) h = (h ^ c) * 16777619;
            suffix = "-" + h;
        }
        _pipeName = id + "-" + Environment.UserName + suffix;
        _mutex = new Mutex(true, "Local\\" + _pipeName, out bool created);
        IsFirst = created;
    }

    public void StartListening()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(ct);
                    var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    ArgumentsReceived?.Invoke(args);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Error("Instancia única", ex); await Task.Delay(200, CancellationToken.None); }
            }
        }, ct);
    }

    public bool SendToFirst(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            var payload = Encoding.UTF8.GetBytes(string.Join("\n", args.Length == 0 ? ["--show"] : args));
            client.Write(payload, 0, payload.Length);
            return true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        if (IsFirst) try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}

public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "FranCaptureStudio";

    public static bool IsEnabled()
    {
        try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(ValueName) is string; } catch { return false; }
    }

    public static void Set(bool enabled, bool minimized)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\"{(minimized ? " --minimized" : "")}");
            else k.DeleteValue(ValueName, false);
        }
        catch (Exception ex) { Log.Error("Inicio con Windows", ex); }
    }
}

public static class Shell
{
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch (Exception ex) { Log.Error("Abrir URL", ex); }
    }

    public static void OpenFile(string path)
    {
        try { if (File.Exists(path) || Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { Log.Error("Abrir archivo", ex); }
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex) { Log.Error("Explorador", ex); }
    }

    /// <summary>Envía el archivo a la Papelera de reciclaje (recuperable).</summary>
    public static void Recycle(string path)
    {
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }
}

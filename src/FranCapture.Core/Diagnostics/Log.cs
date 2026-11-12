using FranCapture.Core.Text;

namespace FranCapture.Core.Diagnostics;

/// <summary>
/// Registro local de diagnóstico. Solo guarda eventos técnicos (errores, inicio/fin de operaciones);
/// nunca contenido de capturas, texto de OCR ni pulsaciones. Se puede desactivar y borrar.
/// </summary>
public static class Log
{
    static readonly object Gate = new();
    public static string? FilePath { get; private set; }
    public static bool Enabled { get; set; } = true;
    const long MaxBytes = 1_000_000;

    public static void Init(string filePath, bool enabled)
    {
        FilePath = filePath; Enabled = enabled;
        try { Directory.CreateDirectory(Path.GetDirectoryName(filePath)!); } catch { }
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    static void Write(string level, string msg)
    {
        if (!Enabled || FilePath == null) return;
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(FilePath, FilePath + ".1", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {SensitiveData.ScrubText(msg, Environment.UserName)}{Environment.NewLine}");
            }
        }
        catch { /* el registro nunca debe romper la aplicación */ }
    }

    public static string ReadAll()
    {
        try { lock (Gate) return FilePath != null && File.Exists(FilePath) ? File.ReadAllText(FilePath) : ""; } catch { return ""; }
    }

    public static void Clear()
    {
        try { lock (Gate) { if (FilePath != null) { File.Delete(FilePath); File.Delete(FilePath + ".1"); } } } catch { }
    }
}

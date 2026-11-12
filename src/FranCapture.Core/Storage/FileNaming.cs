using System.Text;

namespace FranCapture.Core.Storage;

public static class FileNaming
{
    static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Expande la plantilla de nombre. Marcadores: {fecha} {hora} {yyyy} {MM} {dd} {HH} {mm} {ss} {n} (contador) {tipo}.
    /// </summary>
    public static string Expand(string template, DateTime now, int counter = 1, string kind = "Captura", bool forceDate = false)
    {
        var t = string.IsNullOrWhiteSpace(template) ? "Captura_{fecha}_{hora}" : template;
        bool hasDate = t.Contains("{fecha}") || t.Contains("{yyyy}") || t.Contains("{dd}");
        var s = t.Replace("{fecha}", now.ToString("yyyy-MM-dd"))
                 .Replace("{hora}", now.ToString("HH-mm-ss"))
                 .Replace("{yyyy}", now.ToString("yyyy")).Replace("{MM}", now.ToString("MM")).Replace("{dd}", now.ToString("dd"))
                 .Replace("{HH}", now.ToString("HH")).Replace("{mm}", now.ToString("mm")).Replace("{ss}", now.ToString("ss"))
                 .Replace("{n}", counter.ToString("000"))
                 .Replace("{tipo}", kind);
        if (forceDate && !hasDate) s += "_" + now.ToString("yyyy-MM-dd");
        return Sanitize(s);
    }

    /// <summary>Elimina caracteres no permitidos por Windows en nombres de archivo.</summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(c < 32 || "<>:\"/\\|?*".Contains(c) ? '_' : c);
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length == 0) s = "Captura";
        if (s.Length > 120) s = s[..120];
        if (ReservedNames.Contains(s)) s = "_" + s;
        return s;
    }

    /// <summary>Devuelve una ruta que no existe aún: nombre.ext, nombre (2).ext, …</summary>
    public static string UniquePath(string folder, string baseName, string extension, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        extension = extension.TrimStart('.');
        var path = Path.Combine(folder, $"{baseName}.{extension}");
        for (int i = 2; exists(path); i++) path = Path.Combine(folder, $"{baseName} ({i}).{extension}");
        return path;
    }

    /// <summary>Nombre de archivo de una ruta, aceptando separadores de Windows y de Unix.</summary>
    public static string NameOf(string path)
    {
        int i = path.LastIndexOfAny(['\\', '/']);
        return i < 0 ? path : path[(i + 1)..];
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes; int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.#} {units[u]}";
    }
}

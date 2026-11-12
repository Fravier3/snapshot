using System.Text.Json;
using System.Text.Json.Serialization;
using FranCapture.Core.Automation;
using FranCapture.Core.Hotkeys;

namespace FranCapture.Core.Settings;

public sealed class RegionRect
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>Configuración persistente del usuario. Todo se guarda localmente en JSON.</summary>
public sealed class AppSettings
{
    public const int CurrentSchema = 1;
    public int SchemaVersion { get; set; } = CurrentSchema;

    // General
    public string Language { get; set; } = "es";          // es | en
    public string Theme { get; set; } = "dark";           // dark | light
    public bool FirstRunCompleted { get; set; }
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public string CloseBehavior { get; set; } = "tray";   // tray | exit
    public bool Notifications { get; set; } = true;
    public bool Sounds { get; set; } = true;

    // Atajos: acción -> gesto ("F9", "Ctrl+Shift+F10"); cadena vacía = sin atajo
    public Dictionary<string, string> Hotkeys { get; set; } = HotkeyDefaults.Create();

    // Almacenamiento
    public string CapturesFolder { get; set; } = "";
    public string VideosFolder { get; set; } = "";
    public string GifsFolder { get; set; } = "";
    public string FileNameTemplate { get; set; } = "Captura_{fecha}_{hora}";
    public string ImageFormat { get; set; } = "png";      // png | jpg | webp | bmp
    public int JpegQuality { get; set; } = 90;
    public bool AskWhereToSave { get; set; }
    public bool StripMetadata { get; set; } = true;

    // Captura
    public int CaptureDelaySeconds { get; set; }
    public bool IncludeCursor { get; set; }
    public int PeriodicIntervalSeconds { get; set; } = 10;
    public RegionRect? LastRegion { get; set; }
    public string WatermarkText { get; set; } = "Fran Capture Studio";

    // Grabación
    public int RecordFps { get; set; } = 30;              // 30 | 60
    public string RecordQuality { get; set; } = "high";   // low | medium | high
    public string RecordResolution { get; set; } = "original"; // original | 1080 | 720
    public string VideoFormat { get; set; } = "mp4";      // mp4 | webm
    public bool RecordCursor { get; set; } = true;
    public bool HighlightClicks { get; set; } = true;
    public bool ShowKeys { get; set; }
    public bool RecordMic { get; set; }
    public bool RecordSystemAudio { get; set; } = true;
    public string MicDeviceId { get; set; } = "";
    public bool ShowWebcam { get; set; }
    public string WebcamDevice { get; set; } = "";
    public int Countdown { get; set; } = 3;               // 0 | 3 | 5 | 10
    public bool HardwareAcceleration { get; set; } = true;
    public int GifFps { get; set; } = 12;
    public int GifMaxWidth { get; set; } = 800;

    // OCR
    public string OcrLanguage { get; set; } = "auto";     // auto | es | en | etiqueta BCP-47
    public string SearchEngine { get; set; } = "google";  // google | bing | duckduckgo

    // Privacidad
    public bool DiagnosticsLogging { get; set; } = true;  // registro local; nunca se envía

    public List<AutomationRule> Automations { get; set; } = AutomationRule.Defaults();

    public AppSettings Clone() => SettingsSerializer.Deserialize(SettingsSerializer.Serialize(this));
}

public static class SettingsSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(AppSettings s) => JsonSerializer.Serialize(s, Options);

    public static AppSettings Deserialize(string json)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? throw new InvalidDataException("Archivo vacío");
        SettingsValidator.Normalize(s);
        return s;
    }
}

public static class SettingsValidator
{
    static readonly string[] Themes = ["dark", "light"];
    static readonly string[] Languages = ["es", "en"];
    static readonly string[] Formats = ["png", "jpg", "webp", "bmp"];
    static readonly string[] Qualities = ["low", "medium", "high"];
    static readonly string[] Resolutions = ["original", "1080", "720"];
    static readonly string[] VideoFormats = ["mp4", "webm"];
    static readonly string[] CloseBehaviors = ["tray", "exit"];
    static readonly string[] Engines = ["google", "bing", "duckduckgo"];

    static string Pick(string? v, string[] allowed, string fallback)
    {
        v = (v ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(allowed, v) >= 0 ? v : fallback;
    }

    /// <summary>Corrige valores fuera de rango para que la aplicación siempre arranque con una configuración válida.</summary>
    public static void Normalize(AppSettings s)
    {
        var d = new AppSettings();
        s.SchemaVersion = AppSettings.CurrentSchema;
        s.Language = Pick(s.Language, Languages, d.Language);
        s.Theme = Pick(s.Theme, Themes, d.Theme);
        s.CloseBehavior = Pick(s.CloseBehavior, CloseBehaviors, d.CloseBehavior);
        s.ImageFormat = Pick(string.Equals(s.ImageFormat?.Trim(), "jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : s.ImageFormat, Formats, d.ImageFormat);
        s.RecordQuality = Pick(s.RecordQuality, Qualities, d.RecordQuality);
        s.RecordResolution = Pick(s.RecordResolution, Resolutions, d.RecordResolution);
        s.VideoFormat = Pick(s.VideoFormat, VideoFormats, d.VideoFormat);
        s.SearchEngine = Pick(s.SearchEngine, Engines, d.SearchEngine);
        s.JpegQuality = Math.Clamp(s.JpegQuality, 10, 100);
        s.RecordFps = s.RecordFps >= 45 ? 60 : 30;
        s.Countdown = s.Countdown is 0 or 3 or 5 or 10 ? s.Countdown : 3;
        s.CaptureDelaySeconds = s.CaptureDelaySeconds is 0 or 3 or 5 or 10 ? s.CaptureDelaySeconds : 0;
        s.PeriodicIntervalSeconds = Math.Clamp(s.PeriodicIntervalSeconds, 1, 3600);
        s.GifFps = Math.Clamp(s.GifFps, 2, 30);
        s.GifMaxWidth = Math.Clamp(s.GifMaxWidth, 120, 3840);
        s.FileNameTemplate = string.IsNullOrWhiteSpace(s.FileNameTemplate) ? d.FileNameTemplate : s.FileNameTemplate.Trim();
        s.OcrLanguage = string.IsNullOrWhiteSpace(s.OcrLanguage) ? "auto" : s.OcrLanguage.Trim();
        s.WatermarkText ??= "";
        s.CapturesFolder ??= ""; s.VideosFolder ??= ""; s.GifsFolder ??= "";
        s.MicDeviceId ??= ""; s.WebcamDevice ??= "";
        if (s.LastRegion is { } r && (r.Width < 1 || r.Height < 1)) s.LastRegion = null;

        // Atajos: solo acciones conocidas y gestos válidos; sin duplicados.
        var clean = new Dictionary<string, string>();
        var used = new HashSet<string>();
        foreach (var action in Enum.GetNames<HotkeyAction>())
        {
            string g = "";
            if (s.Hotkeys != null && s.Hotkeys.TryGetValue(action, out var raw) && HotkeyGesture.TryParse(raw, out var gesture)
                && gesture.Validate() == HotkeyProblem.None && used.Add(gesture.ToString()))
                g = gesture.ToString();
            clean[action] = g;
        }
        s.Hotkeys = clean;

        s.Automations = (s.Automations ?? AutomationRule.Defaults())
            .Where(a => a != null && Enum.IsDefined(a.Trigger) && Enum.IsDefined(a.Action) && AutomationRule.IsCompatible(a.Trigger, a.Action))
            .ToList();
        foreach (var a in s.Automations) if (string.IsNullOrWhiteSpace(a.Id)) a.Id = Guid.NewGuid().ToString("N");
    }

    /// <summary>Valida un archivo de configuración importado ANTES de aplicarlo. Devuelve null si es válido o el motivo del rechazo.</summary>
    public static string? ValidateImport(string json, out AppSettings? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(json)) return "El archivo está vacío.";
        if (json.Length > 2_000_000) return "El archivo es demasiado grande para ser una configuración válida.";
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "El archivo no contiene un objeto de configuración.";
            if (!doc.RootElement.EnumerateObject().Any(p => p.Name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase)))
                return "El archivo no parece una configuración de Fran Capture Studio (falta SchemaVersion).";
            var ver = doc.RootElement.EnumerateObject().First(p => p.Name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase)).Value;
            if (ver.ValueKind != JsonValueKind.Number || ver.GetInt32() > AppSettings.CurrentSchema)
                return "La configuración fue creada con una versión más reciente y no puede importarse.";
            result = SettingsSerializer.Deserialize(json);
            return null;
        }
        catch (JsonException ex) { return "El archivo no es JSON válido: " + ex.Message; }
        catch (Exception ex) { return "No se pudo leer la configuración: " + ex.Message; }
    }
}

/// <summary>Lectura y escritura atómica del archivo de configuración.</summary>
public sealed class SettingsStore(string path)
{
    public string Path { get; } = path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(Path)) return SettingsSerializer.Deserialize(File.ReadAllText(Path));
        }
        catch
        {
            // Configuración dañada: se conserva una copia y se arranca con valores por defecto.
            try { File.Copy(Path, Path + ".dañado", true); } catch { }
        }
        var s = new AppSettings();
        SettingsValidator.Normalize(s);
        return s;
    }

    public void Save(AppSettings s)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, SettingsSerializer.Serialize(s));
        File.Move(tmp, Path, true);
    }
}

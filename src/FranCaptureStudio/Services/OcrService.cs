using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media.Imaging;
using FranCapture.Core.Diagnostics;
using WinOcr = global::Windows.Media.Ocr;
using WinImaging = global::Windows.Graphics.Imaging;
using WinGlobalization = global::Windows.Globalization;

namespace FranCaptureStudio.Services;

public sealed record OcrWord(string Text, Rect Bounds);
public sealed record OcrLine(string Text, List<OcrWord> Words);
public sealed record OcrResult(string Text, List<OcrLine> Lines, string Language, string? Warning);

/// <summary>
/// OCR local con el motor integrado de Windows (Windows.Media.Ocr). Las imágenes nunca salen del equipo.
/// Los idiomas disponibles dependen de los paquetes de idioma instalados en Windows.
/// </summary>
public static class OcrService
{
    public static List<(string Tag, string Name)> AvailableLanguages()
    {
        try { return WinOcr.OcrEngine.AvailableRecognizerLanguages.Select(l => (l.LanguageTag, l.DisplayName)).ToList(); }
        catch (Exception ex) { Log.Warn("Idiomas OCR: " + ex.Message); return []; }
    }

    static (WinOcr.OcrEngine? Engine, string? Warning) CreateEngine(string language)
    {
        var available = WinOcr.OcrEngine.AvailableRecognizerLanguages.ToList();
        if (available.Count == 0) return (null, null);
        if (string.IsNullOrEmpty(language) || language == "auto")
        {
            var e = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (e != null) return (e, null);
            return (WinOcr.OcrEngine.TryCreateFromLanguage(available[0]), null);
        }
        var match = available.FirstOrDefault(l => l.LanguageTag.Equals(language, StringComparison.OrdinalIgnoreCase))
                    ?? available.FirstOrDefault(l => l.LanguageTag.StartsWith(language.Split('-')[0], StringComparison.OrdinalIgnoreCase));
        if (match != null) return (WinOcr.OcrEngine.TryCreateFromLanguage(match), null);
        var fallback = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages() ?? WinOcr.OcrEngine.TryCreateFromLanguage(available[0]);
        return (fallback, $"El paquete de OCR para «{language}» no está instalado en Windows; se usó {fallback?.RecognizerLanguage.DisplayName}. Puedes instalarlo en Configuración > Hora e idioma > Idioma y región.");
    }

    public static async Task<OcrResult> RecognizeAsync(BitmapSource image, string language = "auto")
    {
        var (engine, warning) = CreateEngine(language);
        if (engine == null)
            throw new InvalidOperationException("Windows no tiene instalado ningún idioma con reconocimiento de texto (OCR). Instala un paquete de idioma en Configuración > Hora e idioma > Idioma y región.");

        // Ampliar imágenes pequeñas mejora notablemente la precisión con texto de pantalla.
        double scale = 1;
        int maxDim = Math.Max(image.PixelWidth, image.PixelHeight);
        uint limit = WinOcr.OcrEngine.MaxImageDimension;
        if (maxDim < 1800) scale = 2;
        if (maxDim * scale > limit) scale = (double)limit / maxDim;
        var src = Math.Abs(scale - 1) > 0.01 ? Img.Resize(image, (int)(image.PixelWidth * scale), (int)(image.PixelHeight * scale)) : image;
        if (src.PixelWidth < 40 || src.PixelHeight < 40)
        {
            // el motor exige un tamaño mínimo: añadir margen blanco
            src = Img.AddBorder(src, 24, System.Windows.Media.Colors.White);
        }
        int pad = src.PixelWidth - (int)(image.PixelWidth * scale) > 0 && Math.Min(image.PixelWidth, image.PixelHeight) * scale < 40 ? 24 : 0;

        var pixels = Img.Pixels(src);
        using var bitmap = WinImaging.SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), WinImaging.BitmapPixelFormat.Bgra8, src.PixelWidth, src.PixelHeight, WinImaging.BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap);

        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.Select(w => new OcrWord(w.Text,
                new Rect((w.BoundingRect.X - pad) / scale, (w.BoundingRect.Y - pad) / scale, w.BoundingRect.Width / scale, w.BoundingRect.Height / scale))).ToList();
            lines.Add(new OcrLine(line.Text, words));
        }
        return new OcrResult(string.Join(Environment.NewLine, lines.Select(l => l.Text)), lines, engine.RecognizerLanguage.DisplayName, warning);
    }
}

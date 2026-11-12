using System.Net;
using System.Text;

namespace FranCapture.Core.Qa;

public sealed class BugReport
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";     // qué ocurrió
    public string Expected { get; set; } = "";        // qué se esperaba
    public List<string> Steps { get; set; } = [];     // pasos para reproducir
    public string Severity { get; set; } = "";        // opcional
    public List<string> Attachments { get; set; } = []; // rutas de capturas
    public bool IncludeTechnicalInfo { get; set; }
    public string TechnicalInfo { get; set; } = "";   // editable por el usuario antes de exportar
    public DateTime Created { get; set; } = DateTime.Now;

    IEnumerable<string> CleanSteps => Steps.Select(s => s.Trim()).Where(s => s.Length > 0);

    public static List<string> ParseSteps(string multiline) =>
        (multiline ?? "").Replace("\r\n", "\n").Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"^(\d+[\.\)]|[-*•])\s*", ""))
            .Where(l => l.Length > 0).ToList();

    /// <summary>Descripción estructurada lista para pegar en GitHub Issues o un chat.</summary>
    public string ToMarkdown(bool includeImageLinks = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## " + (string.IsNullOrWhiteSpace(Title) ? "Reporte de error" : Title.Trim()));
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(Severity)) { sb.AppendLine("**Severidad:** " + Severity.Trim()); sb.AppendLine(); }
        sb.AppendLine("### Qué ocurrió");
        sb.AppendLine(string.IsNullOrWhiteSpace(Description) ? "_Sin descripción._" : Description.Trim());
        sb.AppendLine();
        sb.AppendLine("### Qué se esperaba");
        sb.AppendLine(string.IsNullOrWhiteSpace(Expected) ? "_Sin especificar._" : Expected.Trim());
        sb.AppendLine();
        sb.AppendLine("### Pasos para reproducir");
        int n = 1;
        foreach (var s in CleanSteps) sb.AppendLine($"{n++}. {s}");
        if (n == 1) sb.AppendLine("_Sin pasos indicados._");
        sb.AppendLine();
        if (Attachments.Count > 0)
        {
            sb.AppendLine("### Capturas");
            foreach (var a in Attachments)
                sb.AppendLine(includeImageLinks ? $"![{Path.GetFileNameWithoutExtension(Storage.FileNaming.NameOf(a))}]({Storage.FileNaming.NameOf(a)})" : $"- {Storage.FileNaming.NameOf(a)}");
            sb.AppendLine();
        }
        if (IncludeTechnicalInfo && !string.IsNullOrWhiteSpace(TechnicalInfo))
        {
            sb.AppendLine("### Información técnica");
            sb.AppendLine("```");
            sb.AppendLine(TechnicalInfo.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
        }
        sb.AppendLine($"_Reporte generado el {Created:yyyy-MM-dd HH:mm} con Fran Capture Studio._");
        return sb.ToString();
    }

    /// <summary>HTML autocontenido; las imágenes se incrustan en base64 con el lector suministrado.</summary>
    public string ToHtml(Func<string, byte[]?>? readImage = null)
    {
        string E(string s) => WebUtility.HtmlEncode(s ?? "").Replace("\n", "<br>");
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"es\"><head><meta charset=\"utf-8\"><title>").Append(E(Title)).Append("</title><style>")
          .Append("body{font-family:'Segoe UI',Arial,sans-serif;max-width:900px;margin:32px auto;padding:0 20px;color:#1b1f24;line-height:1.5}")
          .Append("h1{font-size:24px;border-bottom:2px solid #0a84ff;padding-bottom:8px}h2{font-size:16px;margin-top:26px;color:#0a5fb8}")
          .Append("img{max-width:100%;border:1px solid #d0d7de;border-radius:6px;margin:8px 0}pre{background:#f3f5f7;padding:12px;border-radius:6px;white-space:pre-wrap}")
          .Append(".meta{color:#667;font-size:12px}</style></head><body>");
        sb.Append("<h1>").Append(E(string.IsNullOrWhiteSpace(Title) ? "Reporte de error" : Title)).Append("</h1>");
        if (!string.IsNullOrWhiteSpace(Severity)) sb.Append("<p><b>Severidad:</b> ").Append(E(Severity)).Append("</p>");
        sb.Append("<h2>Qué ocurrió</h2><p>").Append(E(Description)).Append("</p>");
        sb.Append("<h2>Qué se esperaba</h2><p>").Append(E(Expected)).Append("</p>");
        sb.Append("<h2>Pasos para reproducir</h2><ol>");
        foreach (var s in CleanSteps) sb.Append("<li>").Append(E(s)).Append("</li>");
        sb.Append("</ol>");
        if (Attachments.Count > 0)
        {
            sb.Append("<h2>Capturas</h2>");
            foreach (var a in Attachments)
            {
                var data = readImage?.Invoke(a);
                if (data == null) { sb.Append("<p>").Append(E(Storage.FileNaming.NameOf(a))).Append("</p>"); continue; }
                string mime = Path.GetExtension(a).ToLowerInvariant() switch { ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/png" };
                sb.Append("<img alt=\"").Append(E(Storage.FileNaming.NameOf(a))).Append("\" src=\"data:").Append(mime).Append(";base64,").Append(Convert.ToBase64String(data)).Append("\">");
            }
        }
        if (IncludeTechnicalInfo && !string.IsNullOrWhiteSpace(TechnicalInfo))
            sb.Append("<h2>Información técnica</h2><pre>").Append(WebUtility.HtmlEncode(TechnicalInfo.Trim())).Append("</pre>");
        sb.Append("<p class=\"meta\">Reporte generado el ").Append(Created.ToString("yyyy-MM-dd HH:mm")).Append(" con Fran Capture Studio.</p></body></html>");
        return sb.ToString();
    }
}

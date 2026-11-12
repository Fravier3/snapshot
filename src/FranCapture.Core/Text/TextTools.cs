using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace FranCapture.Core.Text;

public static class LinkDetector
{
    static readonly Regex Rx = new(@"(?i)\b((?:https?://|www\.)[^\s<>""'()]+|[a-z0-9][a-z0-9\-]*(?:\.[a-z0-9\-]+)*\.(?:com|org|net|es|mx|io|dev|edu|gov|app|co|info)(?:/[^\s<>""'()]*)?)", RegexOptions.Compiled);
    static readonly Regex EmailRx = new(@"(?i)\b[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}\b", RegexOptions.Compiled);

    /// <summary>Enlaces reconocibles en el texto, normalizados con esquema.</summary>
    public static List<string> Find(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        var emails = EmailRx.Matches(text).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
        foreach (Match m in Rx.Matches(text))
        {
            if (emails.Any(e => m.Index >= e.Index && m.Index < e.End)) continue; // dominio de un correo
            var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !result.Contains(url)) result.Add(url);
        }
        return result;
    }

    public static string SearchUrl(string engine, string query)
    {
        var q = Uri.EscapeDataString(query.Trim());
        return engine switch
        {
            "bing" => "https://www.bing.com/search?q=" + q,
            "duckduckgo" => "https://duckduckgo.com/?q=" + q,
            _ => "https://www.google.com/search?q=" + q,
        };
    }
}

public enum SensitiveKind { Email, Phone, CardNumber, IpAddress, IdNumber, Secret }

public static class SensitiveData
{
    static readonly (SensitiveKind Kind, Regex Rx)[] Patterns =
    [
        (SensitiveKind.Email, new Regex(@"(?i)^[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}$", RegexOptions.Compiled)),
        (SensitiveKind.CardNumber, new Regex(@"^(?:\d[ \-]?){13,19}$", RegexOptions.Compiled)),
        (SensitiveKind.IpAddress, new Regex(@"^(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?$", RegexOptions.Compiled)),
        (SensitiveKind.Phone, new Regex(@"^\+?\(?\d[\d \-.()]{7,18}\d$", RegexOptions.Compiled)),
        (SensitiveKind.IdNumber, new Regex(@"^\d{3}-\d{2}-\d{4}$", RegexOptions.Compiled)),
        (SensitiveKind.Secret, new Regex(@"^(?:sk|pk|ghp|gho|xox[abp]|AKIA)[\-_A-Za-z0-9]{12,}$", RegexOptions.Compiled)),
    ];

    /// <summary>Clasifica una palabra (o grupo corto de palabras) reconocida por OCR.</summary>
    public static SensitiveKind? Classify(string token)
    {
        var t = token.Trim().Trim(',', ';', '(', ')', '<', '>', '"', '\'');
        if (t.Length < 6) return null;
        foreach (var (kind, rx) in Patterns)
        {
            if (!rx.IsMatch(t)) continue;
            if (kind == SensitiveKind.CardNumber && !Luhn(t)) continue;
            if (kind == SensitiveKind.IpAddress && !t.Split(':')[0].Split('.').All(p => int.TryParse(p, out var n) && n <= 255)) continue;
            if (kind == SensitiveKind.Phone && t.Count(char.IsDigit) is < 9 or > 15) continue;
            return kind;
        }
        return null;
    }

    static bool Luhn(string s)
    {
        int sum = 0; bool dbl = false; int digits = 0;
        for (int i = s.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(s[i])) continue;
            int d = s[i] - '0'; digits++;
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d; dbl = !dbl;
        }
        return digits >= 13 && sum % 10 == 0;
    }

    /// <summary>
    /// Dadas las palabras de una línea (en orden), devuelve los rangos [inicio, fin] de índices que forman datos sensibles.
    /// Prueba grupos de 1 a 4 palabras para cubrir números de tarjeta o teléfonos separados por espacios.
    /// </summary>
    public static List<(int First, int Last, SensitiveKind Kind)> FindInWords(IReadOnlyList<string> words)
    {
        var hits = new List<(int, int, SensitiveKind)>();
        int i = 0;
        while (i < words.Count)
        {
            bool found = false;
            for (int len = Math.Min(4, words.Count - i); len >= 1; len--)
            {
                var joined = string.Join(" ", words.Skip(i).Take(len));
                if (Classify(joined) is { } k)
                {
                    hits.Add((i, i + len - 1, k));
                    i += len; found = true; break;
                }
            }
            if (!found) i++;
        }
        return hits;
    }

    /// <summary>Sustituye datos privados habituales en un texto de registro (rutas de usuario, correos, IP).</summary>
    public static string ScrubText(string text, string? userName = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var s = Regex.Replace(text, @"(?i)[a-z0-9._%+\-]+@[a-z0-9.\-]+\.[a-z]{2,}", "<correo>");
        s = Regex.Replace(s, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "<ip>");
        s = Regex.Replace(s, @"(?i)([A-Z]:\\Users\\)[^\\\s""]+", "$1<usuario>");
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 3) s = s.Replace(userName, "<usuario>", StringComparison.OrdinalIgnoreCase);
        return s;
    }
}

/// <summary>Generador mínimo de documentos .docx (Office Open XML) sin dependencias.</summary>
public static class DocxWriter
{
    public static void Write(string path, string title, string body)
    {
        using var fs = File.Create(path);
        Write(fs, title, body);
    }

    public static void Write(Stream output, string title, string body)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>");
        Add(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>");
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
        if (!string.IsNullOrWhiteSpace(title))
            sb.Append("<w:p><w:r><w:rPr><w:b/><w:sz w:val=\"32\"/></w:rPr><w:t xml:space=\"preserve\">").Append(SecurityElement.Escape(title)).Append("</w:t></w:r></w:p>");
        foreach (var line in (body ?? "").Replace("\r\n", "\n").Split('\n'))
            sb.Append("<w:p><w:r><w:t xml:space=\"preserve\">").Append(SecurityElement.Escape(Clean(line))).Append("</w:t></w:r></w:p>");
        sb.Append("<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1417\" w:right=\"1417\" w:bottom=\"1417\" w:left=\"1417\"/></w:sectPr></w:body></w:document>");
        Add(zip, "word/document.xml", sb.ToString());
    }

    static string Clean(string s) => new(s.Where(c => c >= 32 || c == '\t').ToArray());

    static void Add(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}

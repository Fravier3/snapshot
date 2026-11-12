using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FranCapture.Core.Automation;
using FranCapture.Core.Diagnostics;
using FranCapture.Core.Localization;
using FranCapture.Core.Qa;
using FranCapture.Core.Storage;
using FranCapture.Core.Text;
using FranCaptureStudio.Capture;
using FranCaptureStudio.Services;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace FranCaptureStudio.Views;

/// <summary>OCR local: reconoce texto de una región, imagen o portapapeles; el resultado es editable.</summary>
public sealed class OcrPage : UserControl, IRefreshable
{
    readonly TextBox _text = new() { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 240, FontSize = 14 };
    readonly TextBlock _status = Form.Text("Captura una región o abre una imagen para extraer su texto. Todo el reconocimiento se hace en este equipo.", "Muted");
    readonly WrapPanel _links = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly Image _preview = new() { MaxHeight = 150, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
    public string ResultText => _text.Text;

    public OcrPage()
    {
        var top = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        top.Children.Add(Form.Button("Capturar región", () => _ = FromRegion(), "PrimaryButton"));
        top.Children.Add(Form.Button("Abrir imagen…", () => { if (Ui.OpenImageFile() is { } f) try { _ = RecognizeAsync(Img.Load(f)); } catch (Exception ex) { Ui.Error(ex.Message); } }));
        top.Children.Add(Form.Button("Desde el portapapeles", () => { var i = ClipboardService.GetImage(); if (i != null) _ = RecognizeAsync(i); else _status.Text = Loc.T("El portapapeles no contiene una imagen."); }));
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(Form.Button("Copiar texto", () => { if (ClipboardService.SetText(_text.Text)) _status.Text = Loc.T("Texto copiado al portapapeles."); }));
        actions.Children.Add(Form.Button("Guardar como TXT…", SaveTxt));
        actions.Children.Add(Form.Button("Exportar a documento (DOCX)…", SaveDocx));
        actions.Children.Add(Form.Button("Buscar texto seleccionado", Search));
        actions.Children.Add(Form.Button("Borrar", () => { _text.Clear(); _links.Children.Clear(); _preview.Source = null; }));
        _text.TextChanged += (_, _) => RefreshLinks();
        var linksTitle = Form.Text("Enlaces detectados", "SectionTitle"); linksTitle.Margin = new Thickness(0, 16, 0, 2);
        Content = Form.Page(top, _status, new Border { Height = 10 }, _preview, _text, actions, linksTitle, _links);
    }

    public void Refresh() { }

    async Task FromRegion()
    {
        var main = Studio.Main; bool hid = main is { IsVisible: true };
        if (hid) { main!.Hide(); await Task.Delay(250); }
        BitmapSource? img = null;
        try
        {
            using var res = OverlayForm.Show(new OverlayOptions { Purpose = OverlayPurpose.SelectOnly, AcceptLabel = "Reconocer texto" });
            if (res.Image != null) img = Img.FromGdi(res.Image);
        }
        finally { if (hid) main!.Show(); }
        if (img != null) await RecognizeAsync(img);
    }

    public async Task RecognizeAsync(BitmapSource image)
    {
        _preview.Source = image;
        _status.Text = Loc.T("Reconociendo texto…");
        try
        {
            var r = await OcrService.RecognizeAsync(image, Studio.Settings.OcrLanguage);
            _text.Text = r.Text;
            _status.Text = r.Text.Length == 0
                ? Loc.T("No se reconoció texto en la imagen.")
                : Loc.T("{0} líneas reconocidas · idioma: {1}", r.Lines.Count, r.Language) + (r.Warning != null ? "  ⚠ " + r.Warning : "");
            if (r.Text.Length > 0 && AutomationPlan.For(AutomationTrigger.Ocr, Studio.Settings.Automations).CopyText && ClipboardService.SetText(r.Text))
                _status.Text += " · " + Loc.T("copiado al portapapeles");
        }
        catch (Exception ex)
        {
            Log.Error("OCR", ex);
            _status.Text = Loc.T(ex.Message);
        }
    }

    void RefreshLinks()
    {
        _links.Children.Clear();
        var links = LinkDetector.Find(_text.Text);
        if (links.Count == 0) { _links.Children.Add(Form.Text("Ninguno.", "Muted")); return; }
        foreach (var l in links.Take(30))
        {
            var url = l;
            var b = new Button { Style = Ui.S("SecondaryButton"), Content = url.Length > 60 ? url[..60] + "…" : url, Margin = new Thickness(0, 0, 8, 8), ToolTip = Loc.T("Abrir en el navegador") };
            b.Click += (_, _) => { if (Ui.Confirm(Loc.T("¿Abrir este enlace en el navegador?") + "\n" + url)) Shell.OpenUrl(url); };
            _links.Children.Add(b);
        }
    }

    void Search()
    {
        var q = _text.SelectedText.Trim();
        if (q.Length == 0) { _status.Text = Loc.T("Selecciona primero el texto que quieres buscar."); return; }
        if (q.Length > 300) q = q[..300];
        // Acción explícita del usuario: abre el navegador con la búsqueda. Es la única vez que el texto sale de la aplicación.
        Shell.OpenUrl(LinkDetector.SearchUrl(Studio.Settings.SearchEngine, q));
    }

    void SaveTxt()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = Loc.T("Guardar texto"), Filter = "Texto|*.txt", FileName = "Texto_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt", AddExtension = true };
        if (dlg.ShowDialog() != true) return;
        try { File.WriteAllText(dlg.FileName, _text.Text, new System.Text.UTF8Encoding(true)); _status.Text = Loc.T("Guardado en") + " " + dlg.FileName; }
        catch (Exception ex) { Ui.Error(Loc.T("No se pudo guardar el archivo:") + "\n" + ex.Message); }
    }

    void SaveDocx()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = Loc.T("Exportar a documento"), Filter = "Documento de Word|*.docx", FileName = "Texto_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".docx", AddExtension = true };
        if (dlg.ShowDialog() != true) return;
        try { DocxWriter.Write(dlg.FileName, "", _text.Text); _status.Text = Loc.T("Guardado en") + " " + dlg.FileName; }
        catch (Exception ex) { Ui.Error(Loc.T("No se pudo guardar el archivo:") + "\n" + ex.Message); }
    }
}

/// <summary>Generación de PDF local con PDFsharp.</summary>
public static class BugReportPdf
{
    public static void Write(BugReport r, string path)
    {
        using var doc = new PdfDocument();
        doc.Info.Title = string.IsNullOrWhiteSpace(r.Title) ? "Reporte de error" : r.Title;
        doc.Info.Creator = "Fran Capture Studio";
        const double margin = 48;
        var h1 = new XFont("Segoe UI", 18, XFontStyleEx.Bold);
        var h2 = new XFont("Segoe UI", 12, XFontStyleEx.Bold);
        var body = new XFont("Segoe UI", 10.5, XFontStyleEx.Regular);
        var small = new XFont("Segoe UI", 8.5, XFontStyleEx.Regular);
        var mono = new XFont("Consolas", 8.5, XFontStyleEx.Regular);
        var accent = new XSolidBrush(XColor.FromArgb(10, 95, 184));
        PdfPage page = null!; XGraphics g = null!; double y = 0, width = 0;

        void NewPage()
        {
            g?.Dispose();
            page = doc.AddPage(); page.Size = PdfSharp.PageSize.A4;
            g = XGraphics.FromPdfPage(page);
            width = page.Width.Point - margin * 2; y = margin;
        }
        void Ensure(double h) { if (y + h > page.Height.Point - margin) NewPage(); }
        void Para(string text, XFont font, XBrush brush, double after = 6)
        {
            double lh = font.GetHeight() * 1.18;
            foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                var line = "";
                foreach (var word in raw.Split(' '))
                {
                    var w = word;
                    // palabras más largas que la línea: cortar
                    while (g.MeasureString(w, font).Width > width && w.Length > 8)
                    {
                        int cut = w.Length - 1;
                        while (cut > 4 && g.MeasureString(w[..cut], font).Width > width) cut--;
                        if (line.Length > 0) { Ensure(lh); g.DrawString(line, font, brush, margin, y + font.GetHeight() * 0.8); y += lh; line = ""; }
                        Ensure(lh); g.DrawString(w[..cut], font, brush, margin, y + font.GetHeight() * 0.8); y += lh;
                        w = w[cut..];
                    }
                    var test = line.Length == 0 ? w : line + " " + w;
                    if (g.MeasureString(test, font).Width > width && line.Length > 0)
                    {
                        Ensure(lh); g.DrawString(line, font, brush, margin, y + font.GetHeight() * 0.8); y += lh; line = w;
                    }
                    else line = test;
                }
                Ensure(lh); g.DrawString(line, font, brush, margin, y + font.GetHeight() * 0.8); y += lh;
            }
            y += after;
        }
        void Heading(string text) { y += 8; Ensure(40); Para(text, h2, accent, 3); }

        NewPage();
        Para(doc.Info.Title, h1, XBrushes.Black, 2);
        g.DrawLine(new XPen(XColor.FromArgb(10, 132, 255), 1.5), margin, y, margin + width, y); y += 10;
        if (!string.IsNullOrWhiteSpace(r.Severity)) Para("Severidad: " + r.Severity.Trim(), body, XBrushes.Black);
        Heading("Qué ocurrió"); Para(string.IsNullOrWhiteSpace(r.Description) ? "Sin descripción." : r.Description.Trim(), body, XBrushes.Black);
        Heading("Qué se esperaba"); Para(string.IsNullOrWhiteSpace(r.Expected) ? "Sin especificar." : r.Expected.Trim(), body, XBrushes.Black);
        Heading("Pasos para reproducir");
        int n = 1;
        foreach (var s in r.Steps.Where(s => !string.IsNullOrWhiteSpace(s))) Para($"{n++}. {s.Trim()}", body, XBrushes.Black, 2);
        if (n == 1) Para("Sin pasos indicados.", body, XBrushes.Black);
        if (r.Attachments.Count > 0)
        {
            Heading("Capturas");
            foreach (var a in r.Attachments)
            {
                if (!File.Exists(a)) { Para(FileNaming.NameOf(a) + " (archivo no encontrado)", small, XBrushes.Gray); continue; }
                try
                {
                    // Se incrusta como JPEG/PNG re-codificado: sin metadatos del archivo original.
                    var src = Img.Load(a);
                    using var ms = new MemoryStream(Img.Encode(src, src.PixelWidth * src.PixelHeight > 1_500_000 ? "jpg" : "png", 88));
                    using var img = XImage.FromStream(ms);
                    double scale = Math.Min(width / img.PixelWidth, (page.Height.Point - margin * 2 - 30) / img.PixelHeight);
                    scale = Math.Min(scale, 0.75); // 96 ppp → puntos
                    double w = img.PixelWidth * scale, h = img.PixelHeight * scale;
                    Ensure(h + 22);
                    g.DrawImage(img, margin, y, w, h);
                    g.DrawRectangle(new XPen(XColor.FromArgb(200, 205, 212), 0.5), margin, y, w, h);
                    y += h + 3;
                    Para(FileNaming.NameOf(a), small, XBrushes.Gray, 10);
                }
                catch (Exception ex) { Para($"{FileNaming.NameOf(a)} (no se pudo incrustar: {ex.Message})", small, XBrushes.Gray); }
            }
        }
        if (r.IncludeTechnicalInfo && !string.IsNullOrWhiteSpace(r.TechnicalInfo)) { Heading("Información técnica"); Para(r.TechnicalInfo.Trim(), mono, XBrushes.Black); }
        y += 6;
        Para($"Reporte generado el {r.Created:yyyy-MM-dd HH:mm} con Fran Capture Studio.", small, XBrushes.Gray);
        g.Dispose();
        doc.Save(path);
    }
}

/// <summary>Modo QA: documentar un fallo con capturas, descripción, resultado esperado y pasos; exportar o copiar.</summary>
public sealed class QaPage : UserControl, IRefreshable
{
    readonly TextBox _title = new(), _severity = new() { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBox _desc = Multi(90), _expected = Multi(70), _steps = Multi(110), _tech = Multi(110);
    readonly CheckBox _includeTech = new();
    readonly WrapPanel _attachments = new();
    readonly TextBlock _status = Form.Text("", "Muted", false);
    readonly List<string> _files = [];
    public IReadOnlyList<string> Attachments => _files;

    static TextBox Multi(double h) => new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = h, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    static UIElement Field(string label, UIElement box, string? hint = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var l = Form.Text(label, "FieldLabel"); l.Margin = new Thickness(0, 0, 0, 4); l.FontWeight = FontWeights.SemiBold;
        sp.Children.Add(l);
        if (hint != null) { var h = Form.Text(hint, "Muted"); h.Margin = new Thickness(0, 0, 0, 4); sp.Children.Add(h); }
        sp.Children.Add(box);
        return sp;
    }

    public QaPage()
    {
        Form.Tr(_includeTech, ContentProperty, "Incluir información técnica (puedes editarla y borrar datos privados antes de exportar)");
        _includeTech.Click += (_, _) => { _tech.Visibility = _includeTech.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; if (_includeTech.IsChecked == true && _tech.Text.Length == 0) _tech.Text = DefaultTechInfo(false); };
        _tech.Visibility = Visibility.Collapsed; _tech.FontFamily = new FontFamily("Consolas");
        var capButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        capButtons.Children.Add(Form.Button("Capturar el error", () => _ = CaptureAsync(), "PrimaryButton"));
        capButtons.Children.Add(Form.Button("Adjuntar imágenes…", AttachFiles));
        capButtons.Children.Add(Form.Button("Pegar del portapapeles", PasteImage));
        var techButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        techButtons.Children.Add(Form.Button("Añadir registro de la aplicación (saneado)", () => { _includeTech.IsChecked = true; _tech.Visibility = Visibility.Visible; _tech.Text = DefaultTechInfo(true); }));
        var export = new WrapPanel();
        export.Children.Add(Form.Button("Copiar para GitHub / chat", CopyMarkdown, "PrimaryButton"));
        export.Children.Add(Form.Button("Exportar PDF…", () => Export("pdf")));
        export.Children.Add(Form.Button("Exportar Markdown…", () => Export("md")));
        export.Children.Add(Form.Button("Exportar HTML…", () => Export("html")));
        export.Children.Add(Form.Button("Nuevo reporte", Clear));
        Content = Form.Page(
            Form.Card("1 · Capturas",
                Form.Text("Captura el fallo, márcalo con flechas en el editor y adjunta todas las capturas que necesites.", "Muted"), new Border { Height = 8 }, capButtons, _attachments),
            Form.Card("2 · Descripción",
                Field("Título", _title), Field("Qué ocurrió", _desc), Field("Qué esperabas que sucediera", _expected),
                Field("Pasos para reproducirlo", _steps, "Un paso por línea; se numeran automáticamente."), Field("Severidad (opcional)", _severity)),
            Form.Card("3 · Información técnica (opcional)", _includeTech, new Border { Height = 6 }, _tech, techButtons),
            Form.Card("4 · Exportar", export, new Border { Height = 8 }, _status));
        RefreshAttachments();
    }

    public void Refresh() { }

    static string DefaultTechInfo(bool withLog)
    {
        var vb = ScreenCapture.VirtualBounds;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Sistema: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Fran Capture Studio: {Studio.Version}");
        sb.AppendLine($"Pantallas: {ScreenCapture.Monitors().Count} · escritorio {vb.Width}×{vb.Height}");
        sb.AppendLine($"Fecha: {DateTime.Now:yyyy-MM-dd HH:mm}");
        if (withLog)
        {
            var log = Log.ReadAll();
            var lines = log.Split('\n');
            sb.AppendLine().AppendLine("--- Registro de la aplicación (últimas líneas, saneado) ---");
            sb.Append(SensitiveData.ScrubText(string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 40))), Environment.UserName));
        }
        return sb.ToString();
    }

    public BugReport BuildReport() => new()
    {
        Title = _title.Text, Description = _desc.Text, Expected = _expected.Text, Steps = BugReport.ParseSteps(_steps.Text), Severity = _severity.Text,
        Attachments = [.. _files], IncludeTechnicalInfo = _includeTech.IsChecked == true, TechnicalInfo = _tech.Text, Created = DateTime.Now,
    };

    public void AddAttachment(string path)
    {
        if (!File.Exists(path) || _files.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        _files.Add(path);
        RefreshAttachments();
    }

    void RefreshAttachments()
    {
        _attachments.Children.Clear();
        if (_files.Count == 0) { _attachments.Children.Add(Form.Text("Sin capturas adjuntas.", "Muted")); return; }
        foreach (var f in _files.ToList())
        {
            var file = f;
            var img = new Image { Width = 150, Height = 96, Stretch = Stretch.UniformToFill, Source = Img.LoadThumbnail(file, 300) };
            var clip = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = img };
            var edit = new Button { Style = Ui.S("IconButton"), Content = "", ToolTip = Loc.T("Anotar en el editor (guarda para actualizar)") };
            edit.Click += (_, _) => { try { Editor.EditorWindow.Open(Img.Load(file), file); } catch (Exception ex) { Ui.Error(ex.Message); } };
            var del = new Button { Style = Ui.S("IconButton"), Content = "", ToolTip = Loc.T("Quitar del reporte (no borra el archivo)") };
            del.Click += (_, _) => { _files.Remove(file); RefreshAttachments(); };
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = { edit, del } };
            var card = new Border { Style = Ui.S("Card"), Padding = new Thickness(8), Margin = new Thickness(0, 0, 10, 10), Child = new StackPanel { Children = { clip, row } }, ToolTip = file };
            _attachments.Children.Add(card);
        }
    }

    async Task CaptureAsync()
    {
        var r = await Studio.Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.Region, FromUi = true, Silent = true });
        if (r.Image == null) return;
        var path = r.SavedPath;
        if (path == null) return;
        AddAttachment(path);
        Studio.ShowMain("qa");
        // abrir el editor para marcar el fallo con flechas y anotaciones
        Editor.EditorWindow.Open(r.Image, path).Closed += (_, _) => RefreshAttachments();
    }

    void AttachFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Img.OpenFilter, Multiselect = true, Title = Loc.T("Adjuntar imágenes") };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames) AddAttachment(f);
    }

    void PasteImage()
    {
        var img = ClipboardService.GetImage();
        if (img == null) { _status.Text = Loc.T("El portapapeles no contiene una imagen."); return; }
        var path = Studio.Storage.NextImagePath();
        try { Studio.Storage.SaveImage(img, path); Studio.Library.AddFile(path); AddAttachment(path); }
        catch (Exception ex) { Ui.Error(ex.Message); }
    }

    void CopyMarkdown()
    {
        bool ok = ClipboardService.SetText(BuildReport().ToMarkdown());
        _status.Text = Loc.T(ok ? "Descripción copiada. Pégala en GitHub Issues o en tu chat; arrastra allí las capturas para adjuntarlas." : "No se pudo acceder al portapapeles.");
    }

    void Export(string kind)
    {
        var report = BuildReport();
        var baseName = FileNaming.Sanitize("Reporte_" + (string.IsNullOrWhiteSpace(report.Title) ? DateTime.Now.ToString("yyyy-MM-dd_HH-mm") : report.Title.Trim()));
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Exportar reporte"), AddExtension = true, FileName = baseName + "." + kind,
            Filter = kind switch { "pdf" => "PDF|*.pdf", "md" => "Markdown|*.md", _ => "HTML|*.html" },
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ExportTo(report, dlg.FileName);
            _status.Text = Loc.T("Reporte exportado:") + " " + dlg.FileName;
            Studio.Notify(Loc.T("Reporte exportado"), Path.GetFileName(dlg.FileName), () => Shell.RevealInExplorer(dlg.FileName));
        }
        catch (Exception ex) { Log.Error("Exportar reporte", ex); Ui.Error(Loc.T("No se pudo exportar el reporte:") + "\n" + ex.Message); }
    }

    public static void ExportTo(BugReport report, string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".pdf": BugReportPdf.Write(report, path); break;
            case ".md":
            {
                // Markdown con las capturas copiadas junto al archivo para que los enlaces funcionen
                var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
                foreach (var a in report.Attachments.Where(File.Exists))
                {
                    var target = Path.Combine(dir, Path.GetFileName(a));
                    if (!File.Exists(target)) File.Copy(a, target);
                }
                File.WriteAllText(path, report.ToMarkdown(includeImageLinks: true), new System.Text.UTF8Encoding(false));
                break;
            }
            default:
                File.WriteAllText(path, report.ToHtml(a => File.Exists(a) ? Img.Encode(Img.Load(a), "png") : null), new System.Text.UTF8Encoding(false));
                break;
        }
    }

    void Clear()
    {
        if ((_title.Text.Length > 0 || _desc.Text.Length > 0 || _files.Count > 0) && !Ui.Confirm("¿Vaciar el reporte actual? Las capturas guardadas no se eliminan.")) return;
        _title.Clear(); _desc.Clear(); _expected.Clear(); _steps.Clear(); _severity.Clear(); _tech.Clear();
        _includeTech.IsChecked = false; _tech.Visibility = Visibility.Collapsed; _files.Clear(); _status.Text = "";
        RefreshAttachments();
    }

    internal void TestFill(string title, string desc, string expected, string steps) { _title.Text = title; _desc.Text = desc; _expected.Text = expected; _steps.Text = steps; }
}

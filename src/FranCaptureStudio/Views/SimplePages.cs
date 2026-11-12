using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FranCapture.Core.Automation;
using FranCapture.Core.Diagnostics;
using FranCapture.Core.Hotkeys;
using FranCapture.Core.Library;
using FranCapture.Core.Localization;
using FranCapture.Core.Settings;
using FranCapture.Core.Storage;
using FranCaptureStudio.Capture;
using FranCaptureStudio.Recording;
using FranCaptureStudio.Services;
using FranCaptureStudio.Tools;
using SD = System.Drawing;

namespace FranCaptureStudio.Views;

/// <summary>Página que se reconstruye a partir de la configuración actual cada vez que se muestra o cambia.</summary>
public abstract class BuiltPage : UserControl, IRefreshable
{
    protected static AppSettings S => Studio.Settings;
    protected BuiltPage()
    {
        Studio.SettingsChanged += () => { if (IsVisible && !_saving) Refresh(); };
        Loaded += (_, _) => { if (Content == null) Refresh(); };
    }
    bool _saving;
    /// <summary>Guarda un cambio hecho desde esta página sin reconstruirla.</summary>
    protected void Save(Action? after = null)
    {
        _saving = true;
        try { Studio.RaiseSettingsChanged(); after?.Invoke(); } finally { _saving = false; }
    }
    public void Refresh() => Content = Build();
    protected abstract UIElement Build();
    protected static string Hotkey(HotkeyAction a) => S.Hotkeys.TryGetValue(a.ToString(), out var g) && g.Length > 0 ? g : "";
}

public sealed class DashboardPage : BuiltPage
{
    public DashboardPage()
    {
        Studio.Library.Changed += () => { if (IsVisible) Refresh(); };
    }

    static Task Cap(CaptureMode m) => Studio.Capture.CaptureAsync(new CaptureRequest { Mode = m, FromUi = true });

    protected override UIElement Build()
    {
        string Sub(string text, HotkeyAction a) => Hotkey(a) is { Length: > 0 } h ? $"{Loc.T(text)} · {h}" : Loc.T(text);
        var tiles = new WrapPanel();
        tiles.Children.Add(Form.Tile("", "Capturar región", Sub("Selecciona un área", HotkeyAction.CaptureRegion), () => _ = Cap(CaptureMode.Region), 208, false));
        tiles.Children.Add(Form.Tile("", "Pantalla completa", Sub("Todas las pantallas", HotkeyAction.CaptureFullScreen), () => _ = Cap(CaptureMode.FullScreen), 208, false));
        tiles.Children.Add(Form.Tile("", "Capturar ventana", Sub("Elige con un clic", HotkeyAction.CaptureActiveWindow), () => _ = Cap(CaptureMode.PickWindow), 208, false));
        tiles.Children.Add(Form.Tile("", "Grabar video", Sub("Pantalla, ventana o región", HotkeyAction.StartRecording), () => Studio.ShowMain("recorder"), 208, false));
        tiles.Children.Add(Form.Tile("", "Grabar GIF", Sub("Región animada", HotkeyAction.StartGifRecording), () => _ = Studio.Recorder.StartWithRegionPickerAsync(true), 208, false));
        tiles.Children.Add(Form.Tile("", "Abrir editor", Sub("Imagen o portapapeles", HotkeyAction.OpenEditor), Editor.EditorWindow.OpenEmpty, 208, false));
        tiles.Children.Add(Form.Tile("", "OCR", Sub("Extraer texto", HotkeyAction.OcrRegion), () => _ = Studio.OcrRegionAsync(), 208, false));
        tiles.Children.Add(Form.Tile("", "Herramientas", "Color, regla, QR, comparar…", () => Studio.ShowMain("tools")));

        // Capturas recientes
        var recent = new WrapPanel();
        var items = Studio.Library.Query(new LibraryQuery { Limit = 8 });
        foreach (var it in items)
        {
            var img = new Image { Width = 150, Height = 96, Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var item = it;
            Task.Run(() => Studio.Library.GetThumbnail(item, 300)).ContinueWith(t => Dispatcher.BeginInvoke(() => img.Source = t.Result), TaskScheduler.Default);
            var name = Form.Text(it.Name, "Muted", false); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.Width = 150; name.Margin = new Thickness(0, 6, 0, 0);
            var clip = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = img, Background = Ui.B("Surface2Brush") };
            var b = new Button { Style = Ui.S("Tile"), Padding = new Thickness(8), Content = new StackPanel { Children = { clip, name } }, ToolTip = it.Path };
            b.Click += (_, _) => LibraryPage.OpenItem(item);
            recent.Children.Add(b);
        }
        UIElement recentBody = items.Count > 0 ? recent : Form.Text("Aún no hay capturas. Pulsa «Capturar región» o tu atajo para empezar.", "Muted");

        // Espacio usado
        long used = Studio.Storage.UsedBytes();
        int count = Studio.Library.Repo.Count();
        var bar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 8, 0, 6) };
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Studio.Storage.CapturesFolder)!);
            bar.Value = Math.Clamp((double)used / Math.Max(1, used + drive.AvailableFreeSpace), 0.004, 1);
            bar.ToolTip = Loc.T("Libre en el disco: {0}", FileNaming.FormatSize(drive.AvailableFreeSpace));
        }
        catch { }
        var storage = Form.Card("Espacio usado por las capturas",
            Form.Text(Loc.T("{0} en las carpetas de capturas y videos · {1} elementos en la biblioteca", FileNaming.FormatSize(used), count), "Body", false),
            bar,
            Form.Buttons(Form.Button("Abrir carpeta de capturas", () => Shell.OpenFile(Studio.Storage.CapturesFolder)), Form.Button("Almacenamiento", () => Studio.ShowMain("storage")), Form.Button("Configuración", () => Studio.ShowMain("settings"))));

        var header = new DockPanel { Margin = new Thickness(0, 6, 0, 10) };
        var more = Form.Button("Ver biblioteca", () => Studio.ShowMain("library"));
        DockPanel.SetDock(more, Dock.Right); header.Children.Add(more);
        var title = Form.Text("Capturas recientes", "SectionTitle"); title.Margin = new Thickness(0); title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);

        var quick = Form.Text("Acceso rápido", "SectionTitle");
        return Form.Page(quick, tiles, header, recentBody, new Border { Height = 18 }, storage);
    }
}

public sealed class RecorderPage : BuiltPage
{
    public RecorderPage()
    {
        Studio.Recorder.StateChanged += () => { if (IsVisible) Refresh(); };
    }

    protected override UIElement Build()
    {
        var rec = Studio.Recorder;
        UIElement top;
        if (rec.IsActive)
        {
            top = Form.Card("Grabación en curso",
                Form.Text("Usa la barra flotante o los atajos para pausar, reanudar o detener. La barra no aparece en el video.", "Muted"),
                Form.Buttons(Form.Button("Pausar / reanudar", rec.TogglePause), Form.Button("Detener y guardar", () => _ = rec.StopAsync(), "PrimaryButton")));
        }
        else
        {
            var mons = ScreenCapture.Monitors();
            var tiles = new WrapPanel();
            foreach (var m in mons)
            {
                var mon = m;
                tiles.Children.Add(Form.Tile("", mons.Count == 1 ? "Pantalla completa" : $"Monitor {m.Index + 1}", $"{m.Bounds.Width} × {m.Bounds.Height}{(m.Primary && mons.Count > 1 ? " · " + Loc.T("principal") : "")}",
                    () => _ = rec.StartAsync(new RecordingTarget { MonitorIndex = mon.Index }), 190, false));
            }
            if (mons.Count > 1) tiles.Children.Add(Form.Tile("", "Todas las pantallas", "Escritorio completo", () => _ = rec.StartAsync(new RecordingTarget { AllScreens = true }), 190));
            tiles.Children.Add(Form.Tile("", "Ventana", "Elige con un clic", () => _ = HideThen(rec.StartWithWindowPickerAsync), 190));
            tiles.Children.Add(Form.Tile("", "Región", "Selecciona un área", () => _ = HideThen(() => rec.StartWithRegionPickerAsync(false)), 190));
            tiles.Children.Add(Form.Tile("", "GIF de una región", "Animación sin audio", () => _ = HideThen(() => rec.StartWithRegionPickerAsync(true)), 190));
            var st = new StackPanel();
            st.Children.Add(Form.Text("¿Qué quieres grabar?", "SectionTitle"));
            st.Children.Add(tiles);
            if (!FfmpegRunner.IsAvailable) st.Children.Add(Form.Text(FfmpegRunner.MissingMessage, "Body"));
            top = st;
        }

        var mics = new List<(string, string)> { ("", Loc.T("Predeterminado del sistema")) };
        mics.AddRange(AudioTrack.Microphones());
        var camCombo = new ComboBox { Width = 260 };
        camCombo.Items.Add(new ComboBoxItem { Content = string.IsNullOrEmpty(S.WebcamDevice) ? Loc.T("Buscando cámaras…") : S.WebcamDevice, Tag = S.WebcamDevice, IsSelected = true });
        _ = WebcamWindow.ListDevicesAsync().ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            camCombo.Items.Clear();
            if (t.Result.Count == 0) { camCombo.Items.Add(new ComboBoxItem { Content = Loc.T("No se detectó ninguna cámara"), Tag = "", IsSelected = true }); return; }
            foreach (var d in t.Result) camCombo.Items.Add(new ComboBoxItem { Content = d, Tag = d, IsSelected = d == S.WebcamDevice });
            if (camCombo.SelectedItem == null) { camCombo.SelectedIndex = 0; S.WebcamDevice = t.Result[0]; Studio.SaveSettings(); }
            camCombo.SelectionChanged += (_, _) => { if (camCombo.SelectedItem is ComboBoxItem { Tag: string d }) { S.WebcamDevice = d; Save(); } };
        }), TaskScheduler.Default);

        var video = Form.Card("Video",
            Form.Row("Fotogramas por segundo", Form.Combo([(30, "30 FPS"), (60, "60 FPS")], S.RecordFps, v => { S.RecordFps = v; Save(); }, 200, false), "60 FPS exige más CPU; si el equipo no alcanza, se repiten fotogramas para mantener la duración real."),
            Form.Row("Calidad", Form.Combo([("high", "Alta"), ("medium", "Media"), ("low", "Baja")], S.RecordQuality, v => { S.RecordQuality = v; Save(); })),
            Form.Row("Resolución", Form.Combo([("original", "Original"), ("1080", "1080p"), ("720", "720p")], S.RecordResolution, v => { S.RecordResolution = v; Save(); })),
            Form.Row("Formato", Form.Combo([("mp4", "MP4 (H.264)"), ("webm", "WebM (VP9)")], S.VideoFormat, v => { S.VideoFormat = v; Save(); }, 200, false)),
            Form.Row("Cuenta regresiva", Form.Combo([(0, "Sin cuenta regresiva"), (3, "3 segundos"), (5, "5 segundos"), (10, "10 segundos")], S.Countdown, v => { S.Countdown = v; Save(); })),
            Form.ToggleRow("Aceleración por hardware", S.HardwareAcceleration, v => { S.HardwareAcceleration = v; Save(); }, "Usa NVENC, Quick Sync o AMF si están disponibles; si no, codifica por software."));
        var pointer = Form.Card("Cursor y teclado",
            Form.ToggleRow("Mostrar el cursor", S.RecordCursor, v => { S.RecordCursor = v; Save(); }),
            Form.ToggleRow("Resaltar clics", S.HighlightClicks, v => { S.HighlightClicks = v; Save(); }),
            Form.ToggleRow("Mostrar teclas pulsadas", S.ShowKeys, v => { S.ShowKeys = v; Save(); }, "Solo se muestran atajos (con Ctrl, Alt o Win) y teclas especiales. El texto que escribes —incluidas contraseñas— nunca se muestra ni se guarda."));
        var audio = Form.Card("Audio y cámara",
            Form.ToggleRow("Grabar audio del sistema", S.RecordSystemAudio, v => { S.RecordSystemAudio = v; Save(); }),
            Form.ToggleRow("Grabar micrófono", S.RecordMic, v => { S.RecordMic = v; Save(); }),
            Form.Row("Dispositivo de entrada", Form.Combo(mics, S.MicDeviceId, v => { S.MicDeviceId = v; Save(); }, 260, false)),
            Form.ToggleRow("Mostrar cámara web", S.ShowWebcam, v => { S.ShowWebcam = v; Save(); }, "Aparece como un círculo flotante que puedes mover; queda dentro del video."),
            Form.Row("Cámara", camCombo));
        var gif = Form.Card("GIF",
            Form.Row("Fotogramas por segundo del GIF", Form.Combo([(8, "8"), (10, "10"), (12, "12"), (15, "15"), (20, "20"), (24, "24")], S.GifFps, v => { S.GifFps = v; Save(); }, 200, false)),
            Form.Row("Ancho máximo", Form.Combo([(320, "320 px"), (480, "480 px"), (640, "640 px"), (800, "800 px"), (1280, "1280 px")], S.GifMaxWidth, v => { S.GifMaxWidth = v; Save(); }, 200, false)));
        var note = Form.Text("El contenido protegido por DRM o por Windows aparece en negro: la aplicación no intenta eludir esas restricciones.", "Muted");
        return Form.Page(top, new Border { Height = 8 }, video, pointer, audio, gif, note);
    }

    static async Task HideThen(Func<Task> action)
    {
        var main = Studio.Main;
        bool hid = main is { IsVisible: true };
        if (hid) { main!.Hide(); await Task.Delay(250); }
        try { await action(); }
        finally { if (hid && !Studio.Recorder.IsActive) main!.Show(); }
    }
}

public sealed class ToolsPage : BuiltPage
{
    static readonly List<SD.Color> ColorHistory = [];
    DispatcherTimer? _periodic;
    int _periodicCount;

    static async Task<T> Hidden<T>(Func<T> f)
    {
        var main = Studio.Main; bool hid = main is { IsVisible: true };
        if (hid) { main!.Hide(); await Task.Delay(250); }
        try { return f(); } finally { if (hid) main!.Show(); }
    }

    async Task PickColor()
    {
        using var res = await Hidden(() => OverlayForm.Show(new OverlayOptions { Mode = OverlayMode.ColorPicker }));
        if (res.Action != OverlayAction.ColorPicked) return;
        ColorHistory.Remove(res.Color); ColorHistory.Insert(0, res.Color);
        if (ColorHistory.Count > 12) ColorHistory.RemoveAt(12);
        ClipboardService.SetText($"#{res.Color.R:X2}{res.Color.G:X2}{res.Color.B:X2}");
        Refresh();
    }

    void ExactRegion()
    {
        var lr = S.LastRegion;
        var txt = Ui.Prompt("Región con dimensiones exactas", "Escribe X, Y, ancho y alto en píxeles (por ejemplo: 100, 100, 1280, 720):", lr != null ? $"{lr.X}, {lr.Y}, {lr.Width}, {lr.Height}" : "0, 0, 1280, 720", Window.GetWindow(this));
        if (txt == null) return;
        var p = txt.Split([',', ';', ' ', 'x'], StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 4 || !p.All(v => int.TryParse(v, out _))) { Ui.Error("Formato no válido. Usa cuatro números: X, Y, ancho, alto."); return; }
        var v = p.Select(int.Parse).ToArray();
        if (v[2] < 1 || v[3] < 1) { Ui.Error("El ancho y el alto deben ser mayores que cero."); return; }
        _ = Studio.Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.ExactRegion, Rect = new SD.Rectangle(v[0], v[1], v[2], v[3]), FromUi = true });
    }

    void TogglePeriodic()
    {
        if (_periodic != null) { _periodic.Stop(); _periodic = null; Studio.Notify(Loc.T("Capturas periódicas detenidas"), Loc.T("{0} capturas guardadas.", _periodicCount)); Refresh(); return; }
        var txt = Ui.Prompt("Capturas periódicas", "Intervalo en segundos entre capturas de pantalla completa. Se guardan en la carpeta de capturas hasta que las detengas desde esta misma página:", S.PeriodicIntervalSeconds.ToString(), Window.GetWindow(this));
        if (txt == null || !int.TryParse(txt.Trim(), out var sec) || sec < 1 || sec > 3600) return;
        S.PeriodicIntervalSeconds = sec; Studio.SaveSettings();
        _periodicCount = 0;
        _periodic = new DispatcherTimer { Interval = TimeSpan.FromSeconds(sec) };
        _periodic.Tick += async (_, _) =>
        {
            var r = await Studio.Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.FullScreen, Silent = true });
            if (r.SavedPath != null) _periodicCount++;
            if (_periodicCount >= 2000) TogglePeriodic(); // límite de seguridad
        };
        _periodic.Start();
        Studio.Notify(Loc.T("Capturas periódicas activas"), Loc.T("Cada {0} s. Detenlas desde Herramientas.", sec));
        Refresh();
    }

    protected override UIElement Build()
    {
        var owner = Window.GetWindow(this);
        var measure = new WrapPanel();
        measure.Children.Add(Form.Tile("", "Selector de colores", "HEX y RGB de cualquier píxel", () => _ = PickColor()));
        measure.Children.Add(Form.Tile("", "Regla en pantalla", "Mide distancias en píxeles", () => _ = Hidden(() => { using var r = OverlayForm.Show(new OverlayOptions { Mode = OverlayMode.Ruler }); return 0; })));
        measure.Children.Add(Form.Tile("", "Lupa", "Amplía la zona bajo el cursor", () => new MagnifierWindow().Show()));
        measure.Children.Add(Form.Tile("", "Código QR", "Leer y generar", () => new QrWindow().Show()));

        var capture = new WrapPanel();
        capture.Children.Add(Form.Tile("", "Captura extendida", "Desplazamiento vertical", () => _ = Studio.Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.Scrolling, FromUi = true })));
        capture.Children.Add(Form.Tile("", "Captura con pasos", "Numeración automática", () => new StepsCaptureWindow().Show()));
        capture.Children.Add(Form.Tile("", "Con temporizador", "3, 5 o 10 segundos", () =>
        {
            var m = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
            foreach (var s in new[] { 3, 5, 10 }) { var mi = new MenuItem { Header = Loc.T("{0} segundos · pantalla completa", s) }; int sec = s; mi.Click += (_, _) => _ = Studio.Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.FullScreen, DelaySeconds = sec, FromUi = true }); m.Items.Add(mi); }
            foreach (var s in new[] { 3, 5, 10 }) { var mi = new MenuItem { Header = Loc.T("{0} segundos · ventana activa", s) }; int sec = s; mi.Click += (_, _) => _ = Studio.Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.ActiveWindow, DelaySeconds = sec, FromUi = true }); m.Items.Add(mi); }
            m.IsOpen = true;
        }));
        capture.Children.Add(Form.Tile("", _periodic != null ? "Detener capturas periódicas" : "Capturas periódicas", _periodic != null ? Loc.T("Activas · {0} guardadas", _periodicCount) : Loc.T("Cada N segundos"), TogglePeriodic, 208, false));
        capture.Children.Add(Form.Tile("", "Región exacta", "Dimensiones manuales", ExactRegion));
        capture.Children.Add(Form.Tile("", "Última región", "Repetir la selección anterior", () => _ = Studio.Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.LastRegion, FromUi = true })));
        var mons = ScreenCapture.Monitors();
        foreach (var m in mons)
        {
            var mon = m;
            capture.Children.Add(Form.Tile("", $"Monitor {m.Index + 1}", $"{m.Bounds.Width} × {m.Bounds.Height}", () => _ = Studio.Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.Monitor, MonitorIndex = mon.Index, FromUi = true }), 208, false));
        }
        capture.Children.Add(Form.Tile("", "Ventana activa", "La que tenga el foco", () => _ = Studio.Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.ActiveWindow, DelaySeconds = 3, FromUi = true })));

        var images = new WrapPanel();
        images.Children.Add(Form.Tile("", "Fijar imagen en pantalla", "Siempre visible", () =>
        {
            var img = ClipboardService.GetImage() ?? (Ui.OpenImageFile(owner) is { } f ? Img.Load(f) : null);
            if (img != null) PinnedImageWindow.Pin(img);
        }));
        images.Children.Add(Form.Tile("", "Comparador", "Antes y después", () => new CompareWindow().Show()));
        images.Children.Add(Form.Tile("", "Combinador", "Unir capturas", () => new CombineWindow().Show()));
        images.Children.Add(Form.Tile("", "Conversor", "PNG, JPG, WebP…", () => new ConverterWindow().Show()));
        images.Children.Add(Form.Tile("", "Eliminar fondo", "Local, sin conexión", () =>
        {
            if (Ui.OpenImageFile(owner) is not { } f) return;
            try { var r = BackgroundRemovalWindow.Run(Img.Load(f), owner); if (r != null) Editor.EditorWindow.Open(r, null); }
            catch (Exception ex) { Ui.Error(ex.Message); }
        }));

        UIElement? colors = null;
        if (ColorHistory.Count > 0)
        {
            var wp = new WrapPanel();
            foreach (var c in ColorHistory)
            {
                string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}", rgb = $"rgb({c.R}, {c.G}, {c.B})";
                var sw = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 10, 0) };
                sw.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                var bh = Form.Button(hex, () => ClipboardService.SetText(hex)); bh.Content = hex; bh.ToolTip = Loc.T("Copiar HEX");
                var br = Form.Button(rgb, () => ClipboardService.SetText(rgb)); br.Content = rgb; br.ToolTip = Loc.T("Copiar RGB");
                wp.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 8), Children = { sw, bh, br } });
            }
            colors = Form.Card("Colores recientes (clic para copiar)", wp);
        }
        return Form.Page(colors, Form.Text("Medición e inspección", "SectionTitle"), measure, Form.Text("Modos de captura", "SectionTitle"), capture, Form.Text("Imágenes", "SectionTitle"), images);
    }
}

public sealed class AutomationsPage : BuiltPage
{
    protected override UIElement Build()
    {
        var list = new StackPanel();
        foreach (var rule in S.Automations.OrderBy(r => r.Trigger).ToList())
        {
            var r = rule;
            var label = Form.Text($"{Loc.T(AutomationRule.TriggerName(r.Trigger))}  →  {Loc.T(AutomationRule.ActionName(r.Action))}", "FieldLabel", false);
            var del = new Button { Style = Ui.S("IconButton"), Content = "", ToolTip = Loc.T("Eliminar regla") };
            del.Click += (_, _) => { S.Automations.Remove(r); Save(); Refresh(); };
            var toggle = Form.Toggle(r.Enabled, v => { r.Enabled = v; Save(); });
            toggle.Margin = new Thickness(0, 0, 4, 0);
            var g = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            DockPanel.SetDock(del, Dock.Right); DockPanel.SetDock(toggle, Dock.Right);
            g.Children.Add(del); g.Children.Add(toggle); g.Children.Add(label);
            list.Children.Add(g);
        }
        if (S.Automations.Count == 0) list.Children.Add(Form.Text("No hay reglas. Agrega una abajo.", "Muted"));

        var trigger = AutomationTrigger.Capture;
        var actionBox = new ComboBox { Width = 230, Margin = new Thickness(0, 0, 8, 0) };
        void FillActions()
        {
            actionBox.Items.Clear();
            foreach (var a in AutomationRule.ActionsFor(trigger)) actionBox.Items.Add(new ComboBoxItem { Content = Loc.T(AutomationRule.ActionName(a)), Tag = a });
            actionBox.SelectedIndex = 0;
        }
        var triggerBox = Form.Combo([(AutomationTrigger.Capture, "Capturar"), (AutomationTrigger.Recording, "Grabar"), (AutomationTrigger.Ocr, "OCR")], trigger, v => { trigger = v; FillActions(); }, 150);
        triggerBox.Margin = new Thickness(0, 0, 8, 0);
        FillActions();
        var add = Form.Button("Agregar regla", () =>
        {
            if (actionBox.SelectedItem is not ComboBoxItem { Tag: AutomationAction a }) return;
            if (S.Automations.Any(x => x.Trigger == trigger && x.Action == a)) { Ui.Info("Esa regla ya existe. Puedes activarla o desactivarla en la lista."); return; }
            S.Automations.Add(new AutomationRule { Trigger = trigger, Action = a, Enabled = true });
            Save(); Refresh();
        }, "PrimaryButton");
        var arrow = Form.Text("→", "Body", false); arrow.Margin = new Thickness(0, 0, 8, 0); arrow.VerticalAlignment = VerticalAlignment.Center;
        var addRow = new WrapPanel { Children = { triggerBox, arrow, actionBox, add } };

        return Form.Page(
            Form.Text("Define qué ocurre automáticamente después de capturar, grabar o reconocer texto. Cada regla puede activarse o desactivarse.", "Muted"),
            new Border { Height = 12 },
            Form.Card("Reglas", list),
            Form.Card("Nueva regla", addRow),
            Form.Card("Opciones de las reglas",
                Form.Row("Texto de la marca de agua", Form.Input(S.WatermarkText, v => { S.WatermarkText = v; Save(); })),
                Form.Button("Restaurar reglas predeterminadas", () => { S.Automations = AutomationRule.Defaults(); Save(); Refresh(); })),
            Form.Text("Por seguridad, las automatizaciones solo ejecutan acciones internas de la aplicación: no lanzan programas externos, no ejecutan comandos y no suben archivos a ningún servicio.", "Muted"));
    }
}

public sealed class StoragePage : BuiltPage
{
    FrameworkElement Folder(string label, string current, string fallback, Action<string> set)
    {
        var path = Form.Text(string.IsNullOrWhiteSpace(current) ? fallback : current, "Muted", false);
        path.MaxWidth = 380; path.TextTrimming = TextTrimming.CharacterEllipsis; path.TextWrapping = TextWrapping.NoWrap; path.VerticalAlignment = VerticalAlignment.Center; path.Margin = new Thickness(0, 0, 10, 0);
        path.ToolTip = path.Text;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(path);
        sp.Children.Add(Form.Button("Cambiar…", () => { var f = Ui.PickFolder(path.Text, label); if (f != null) { set(f); Save(); Refresh(); } }));
        sp.Children.Add(Form.Button("Abrir", () => { Directory.CreateDirectory(path.Text); Shell.OpenFile(path.Text); }));
        sp.Children.Add(Form.Button("Predeterminada", () => { set(""); Save(); Refresh(); }));
        return Form.Row(label, sp);
    }

    protected override UIElement Build()
    {
        bool Rule(AutomationAction a) => AutomationPlan.IsEnabled(S.Automations, AutomationTrigger.Capture, a);
        void SetRule(AutomationAction a, bool v) { AutomationPlan.SetEnabled(S.Automations, AutomationTrigger.Capture, a, v); Save(); }
        var preview = Form.Text("", "Muted", false);
        void UpdatePreview() => preview.Text = Loc.T("Ejemplo:") + " " + FileNaming.Expand(S.FileNameTemplate, DateTime.Now, 1) + "." + S.ImageFormat;
        UpdatePreview();
        var template = Form.Input(S.FileNameTemplate, v => { S.FileNameTemplate = string.IsNullOrWhiteSpace(v) ? new AppSettings().FileNameTemplate : v.Trim(); Save(); UpdatePreview(); });

        return Form.Page(
            Form.Card("Carpetas",
                Folder("Carpeta predeterminada de capturas", S.CapturesFolder, AppPaths.DefaultCapturesFolder, v => S.CapturesFolder = v),
                Folder("Carpeta de videos", S.VideosFolder, AppPaths.DefaultVideosFolder, v => S.VideosFolder = v),
                Folder("Carpeta de GIF", S.GifsFolder, AppPaths.DefaultGifsFolder, v => S.GifsFolder = v)),
            Form.Card("Nombre y formato",
                Form.Row("Nombre automático", template, "Marcadores: {fecha} {hora} {yyyy} {MM} {dd} {HH} {mm} {ss} {n} {tipo}"),
                preview,
                Form.Row("Formato predeterminado", Form.Combo([("png", "PNG"), ("jpg", "JPG"), ("webp", "WebP"), ("bmp", "BMP")], S.ImageFormat, v => { S.ImageFormat = v; Save(); UpdatePreview(); }, 200, false)),
                Form.Row("Calidad JPG / WebP (compresión)", Form.Combo([(100, "100 (máxima)"), (95, "95"), (90, "90"), (80, "80"), (70, "70"), (60, "60")], S.JpegQuality is 100 or 95 or 90 or 80 or 70 or 60 ? S.JpegQuality : 90, v => { S.JpegQuality = v; Save(); }, 200, false))),
            Form.Card("Al capturar",
                Form.ToggleRow("Guardar automáticamente", Rule(AutomationAction.AutoSave), v => SetRule(AutomationAction.AutoSave, v), "Cada captura se escribe en la carpeta de capturas."),
                Form.ToggleRow("Preguntar ubicación al guardar", S.AskWhereToSave, v => { S.AskWhereToSave = v; Save(); }, "Muestra el diálogo «Guardar como» de Windows en cada captura."),
                Form.ToggleRow("Copiar al portapapeles después de capturar", Rule(AutomationAction.CopyToClipboard), v => SetRule(AutomationAction.CopyToClipboard, v)),
                Form.ToggleRow("Abrir el editor después de capturar", Rule(AutomationAction.OpenEditor), v => SetRule(AutomationAction.OpenEditor, v)),
                Form.Text("Los archivos exportados se recodifican desde los píxeles y no incluyen metadatos (EXIF, autor, ubicación).", "Muted")),
            Form.Card("Espacio y limpieza",
                Form.Text(Loc.T("Espacio usado: {0} · Elementos en la biblioteca: {1}", FileNaming.FormatSize(Studio.Storage.UsedBytes()), Studio.Library.Repo.Count()), "Body", false),
                Form.Buttons(
                    Form.Button("Borrar historial de la biblioteca", () =>
                    {
                        if (!Ui.Confirm("Se olvidará el historial de la biblioteca (favoritos, etiquetas y colecciones). Tus archivos NO se eliminarán. ¿Continuar?")) return;
                        Studio.Library.ClearHistory(); Refresh();
                    }),
                    Form.Button("Borrar archivos temporales", () => { AppPaths.ClearTemp(); Ui.Info("Archivos temporales y miniaturas eliminados."); }))));
    }
}

public sealed class SettingsPage : BuiltPage
{
    protected override UIElement Build()
    {
        var ocrLangs = new List<(string, string)> { ("auto", Loc.T("Automático (idioma de Windows)")) };
        foreach (var (tag, name) in OcrService.AvailableLanguages()) ocrLangs.Add((tag, name));
        if (!ocrLangs.Any(l => l.Item1 == S.OcrLanguage)) ocrLangs.Add((S.OcrLanguage, S.OcrLanguage));

        return Form.Page(
            Form.Card("Apariencia e idioma",
                Form.Row("Tema", Form.Combo([("dark", "Oscuro"), ("light", "Claro")], S.Theme, v => { S.Theme = v; Studio.ApplyTheme(); Save(); })),
                Form.Row("Idioma", Form.Combo([("es", "Español"), ("en", "English")], S.Language, v => { S.Language = v; Studio.ApplyLanguage(); Save(); Refresh(); }, 200, false))),
            Form.Card("Inicio y cierre",
                Form.ToggleRow("Iniciar con Windows", S.StartWithWindows, v => { S.StartWithWindows = v; Autostart.Set(v, S.StartMinimized); Save(); }),
                Form.ToggleRow("Iniciar minimizada en la bandeja", S.StartMinimized, v => { S.StartMinimized = v; Autostart.Set(S.StartWithWindows, v); Save(); }),
                Form.Row("Al pulsar la X de la ventana", Form.Combo([("tray", "Minimizar a la bandeja (atajos activos)"), ("exit", "Cerrar la aplicación por completo")], S.CloseBehavior, v => { S.CloseBehavior = v; Save(); }, 300)),
                Form.Buttons(Form.Button("Salir completamente ahora", Studio.ExitCompletely, "DangerButton"))),
            Form.Card("Avisos",
                Form.ToggleRow("Notificaciones", S.Notifications, v => { S.Notifications = v; Save(); }),
                Form.ToggleRow("Sonidos", S.Sounds, v => { S.Sounds = v; Save(); })),
            Form.Card("Captura",
                Form.Row("Temporizador predeterminado", Form.Combo([(0, "Sin temporizador"), (3, "3 segundos"), (5, "5 segundos"), (10, "10 segundos")], S.CaptureDelaySeconds, v => { S.CaptureDelaySeconds = v; Save(); }), "Se aplica a pantalla completa, monitor y ventana activa."),
                Form.ToggleRow("Incluir el cursor en las capturas", S.IncludeCursor, v => { S.IncludeCursor = v; Save(); })),
            Form.Card("OCR",
                Form.Row("Idioma de reconocimiento", Form.Combo(ocrLangs, S.OcrLanguage, v => { S.OcrLanguage = v; Save(); }, 300, false), "Depende de los paquetes de idioma instalados en Windows. El reconocimiento es 100 % local."),
                Form.Row("Buscador para «Buscar texto»", Form.Combo([("google", "Google"), ("bing", "Bing"), ("duckduckgo", "DuckDuckGo")], S.SearchEngine, v => { S.SearchEngine = v; Save(); }, 200, false))),
            Form.Card("Privacidad y diagnósticos",
                Form.Text("Fran Capture Studio funciona en local: sin cuentas, sin telemetría y sin subir capturas a ningún servicio.", "Muted"),
                Form.ToggleRow("Guardar registro local de errores", S.DiagnosticsLogging, v => { S.DiagnosticsLogging = v; Log.Enabled = v; Save(); }, "Solo eventos técnicos; nunca contenido de capturas ni pulsaciones. No se envía a ningún sitio."),
                Form.Buttons(
                    Form.Button("Ver registro", () => { if (File.Exists(AppPaths.LogFile)) Shell.OpenFile(AppPaths.LogFile); else Ui.Info("El registro está vacío."); }),
                    Form.Button("Borrar registro", () => { Log.Clear(); Ui.Info("Registro eliminado."); }),
                    Form.Button("Borrar temporales", () => { AppPaths.ClearTemp(); Ui.Info("Archivos temporales y miniaturas eliminados."); }))),
            Form.Card("Copia de la configuración",
                Form.Buttons(Form.Button("Exportar configuración…", Export), Form.Button("Importar configuración…", Import))),
            Form.Card("Acerca de",
                Form.Text($"Fran Capture Studio {Studio.Version}", "Body", false),
                Form.Text("Desarrollos Francisco J Vazquez Santiago", "Muted", false),
                Form.Text("Este instalador no está firmado digitalmente. Componentes de terceros: .NET, FFmpeg (LGPL, ejecutable independiente), NAudio, ZXing.Net, SQLite, PDFsharp, ONNX Runtime y CommunityToolkit. Consulta THIRD-PARTY-NOTICES.md en la carpeta de instalación.", "Muted"),
                Form.Buttons(Form.Button("Abrir carpeta de instalación", () => Shell.OpenFile(AppPaths.AppDir)), Form.Button("Abrir carpeta de datos", () => Shell.OpenFile(AppPaths.DataDir)))));
    }

    void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = Loc.T("Exportar configuración"), Filter = "JSON|*.json", FileName = "FranCaptureStudio-configuracion.json", AddExtension = true };
        if (dlg.ShowDialog() != true) return;
        try { File.WriteAllText(dlg.FileName, SettingsSerializer.Serialize(S)); Ui.Info("Configuración exportada."); }
        catch (Exception ex) { Ui.Error(Loc.T("No se pudo exportar:") + "\n" + ex.Message); }
    }

    void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Importar configuración"), Filter = "JSON|*.json" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var fi = new FileInfo(dlg.FileName);
            if (fi.Length > 2_000_000) { Ui.Error("El archivo es demasiado grande para ser una configuración válida."); return; }
            var error = SettingsValidator.ValidateImport(File.ReadAllText(dlg.FileName), out var imported);
            if (error != null || imported == null) { Ui.Error(Loc.T("El archivo no se aplicó.") + "\n" + Loc.T(error ?? "")); return; }
            if (!Ui.Confirm("El archivo es válido. ¿Reemplazar la configuración actual por la importada?")) return;
            imported.FirstRunCompleted = true;
            Studio.ReplaceSettings(imported);
            var conflicts = Studio.ApplyHotkeys(false);
            Refresh();
            Ui.Info(Loc.T("Configuración importada.") + (conflicts != null ? "\n\n" + Loc.T("Atajos no disponibles") + ":\n" + conflicts : ""));
        }
        catch (Exception ex) { Ui.Error(Loc.T("No se pudo importar:") + "\n" + ex.Message); }
    }
}

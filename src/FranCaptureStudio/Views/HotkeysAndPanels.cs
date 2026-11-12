using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FranCapture.Core.Diagnostics;
using FranCapture.Core.Hotkeys;
using FranCapture.Core.Localization;
using FranCaptureStudio.Capture;
using FranCaptureStudio.Interop;
using FranCaptureStudio.Recording;
using FranCaptureStudio.Services;
using CaptureMode = FranCaptureStudio.Capture.CaptureMode;

namespace FranCaptureStudio.Views;

/// <summary>Diálogo «Configurar tecla»: detecta la combinación pulsada, la valida y comprueba su disponibilidad real.</summary>
public sealed class HotkeyCaptureWindow : StudioWindow
{
    readonly HotkeyAction _action;
    readonly TextBlock _detected = new() { FontSize = 26, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 10) };
    readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MinHeight = 54 };
    readonly WrapPanel _suggestions = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 8) };
    readonly Button _save;
    HotkeyGesture _gesture;
    public HotkeyGesture? Result { get; private set; }

    public HotkeyCaptureWindow(HotkeyAction action)
    {
        _action = action;
        Title = Loc.T("Configurar tecla"); Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _detected.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        _detected.Text = "…";
        _message.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        _message.Text = Loc.T("Pulsa ahora la tecla o combinación que quieres usar (por ejemplo F9 o Ctrl+Shift+S).");
        _save = Form.Button("Guardar", () => { Result = _gesture; DialogResult = true; }, "PrimaryButton");
        _save.IsEnabled = false;
        var cancel = Form.Button("Cancelar", () => DialogResult = false);
        var name = new TextBlock { Text = Loc.T(HotkeyDefaults.DisplayName(action)), Style = Ui.S("SectionTitle"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0) };
        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Children = { name, _detected, _message, _suggestions, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { _save, cancel } } },
        };
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        // Mientras se configura, los atajos propios se liberan para que la tecla llegue a este diálogo.
        Loaded += (_, _) => { Studio.Hotkeys.UnregisterAll(); Focus(); };
        Closed += (_, _) => Studio.ApplyHotkeys(false);
    }

    static HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        var k = Keyboard.Modifiers;
        if ((k & ModifierKeys.Control) != 0) m |= HotkeyModifiers.Ctrl;
        if ((k & ModifierKeys.Alt) != 0) m |= HotkeyModifiers.Alt;
        if ((k & ModifierKeys.Shift) != 0) m |= HotkeyModifiers.Shift;
        if ((k & ModifierKeys.Windows) != 0) m |= HotkeyModifiers.Win;
        return m;
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        if (key == Key.Escape && CurrentModifiers() == HotkeyModifiers.None) { DialogResult = false; return; }
        if (key == Key.Enter && CurrentModifiers() == HotkeyModifiers.None && _save.IsEnabled) { Result = _gesture; DialogResult = true; return; }
        Evaluate(new HotkeyGesture(CurrentModifiers(), KeyInterop.VirtualKeyFromKey(key)));
    }

    void OnKeyUp(object sender, KeyEventArgs e)
    {
        // Windows no envía «tecla abajo» para ImprPant; solo «tecla arriba».
        if (e.Key == Key.Snapshot || (e.Key == Key.System && e.SystemKey == Key.Snapshot)) { e.Handled = true; Evaluate(new HotkeyGesture(CurrentModifiers(), HotkeyGesture.VK_SNAPSHOT)); }
    }

    public void Evaluate(HotkeyGesture g)
    {
        _gesture = g;
        _detected.Text = g.ToString();
        _suggestions.Children.Clear();
        _save.IsEnabled = false;
        string? problem = null;
        if (!HotkeyGesture.IsKnownKey(g.VirtualKey)) problem = Loc.T("Esa tecla no puede usarse como atajo global.");
        else if (g.Validate() is var v && v != HotkeyProblem.None) problem = Loc.T(HotkeyGesture.Explain(v));
        else if (HotkeyMap.FindConflict(Studio.Settings.Hotkeys, _action, g) is { } other)
            problem = Loc.T("Ya está asignada a «{0}». Quita ese atajo primero o elige otra combinación.", Loc.T(HotkeyDefaults.DisplayName(other)));
        else if (!Studio.Hotkeys.IsAvailable(g))
            problem = Loc.T("Windows indica que esta combinación ya está registrada por otra aplicación (o reservada por el sistema), así que no puede usarse.") +
                      (g.VirtualKey == HotkeyGesture.VK_SNAPSHOT ? " " + Loc.T("Para ImprPant, desactiva en Windows «Usar la tecla Imprimir pantalla para abrir Recortes» (Configuración > Accesibilidad > Teclado).") : "");
        if (problem == null)
        {
            _message.Text = "✓ " + Loc.T("Disponible. Pulsa Guardar (o Enter) para asignarla.");
            _message.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
            _save.IsEnabled = true;
            return;
        }
        _message.Text = problem;
        _message.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        var others = Studio.Settings.Hotkeys.Where(k => k.Key != _action.ToString()).ToDictionary(k => k.Key, k => k.Value);
        var alts = HotkeyMap.Suggest(others, g, Studio.Hotkeys.IsAvailable);
        if (alts.Count > 0) _suggestions.Children.Add(new TextBlock { Text = Loc.T("Alternativas disponibles:"), Style = Ui.S("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (var a in alts)
        {
            var alt = a;
            var b = new Button { Content = alt.ToString(), Style = Ui.S("SecondaryButton"), Margin = new Thickness(0, 0, 6, 0), Focusable = false };
            b.Click += (_, _) => Evaluate(alt);
            _suggestions.Children.Add(b);
        }
    }

    internal bool CanSave => _save.IsEnabled;
    internal string MessageText => _message.Text;
}

public sealed class HotkeysPage : BuiltPage
{
    protected override UIElement Build()
    {
        var failures = new Dictionary<HotkeyAction, string>();
        foreach (var kv in S.Hotkeys)
            if (kv.Value.Length > 0 && Enum.TryParse<HotkeyAction>(kv.Key, out var a) && !Studio.Hotkeys.Registered.ContainsKey(a)) failures[a] = kv.Value;

        var rows = new StackPanel();
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            var act = action;
            string gesture = Hotkey(act);
            var label = Form.Text(HotkeyDefaults.DisplayName(act), "FieldLabel");
            var key = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 4), MinWidth = 150, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            key.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
            var keyText = new TextBlock { Text = gesture.Length > 0 ? gesture : Loc.T("Sin atajo"), FontWeight = gesture.Length > 0 ? FontWeights.SemiBold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center };
            keyText.SetResourceReference(TextBlock.ForegroundProperty, failures.ContainsKey(act) ? "DangerBrush" : gesture.Length > 0 ? "TextBrush" : "Text2Brush");
            key.Child = keyText;
            if (failures.ContainsKey(act)) key.ToolTip = Loc.T("No se pudo registrar: está en uso por otra aplicación. Elige otra combinación.");
            var set = Form.Button("Configurar tecla", () => Configure(act), gesture.Length == 0 ? "SecondaryButton" : "SecondaryButton");
            var clear = new Button { Style = Ui.S("IconButton"), Content = "", ToolTip = Loc.T("Quitar atajo"), IsEnabled = gesture.Length > 0 };
            clear.Click += (_, _) => { S.Hotkeys[act.ToString()] = ""; Studio.Hotkeys.Unregister(act); Save(); Refresh(); };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Children = { key, set, clear } };
            var dp = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            DockPanel.SetDock(right, Dock.Right); dp.Children.Add(right); dp.Children.Add(label);
            rows.Children.Add(dp);
        }

        UIElement? warning = null;
        if (failures.Count > 0)
        {
            var t = Form.Text(Loc.T("Algunos atajos no están activos porque otra aplicación ya usa esa combinación:") + " " + string.Join(", ", failures.Select(f => f.Value)) + ". " + Loc.T("Asígnales otra tecla."), "Body", false);
            t.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            warning = Form.Card(null, t);
        }
        return Form.Page(
            Form.Text("Los atajos funcionan en todo Windows aunque otra aplicación tenga el foco, mientras Fran Capture Studio esté abierto o en la bandeja. Se liberan al salir completamente.", "Muted"),
            new Border { Height = 12 }, warning,
            Form.Card("Acciones", rows),
            Form.Card("Notas",
                Form.Text("• Las teclas de función (F2–F11, F13–F24), ImprPant y Pausa pueden usarse solas. Las letras y números necesitan Ctrl o Alt.", "Muted"),
                Form.Text("• ImprPant solo puede asignarse si Windows no la tiene reservada para la herramienta Recortes (Configuración > Accesibilidad > Teclado).", "Muted"),
                Form.Text("• Se usa la API oficial RegisterHotKey: Windows solo avisa de las combinaciones registradas; no se observa ninguna otra pulsación.", "Muted"),
                Form.Buttons(Form.Button("Restaurar atajos predeterminados", () =>
                {
                    S.Hotkeys = HotkeyDefaults.Create();
                    Studio.ApplyHotkeys(false); Save(); Refresh();
                }))));
    }

    void Configure(HotkeyAction action)
    {
        var dlg = new HotkeyCaptureWindow(action) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Result is { } g)
        {
            S.Hotkeys[action.ToString()] = g.ToString();
            Studio.SaveSettings();
        }
        // El diálogo vuelve a registrar todos los atajos al cerrarse (Closed se ejecuta antes de volver aquí).
        Studio.ApplyHotkeys(false);
        Save(); Refresh();
    }
}

/// <summary>Configuración inicial: idioma, tecla de captura (F9 por defecto), cierre y arranque.</summary>
public sealed class FirstRunWindow : StudioWindow
{
    public FirstRunWindow()
    {
        Title = "Fran Capture Studio"; Width = 600; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Build();
    }

    void Build()
    {
        var s = Studio.Settings;
        var logo = new Image { Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/FranCaptureStudio;component/Assets/icon-512.png")), Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var title = Form.Text("Te damos la bienvenida a Fran Capture Studio", "PageTitle"); title.Margin = new Thickness(0, 12, 0, 4); title.TextWrapping = TextWrapping.Wrap;
        var keyText = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MinWidth = 90, TextAlignment = TextAlignment.Right };
        void ShowKey() => keyText.Text = s.Hotkeys[nameof(HotkeyAction.CaptureRegion)] is { Length: > 0 } g ? g : Loc.T("Sin atajo");
        ShowKey();
        var change = Form.Button("Configurar tecla", () =>
        {
            var dlg = new HotkeyCaptureWindow(HotkeyAction.CaptureRegion) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Result is { } g) { s.Hotkeys[nameof(HotkeyAction.CaptureRegion)] = g.ToString(); ShowKey(); }
        });
        var start = Form.Button("Empezar", Close, "PrimaryButton"); start.Margin = new Thickness(0); start.MinWidth = 120; start.IsDefault = true;
        Content = new StackPanel
        {
            Margin = new Thickness(26),
            Children =
            {
                logo, title,
                Form.Text("Unos ajustes rápidos; podrás cambiarlos cuando quieras en Configuración.", "Muted"),
                new Border { Height = 14 },
                Form.Card(null,
                    Form.Row("Idioma", Form.Combo([("es", "Español"), ("en", "English")], s.Language, v => { s.Language = v; Studio.ApplyLanguage(); }, 180, false)),
                    Form.Row("Tecla para abrir el selector de captura", new StackPanel { Orientation = Orientation.Horizontal, Children = { keyText, change } }, "Funciona desde cualquier programa."),
                    Form.Row("Al cerrar la ventana", Form.Combo([("tray", "Seguir en la bandeja (atajos activos)"), ("exit", "Cerrar por completo")], s.CloseBehavior, v => s.CloseBehavior = v, 260)),
                    Form.ToggleRow("Iniciar con Windows (minimizada)", s.StartWithWindows, v => s.StartWithWindows = v)),
                Form.Text("Todo funciona en local: sin cuentas, sin telemetría y sin subir tus capturas.", "Muted"),
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { start } },
            },
        };
    }
}

/// <summary>Modo rápido: panel pequeño junto al cursor con las acciones principales.</summary>
public sealed class QuickPanelWindow : Window
{
    static QuickPanelWindow? _current;

    public static void Toggle()
    {
        if (_current != null) { _current.Close(); return; }
        _current = new QuickPanelWindow();
        _current.Show();
        var p = Ui.CursorPos();
        var wa = Ui.WorkAreaAt(p);
        var b = Ui.PhysicalBounds(_current);
        Ui.MovePhysical(_current, Math.Clamp(p.X - b.Width / 2, wa.Left, Math.Max(wa.Left, wa.Right - b.Width)), Math.Clamp(p.Y - b.Height - 12, wa.Top, Math.Max(wa.Top, wa.Bottom - b.Height)));
        _current.Activate();
    }

    QuickPanelWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize; Title = "Fran Capture Studio – panel rápido";
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        var grid = new WrapPanel { Width = 3 * 132 };
        void Add(string glyph, string title, Action a)
        {
            var t = Form.Tile(glyph, title, null, () => { Close(); a(); }, 122);
            t.MinHeight = 84; t.Margin = new Thickness(0, 0, 10, 10); t.Padding = new Thickness(12, 10, 12, 10);
            grid.Children.Add(t);
        }
        Add("", "Capturar región", () => Studio.Execute(HotkeyAction.CaptureRegion));
        Add("", "Pantalla completa", () => Studio.Execute(HotkeyAction.CaptureFullScreen));
        Add("", "Ventana", () => _ = Studio.Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.PickWindow }));
        if (Studio.Recorder.IsActive) Add("", "Detener grabación", () => _ = Studio.Recorder.StopAsync());
        else Add("", "Grabar", () => _ = Studio.Recorder.StartAsync(new RecordingTarget()));
        Add("", "OCR", () => _ = Studio.OcrRegionAsync());
        Add("", "Biblioteca", () => Studio.ShowMain("library"));
        var border = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(12, 12, 2, 2), Margin = new Thickness(8), Child = grid };
        border.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Content = border;
        Deactivated += (_, _) => { if (_current == this) try { Close(); } catch { } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closed += (_, _) => { if (_current == this) _current = null; };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.ExcludeFromCapture(Ui.Handle(this));
    }
}

/// <summary>Renderiza cada pantalla a PNG (modo «--render-ui carpeta»): se usa en CI para revisar la interfaz y documentarla.</summary>
public static class UiRenderer
{
    static void Snap(FrameworkElement root, string path)
    {
        root.UpdateLayout();
        int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
        if (w < 1 || h < 1) return;
        var img = Img.Draw(w, h, dc =>
        {
            dc.DrawRectangle(Ui.B("BgBrush"), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None }, null, new Rect(0, 0, w, h));
        });
        Img.Save(img, path);
    }

    public static async Task RenderAllAsync(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var theme in new[] { "dark", "light" })
            {
                Studio.Settings.Theme = theme;
                Studio.ApplyTheme();
                var main = new MainWindow { Width = 1240, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                Studio.Main = main;
                main.Show();
                foreach (var page in theme == "dark" ? new[] { "dashboard", "library", "recorder", "ocr", "tools", "qa", "automations", "hotkeys", "storage", "settings" } : ["dashboard", "settings"])
                {
                    main.Navigate(page);
                    await Task.Delay(500);
                    Snap((FrameworkElement)main.Content, Path.Combine(dir, $"{theme}-{page}.png"));
                }
                if (theme == "dark")
                {
                    var sample = Img.Draw(900, 520, dc =>
                    {
                        dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(36, 52, 84), Color.FromRgb(18, 110, 140), 30), null, new Rect(0, 0, 900, 520));
                        dc.DrawText(Editor.AnnotationRenderer.Format("usuario: ana.perez@correo.es  ·  IP 192.168.1.20", 22, Brushes.White), new Point(40, 60));
                        dc.DrawText(Editor.AnnotationRenderer.Format("Fran Capture Studio – imagen de ejemplo", 30, Brushes.White), new Point(40, 200));
                    });
                    var ed = Editor.EditorWindow.Open(sample, null);
                    var c = ed.TestCanvas;
                    c.Add(new Editor.Annotation { Kind = Editor.AnnKind.Arrow, A = new Point(520, 420), B = new Point(330, 250), Color = Color.FromRgb(255, 59, 48), Thickness = 6 });
                    c.Add(new Editor.Annotation { Kind = Editor.AnnKind.Rect, A = new Point(30, 190), B = new Point(640, 250), Color = Color.FromRgb(0, 184, 255), Thickness = 4 });
                    c.Add(new Editor.Annotation { Kind = Editor.AnnKind.Step, A = new Point(700, 120), Number = 1, Color = Color.FromRgb(0, 184, 255), FontSize = 26 });
                    c.Add(new Editor.Annotation { Kind = Editor.AnnKind.Redact, A = new Point(140, 56), B = new Point(400, 96), Color = Colors.Black });
                    c.Add(new Editor.Annotation { Kind = Editor.AnnKind.Pixelate, A = new Point(470, 56), B = new Point(680, 96), Strength = 10 });
                    c.Add(new Editor.Annotation { Kind = Editor.AnnKind.Callout, A = new Point(560, 300), B = new Point(840, 370), Tail = new Point(520, 420), Color = Color.FromRgb(255, 221, 0), Text = "Aquí está el fallo", FontSize = 20 });
                    c.Add(new Editor.Annotation { Kind = Editor.AnnKind.Text, A = new Point(40, 440), Text = "Texto editable", Color = Colors.White, FontSize = 28 });
                    await Task.Delay(700);
                    Snap((FrameworkElement)ed.Content, Path.Combine(dir, "dark-editor.png"));
                    Img.Save(c.FlattenNow(), Path.Combine(dir, "editor-export.png"));
                    Studio.IsExitingForRender = true;
                    ed.Close();
                    var fr = new FirstRunWindow(); fr.Show(); await Task.Delay(300);
                    Snap((FrameworkElement)fr.Content, Path.Combine(dir, "dark-firstrun.png")); fr.Close();
                    var hk = new HotkeyCaptureWindow(HotkeyAction.CaptureRegion) { WindowStartupLocation = WindowStartupLocation.CenterScreen }; hk.Show(); await Task.Delay(300);
                    hk.Evaluate(new HotkeyGesture(HotkeyModifiers.None, 'A'));
                    await Task.Delay(200);
                    Snap((FrameworkElement)hk.Content, Path.Combine(dir, "dark-hotkey-dialog.png")); hk.Close();
                }
                Studio.IsExitingForRender = true;
                main.Close();
                Studio.Main = null;
            }
            File.WriteAllText(Path.Combine(dir, "ok.txt"), "render ok");
        }
        catch (Exception ex)
        {
            Log.Error("Render de interfaz", ex);
            try { File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString()); } catch { }
        }
    }
}

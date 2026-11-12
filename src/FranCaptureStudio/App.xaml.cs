using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using FranCapture.Core.Automation;
using FranCapture.Core.Diagnostics;
using FranCapture.Core.Hotkeys;
using FranCapture.Core.Localization;
using FranCapture.Core.Settings;
using FranCaptureStudio.Capture;
using FranCaptureStudio.Recording;
using FranCaptureStudio.Services;
using FranCaptureStudio.Views;

namespace FranCaptureStudio;

/// <summary>Punto central de servicios de la aplicación.</summary>
public static class Studio
{
    public static AppSettings Settings { get; private set; } = new();
    public static SettingsStore Store { get; private set; } = null!;
    public static StorageService Storage { get; private set; } = null!;
    public static LibraryService Library { get; private set; } = null!;
    public static HotkeyService Hotkeys { get; private set; } = null!;
    public static CaptureCoordinator Capture { get; private set; } = null!;
    public static RecordingController Recorder { get; private set; } = null!;
    public static TrayService? Tray { get; private set; }
    public static MainWindow? Main { get; set; }
    public static event Action? SettingsChanged;
    public static bool IsExiting { get; private set; }
    public static string Version => typeof(Studio).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>Inicializa los servicios sin interfaz (también se usa en las pruebas de integración).</summary>
    public static void InitServices(bool withTray)
    {
        Log.Init(AppPaths.LogFile, true);
        Store = new SettingsStore(AppPaths.SettingsFile);
        Settings = Store.Load();
        Log.Enabled = Settings.DiagnosticsLogging;
        Loc.Instance.Language = Settings.Language;
        Storage = new StorageService(() => Settings);
        Library = new LibraryService(AppPaths.LibraryDb);
        Hotkeys = new HotkeyService();
        Capture = new CaptureCoordinator();
        Recorder = new RecordingController();
        if (withTray) Tray = new TrayService { DarkMenu = Settings.Theme == "dark" };
    }

    public static void SaveSettings()
    {
        try { Store.Save(Settings); }
        catch (Exception ex) { Log.Error("Guardar configuración", ex); }
    }

    public static void RaiseSettingsChanged()
    {
        SaveSettings();
        SettingsChanged?.Invoke();
    }

    /// <summary>Sustituye toda la configuración (importación) y la aplica.</summary>
    public static void ReplaceSettings(AppSettings s)
    {
        Settings = s;
        Log.Enabled = s.DiagnosticsLogging;
        ApplyTheme(); ApplyLanguage(); ApplyHotkeys(false);
        Autostart.Set(s.StartWithWindows, s.StartMinimized);
        RaiseSettingsChanged();
    }

    public static void ApplyTheme()
    {
        var app = Application.Current;
        if (app == null) return;
        bool dark = Settings.Theme == "dark";
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml") };
        var merged = app.Resources.MergedDictionaries;
        merged[0] = dict;
        try { app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light; } catch (Exception ex) { Log.Error("Tema", ex); }
        // ThemeMode añade sus propios diccionarios; los estilos propios deben quedar al final para tener prioridad.
        var styles = merged.FirstOrDefault(d => d.Source != null && d.Source.OriginalString.EndsWith("Styles.xaml", StringComparison.OrdinalIgnoreCase));
        if (styles != null && merged[^1] != styles) { merged.Remove(styles); merged.Add(styles); }
        if (Tray != null) Tray.DarkMenu = dark;
        foreach (Window w in app.Windows) ThemeWindow(w);
    }

    public static void ThemeWindow(Window w)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (h != IntPtr.Zero) Interop.Native.UseDarkTitleBar(h, Settings.Theme == "dark");
    }

    public static void ApplyLanguage() => Loc.Instance.Language = Settings.Language;

    /// <summary>Registra los atajos de la configuración. Devuelve un texto con los conflictos (o null).</summary>
    public static string? ApplyHotkeys(bool notify)
    {
        var failures = Hotkeys.Apply(Settings.Hotkeys);
        if (failures.Count == 0) return null;
        var lines = failures.Select(f =>
            $"• {Loc.T(HotkeyDefaults.DisplayName(f.Key))}: {Settings.Hotkeys[f.Key.ToString()]} — " +
            Loc.T(f.Value == HotkeyRegistration.InUseByOtherApp ? "en uso por otra aplicación" : "no se pudo registrar"));
        var msg = string.Join("\n", lines);
        if (notify) Notify(Loc.T("Atajos no disponibles"), msg + "\n" + Loc.T("Cámbialos en Atajos de teclado."), () => ShowMain("hotkeys"), true);
        return msg;
    }

    public static void Notify(string title, string message, Action? onClick = null, bool error = false)
    {
        if (!Settings.Notifications && !error) return;
        if (Tray != null) Tray.Notify(title, message, onClick, error);
        else Log.Info($"[notificación] {title}: {message}");
    }

    public static void ShowMain(string? page = null)
    {
        var app = Application.Current;
        if (app == null) return;
        if (Main == null) { Main = new MainWindow(); }
        if (!Main.IsVisible) Main.Show();
        if (Main.WindowState == WindowState.Minimized) Main.WindowState = WindowState.Normal;
        Main.Activate();
        Main.Topmost = true; Main.Topmost = false;
        if (page != null) Main.Navigate(page);
    }

    public static async Task RunOcrAsync(BitmapSource image)
    {
        ShowMain("ocr");
        if (Main != null) await Main.OcrPage.RecognizeAsync(image);
    }

    public static void ExitCompletely()
    {
        if (IsExiting) return;
        IsExiting = true;
        try { Recorder.StopForExit(); } catch { }
        try { Hotkeys.Dispose(); } catch { }   // libera todos los atajos registrados
        try { Tray?.Dispose(); } catch { }
        try { SaveSettings(); Library.Dispose(); } catch { }
        Application.Current?.Shutdown();
    }

    /// <summary>Ejecuta la acción asociada a un atajo o comando externo.</summary>
    public static void Execute(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.OpenApp: ShowMain(); break;
            case HotkeyAction.CaptureRegion: _ = Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.Region }); break;
            case HotkeyAction.CaptureFullScreen: _ = Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.FullScreen }); break;
            case HotkeyAction.CaptureActiveWindow: _ = Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.ActiveWindow }); break;
            case HotkeyAction.RepeatLastRegion: _ = Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.LastRegion }); break;
            case HotkeyAction.StartRecording: if (!Recorder.IsActive) _ = Recorder.StartAsync(new RecordingTarget()); break;
            case HotkeyAction.StartGifRecording: if (!Recorder.IsActive) _ = Recorder.StartWithRegionPickerAsync(gif: true); break;
            case HotkeyAction.PauseResumeRecording: Recorder.TogglePause(); break;
            case HotkeyAction.StopRecording: _ = Recorder.StopAsync(); break;
            case HotkeyAction.OcrRegion: _ = OcrRegionAsync(); break;
            case HotkeyAction.OpenEditor: Editor.EditorWindow.OpenEmpty(); break;
            case HotkeyAction.ShowLibrary: ShowMain("library"); break;
            case HotkeyAction.QuickPanel: QuickPanelWindow.Toggle(); break;
        }
    }

    public static async Task OcrRegionAsync()
    {
        if (Capture.IsBusy) return;
        using var res = OverlayForm.Show(new OverlayOptions { Purpose = OverlayPurpose.SelectOnly, AcceptLabel = "Reconocer texto" });
        if (res.Action == OverlayAction.Cancel || res.Image == null) return;
        await RunOcrAsync(Img.FromGdi(res.Image));
    }
}

public partial class App : Application
{
    SingleInstance? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        _single = new SingleInstance("FranCaptureStudio");
        if (!_single.IsFirst)
        {
            // Ya hay una instancia: se le reenvía la orden y esta termina.
            _single.SendToFirst(args);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += (_, ev) =>
        {
            Log.Error("Excepción no controlada", ev.Exception);
            ev.Handled = true;
            try { MessageBox.Show(Loc.T("Ocurrió un error inesperado. La aplicación seguirá funcionando.") + "\n\n" + ev.Exception.Message, "Fran Capture Studio", MessageBoxButton.OK, MessageBoxImage.Warning); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => Log.Error("Excepción fatal", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Error("Tarea no observada", ev.Exception); ev.SetObserved(); };

        int renderIdx = Array.IndexOf(args, "--render-ui");
        bool renderMode = renderIdx >= 0 && renderIdx + 1 < args.Length;

        Studio.InitServices(withTray: !renderMode);
        Studio.ApplyTheme();
        Log.Info($"Inicio v{Studio.Version}");

        if (renderMode)
        {
            _ = UiRenderer.RenderAllAsync(args[renderIdx + 1]).ContinueWith(_ => Dispatcher.Invoke(() => Shutdown(0)));
            return;
        }

        Studio.Tray!.Command += OnTrayCommand;
        Studio.Hotkeys.Pressed += Studio.Execute;
        _single.ArgumentsReceived += a => Dispatcher.BeginInvoke(() => HandleArgs(a, fromOtherInstance: true));
        _single.StartListening();
        Studio.Recorder.StateChanged += () => { if (Studio.Tray != null) Studio.Tray.IsRecording = Studio.Recorder.IsActive; };

        bool minimized = args.Contains("--minimized");
        if (!Studio.Settings.FirstRunCompleted)
        {
            new FirstRunWindow().ShowDialog();
            Studio.Settings.FirstRunCompleted = true;
            Studio.SaveSettings();
            minimized = false;
        }
        Studio.ApplyHotkeys(notify: true);
        Autostart.Set(Studio.Settings.StartWithWindows, Studio.Settings.StartMinimized);

        if (!minimized && !args.Any(a => a.StartsWith("--capture") || a == "--record" || a == "--ocr")) Studio.ShowMain();
        HandleArgs(args, fromOtherInstance: false);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            Editor.EditorWindow.OfferRecovery();
            Studio.Recorder.OfferRecovery();
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    void HandleArgs(string[] args, bool fromOtherInstance)
    {
        bool acted = false;
        foreach (var a in args)
        {
            switch (a)
            {
                case "--capture-region": Studio.Execute(HotkeyAction.CaptureRegion); acted = true; break;
                case "--capture-screen": Studio.Execute(HotkeyAction.CaptureFullScreen); acted = true; break;
                case "--capture-window": Studio.Execute(HotkeyAction.CaptureActiveWindow); acted = true; break;
                case "--record": Studio.Execute(HotkeyAction.StartRecording); acted = true; break;
                case "--ocr": Studio.Execute(HotkeyAction.OcrRegion); acted = true; break;
                case "--quick": Studio.Execute(HotkeyAction.QuickPanel); acted = true; break;
                case "--exit": Studio.ExitCompletely(); return;
                case "--minimized": acted = true; break;
                default:
                    if (!a.StartsWith("--") && File.Exists(a))
                    {
                        acted = true;
                        try
                        {
                            if (a.EndsWith(".fcsproj", StringComparison.OrdinalIgnoreCase)) Editor.EditorWindow.OpenProject(a);
                            else Editor.EditorWindow.Open(Img.Load(a), null);
                        }
                        catch (Exception ex) { Log.Error("Abrir archivo", ex); }
                    }
                    break;
            }
        }
        if (fromOtherInstance && !acted) Studio.ShowMain(); // segunda ejecución sin argumentos: restaurar la ventana
    }

    static void OnTrayCommand(TrayCommand cmd)
    {
        switch (cmd)
        {
            case TrayCommand.CaptureRegion: Studio.Execute(HotkeyAction.CaptureRegion); break;
            case TrayCommand.CaptureScreen: Studio.Execute(HotkeyAction.CaptureFullScreen); break;
            case TrayCommand.StartRecording: Studio.Execute(HotkeyAction.StartRecording); break;
            case TrayCommand.StopRecording: Studio.Execute(HotkeyAction.StopRecording); break;
            case TrayCommand.OpenApp: Studio.ShowMain(); break;
            case TrayCommand.OpenRecent: Studio.ShowMain("library"); break;
            case TrayCommand.Settings: Studio.ShowMain("settings"); break;
            case TrayCommand.QuickPanel: QuickPanelWindow.Toggle(); break;
            case TrayCommand.Exit: Studio.ExitCompletely(); break;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_single?.IsFirst == true && !Studio.IsExiting)
        {
            try { Studio.Hotkeys?.Dispose(); Studio.Tray?.Dispose(); } catch { }
        }
        _single?.Dispose();
        base.OnExit(e);
    }
}

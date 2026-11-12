using System.Windows;
using System.Windows.Controls;
using FranCapture.Core.Localization;
using FranCaptureStudio.Capture;

namespace FranCaptureStudio.Views;

public partial class MainWindow : StudioWindow
{
    public DashboardPage DashboardPage { get; } = new();
    public LibraryPage LibraryPage { get; } = new();
    public RecorderPage RecorderPage { get; } = new();
    public OcrPage OcrPage { get; } = new();
    public ToolsPage ToolsPage { get; } = new();
    public QaPage QaPage { get; } = new();
    public AutomationsPage AutomationsPage { get; } = new();
    public HotkeysPage HotkeysPage { get; } = new();
    public StoragePage StoragePage { get; } = new();
    public SettingsPage SettingsPage { get; } = new();

    readonly Dictionary<string, (RadioButton Nav, UserControl Page, string Title)> _pages;
    public string CurrentPage { get; private set; } = "";

    public MainWindow()
    {
        InitializeComponent();
        // En pantallas pequeñas la ventana no debe exceder el área de trabajo.
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, wa.Width - 24));
        Height = Math.Min(Height, Math.Max(MinHeight, wa.Height - 24));
        if (MinWidth > wa.Width) MinWidth = Math.Max(480, wa.Width);
        if (MinHeight > wa.Height) MinHeight = Math.Max(360, wa.Height);
        _pages = new()
        {
            ["dashboard"] = (NavDashboard, DashboardPage, "Inicio"),
            ["library"] = (NavLibrary, LibraryPage, "Biblioteca"),
            ["recorder"] = (NavRecorder, RecorderPage, "Grabación de pantalla"),
            ["ocr"] = (NavOcr, OcrPage, "OCR — extraer texto"),
            ["tools"] = (NavTools, ToolsPage, "Herramientas"),
            ["qa"] = (NavQa, QaPage, "Reporte de errores"),
            ["automations"] = (NavAutomations, AutomationsPage, "Automatizaciones"),
            ["hotkeys"] = (NavHotkeys, HotkeysPage, "Atajos de teclado"),
            ["storage"] = (NavStorage, StoragePage, "Almacenamiento"),
            ["settings"] = (NavSettings, SettingsPage, "Configuración"),
        };
        foreach (var p in _pages.Values) { p.Page.Visibility = Visibility.Collapsed; PageHost.Children.Add(p.Page); }
        VersionText.Text = "v" + Studio.Version;
        Loc.Instance.PropertyChanged += (_, _) => { if (_pages.TryGetValue(CurrentPage, out var p)) PageTitle.Text = Loc.T(p.Title); };
        Studio.Recorder.StateChanged += RefreshRecState;
        Navigate("dashboard");
    }

    void RefreshRecState() => RecState.Text = Studio.Recorder.IsActive ? "● " + Loc.T(Studio.Recorder.IsPaused ? "Grabación en pausa" : "Grabando") : "";

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_pages == null) return;
        var key = _pages.FirstOrDefault(p => p.Value.Nav == sender).Key;
        if (key != null) Navigate(key);
    }

    public void Navigate(string key)
    {
        if (!_pages.TryGetValue(key, out var target) || CurrentPage == key) return;
        CurrentPage = key;
        foreach (var p in _pages.Values) p.Page.Visibility = p.Page == target.Page ? Visibility.Visible : Visibility.Collapsed;
        if (target.Nav.IsChecked != true) target.Nav.IsChecked = true;
        PageTitle.Text = Loc.T(target.Title);
        (target.Page as IRefreshable)?.Refresh();
    }

    void QuickCapture_Click(object sender, RoutedEventArgs e) =>
        _ = Studio.Capture.CaptureAsync(new CaptureRequest { Mode = CaptureMode.Region, FromUi = true });

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!Studio.IsExiting)
        {
            e.Cancel = true;
            if (Studio.Settings.CloseBehavior == "exit")
            {
                if (Studio.Recorder.IsActive && !Ui.Confirm("Hay una grabación en curso. Si sales ahora se detendrá y se guardará. ¿Salir?", null, this)) return;
                Dispatcher.BeginInvoke(Studio.ExitCompletely);
            }
            else
            {
                Hide(); // sigue disponible en la bandeja para responder a los atajos
                if (!_trayHintShown) { _trayHintShown = true; Studio.Notify("Fran Capture Studio", Loc.T("Sigue activo en la bandeja del sistema. Usa «Salir completamente» en su menú para cerrarlo.")); }
            }
        }
        base.OnClosing(e);
    }

    static bool _trayHintShown;
}

public interface IRefreshable { void Refresh(); }

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FranCapture.Core.Localization;
using FranCaptureStudio.Interop;
using SD = System.Drawing;

namespace FranCaptureStudio.Views;

/// <summary>Ventana base: fondo sólido del tema, tipografía y barra de título oscura/clara.</summary>
public class StudioWindow : Window
{
    public bool HideFromCapture { get; set; }

    public StudioWindow()
    {
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        FontSize = 13;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/FranCaptureStudio;component/Assets/app.ico")); } catch { }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Studio.ThemeWindow(this);
        if (HideFromCapture) Native.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
    }
}

public static class Ui
{
    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).Handle;

    /// <summary>Coloca una ventana en coordenadas físicas de pantalla (independiente del DPI).</summary>
    public static void MovePhysical(Window w, int x, int y)
    {
        var h = Handle(w);
        if (h != IntPtr.Zero) Native.SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    public static SD.Rectangle PhysicalBounds(Window w)
    {
        Native.GetWindowRect(Handle(w), out var r);
        return new SD.Rectangle(r.Left, r.Top, r.Width, r.Height);
    }

    public static SD.Rectangle MonitorAt(SD.Point p) => System.Windows.Forms.Screen.FromPoint(p).Bounds;
    public static SD.Rectangle WorkAreaAt(SD.Point p) => System.Windows.Forms.Screen.FromPoint(p).WorkingArea;

    public static SD.Point CursorPos()
    {
        Native.GetCursorPos(out var p);
        return new SD.Point(p.X, p.Y);
    }

    /// <summary>Centra la ventana (ya mostrada) dentro de un rectángulo físico; fx/fy ∈ [0,1] ajustan la posición.</summary>
    public static void PlaceIn(Window w, SD.Rectangle area, double fx = 0.5, double fy = 0.5)
    {
        var b = PhysicalBounds(w);
        int x = area.X + (int)((area.Width - b.Width) * fx), y = area.Y + (int)((area.Height - b.Height) * fy);
        MovePhysical(w, Math.Max(area.X, x), Math.Max(area.Y, y));
    }

    public static bool Confirm(string message, string? title = null, Window? owner = null) =>
        MessageBox.Show(owner ?? ActiveWindow(), Loc.T(message), Loc.T(title ?? "Fran Capture Studio"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public static void Info(string message, Window? owner = null) =>
        Show(message, MessageBoxImage.Information, owner);

    public static void Error(string message, Window? owner = null) =>
        Show(message, MessageBoxImage.Error, owner);

    static void Show(string message, MessageBoxImage icon, Window? owner)
    {
        owner ??= ActiveWindow();
        if (owner != null) MessageBox.Show(owner, Loc.T(message), "Fran Capture Studio", MessageBoxButton.OK, icon);
        else MessageBox.Show(Loc.T(message), "Fran Capture Studio", MessageBoxButton.OK, icon);
    }

    static Window? ActiveWindow() => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible);

    /// <summary>Diálogo simple de texto.</summary>
    public static string? Prompt(string title, string label, string initial = "", Window? owner = null)
    {
        var w = new StudioWindow
        {
            Title = Loc.T(title), Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen, Owner = owner, ShowInTaskbar = false,
        };
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 8, 0, 14) };
        var ok = new Button { Content = Loc.T("Aceptar"), IsDefault = true, MinWidth = 90, Style = (Style)Application.Current.Resources["PrimaryButton"] };
        var cancel = new Button { Content = Loc.T("Cancelar"), IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), Style = (Style)Application.Current.Resources["SecondaryButton"] };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; w.DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        w.Content = new StackPanel { Margin = new Thickness(18), Children = { new TextBlock { Text = Loc.T(label), TextWrapping = TextWrapping.Wrap }, box, buttons } };
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? result : null;
    }

    public static Color? PickColor(Color initial)
    {
        using var dlg = new System.Windows.Forms.ColorDialog { Color = SD.Color.FromArgb(initial.R, initial.G, initial.B), FullOpen = true };
        return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? Color.FromRgb(dlg.Color.R, dlg.Color.G, dlg.Color.B) : null;
    }

    public static string? PickFolder(string? initial, string title)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T(title), InitialDirectory = initial ?? "" };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public static string? OpenImageFile(Window? owner = null)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Abrir imagen"), Filter = Services.Img.OpenFilter };
        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    public static Style S(string key) => (Style)Application.Current.Resources[key];
    public static Brush B(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>Cuenta regresiva a pantalla (no aparece en las capturas).</summary>
public sealed class CountdownWindow : Window
{
    readonly TextBlock _num;

    CountdownWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
        ShowInTaskbar = false; ShowActivated = false; Width = 190; Height = 190; ResizeMode = ResizeMode.NoResize; IsHitTestVisible = false;
        _num = new TextBlock { FontSize = 96, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 17, 19, 23)), CornerRadius = new CornerRadius(95),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0, 184, 255)), BorderThickness = new Thickness(4), Child = _num,
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = Ui.Handle(this);
        Native.ExcludeFromCapture(h);
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
    }

    public static async Task RunAsync(int seconds, SD.Rectangle? area = null, CancellationToken ct = default)
    {
        if (seconds <= 0) return;
        var w = new CountdownWindow();
        w.Show();
        Ui.PlaceIn(w, area ?? Ui.MonitorAt(Ui.CursorPos()));
        try
        {
            for (int i = seconds; i > 0; i--)
            {
                w._num.Text = i.ToString();
                await Task.Delay(1000, ct);
            }
        }
        finally { w.Close(); }
        await Task.Delay(120, CancellationToken.None); // dar tiempo a que desaparezca de la pantalla
    }
}

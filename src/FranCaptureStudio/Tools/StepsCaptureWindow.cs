using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FranCapture.Core.Localization;
using FranCapture.Core.Storage;
using FranCaptureStudio.Capture;
using FranCaptureStudio.Editor;
using FranCaptureStudio.Services;
using FranCaptureStudio.Views;
using SD = System.Drawing;

namespace FranCaptureStudio.Tools;

/// <summary>Captura con pasos: varias capturas consecutivas numeradas automáticamente para elaborar instrucciones.</summary>
public sealed class StepsCaptureWindow : StudioWindow
{
    readonly List<BitmapSource> _steps = [];
    readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0), FontWeight = FontWeights.SemiBold };
    SD.Rectangle? _lastRegion;
    readonly Button _same;

    public StepsCaptureWindow()
    {
        Title = Loc.T("Captura con pasos"); SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize; Topmost = true; HideFromCapture = true; ShowInTaskbar = true;
        _same = W.Btn("Misma región", () => Capture(sameRegion: true));
        _same.IsEnabled = false;
        var row1 = new StackPanel { Orientation = Orientation.Horizontal };
        row1.Children.Add(_count);
        row1.Children.Add(W.Btn("Capturar paso", () => Capture(false), "PrimaryButton"));
        row1.Children.Add(_same);
        row1.Children.Add(W.Btn("Deshacer último", () => { if (_steps.Count > 0) { _steps.RemoveAt(_steps.Count - 1); Refresh(); } }));
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row2.Children.Add(W.Btn("Terminar y guardar", () => Finish(FinishMode.Save)));
        row2.Children.Add(W.Btn("Terminar y combinar", () => Finish(FinishMode.Combine)));
        row2.Children.Add(W.Btn("Terminar y crear reporte", () => Finish(FinishMode.Report)));
        var hint = new TextBlock { Text = Loc.T("Esta ventana no aparece en las capturas. Cada paso recibe su número automáticamente."), Style = Ui.S("Muted"), Margin = new Thickness(0, 10, 0, 0), MaxWidth = 520 };
        Content = new StackPanel { Margin = new Thickness(16), Children = { row1, row2, hint } };
        Refresh();
        Loaded += (_, _) => Ui.PlaceIn(this, Ui.WorkAreaAt(Ui.CursorPos()), 0.98, 0.04);
    }

    void Refresh()
    {
        _count.Text = Loc.T("Pasos: {0}", _steps.Count);
        _same.IsEnabled = _lastRegion != null;
    }

    /// <summary>Añade la insignia numerada en la esquina superior izquierda.</summary>
    public static BitmapSource Number(BitmapSource image, int number)
    {
        double size = Math.Clamp(Math.Min(image.PixelWidth, image.PixelHeight) / 14.0, 14, 34);
        var ann = new Annotation { Kind = AnnKind.Step, Number = number, FontSize = size, Color = Color.FromRgb(0, 184, 255) };
        double d = ann.StepDiameter;
        ann.A = new Point(d / 2 + 8, d / 2 + 8);
        return new AnnotationRenderer().Flatten(new EditorState { Base = image, Annotations = [ann] });
    }

    void Capture(bool sameRegion)
    {
        BitmapSource? img = null;
        if (sameRegion && _lastRegion is { } r)
        {
            using var bmp = ScreenCapture.Capture(r);
            img = Img.FromGdi(bmp);
        }
        else
        {
            using var res = OverlayForm.Show(new OverlayOptions { Purpose = OverlayPurpose.SelectOnly, AcceptLabel = "Capturar paso" });
            if (res.Image != null) { img = Img.FromGdi(res.Image); _lastRegion = res.ScreenRect; }
        }
        if (img == null) return;
        _steps.Add(Number(img, _steps.Count + 1));
        if (Studio.Settings.Sounds) Sounds.Shutter();
        Refresh();
    }

    enum FinishMode { Save, Combine, Report }

    void Finish(FinishMode mode)
    {
        if (_steps.Count == 0) { Close(); return; }
        var paths = new List<string>();
        try
        {
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            for (int i = 0; i < _steps.Count; i++)
            {
                var path = FileNaming.UniquePath(Studio.Storage.CapturesFolder, $"Paso_{stamp}_{i + 1:00}", Studio.Settings.ImageFormat);
                Studio.Storage.SaveImage(_steps[i], path);
                Studio.Library.AddFile(path, notify: false);
                paths.Add(path);
            }
            Studio.Library.RaiseChanged();
        }
        catch (Exception ex) { Ui.Error(Loc.T("No se pudieron guardar los pasos:") + "\n" + ex.Message, this); return; }
        var steps = _steps.ToList();
        Close();
        switch (mode)
        {
            case FinishMode.Combine: new CombineWindow(steps).Show(); break;
            case FinishMode.Report:
                Studio.ShowMain("qa");
                foreach (var p in paths) Studio.Main?.QaPage.AddAttachment(p);
                break;
            default:
                Studio.Notify(Loc.T("Pasos guardados"), Loc.T("{0} capturas numeradas en la carpeta de capturas.", paths.Count), () => Shell.RevealInExplorer(paths[0]));
                break;
        }
    }
}

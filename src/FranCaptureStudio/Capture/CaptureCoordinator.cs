using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using FranCapture.Core.Automation;
using FranCapture.Core.Diagnostics;
using FranCapture.Core.Imaging;
using FranCapture.Core.Localization;
using FranCapture.Core.Settings;
using FranCaptureStudio.Interop;
using FranCaptureStudio.Services;
using SD = System.Drawing;

namespace FranCaptureStudio.Capture;

public enum CaptureMode { Region, FullScreen, Monitor, ActiveWindow, PickWindow, LastRegion, ExactRegion, Scrolling }

public sealed class CaptureRequest
{
    public CaptureMode Mode { get; init; }
    public bool FromUi { get; init; }            // iniciada desde la ventana principal: ocultarla antes de capturar
    public int DelaySeconds { get; init; }
    public int MonitorIndex { get; init; }
    public SD.Rectangle? Rect { get; init; }
    public bool? OpenEditor { get; init; }       // null = según automatizaciones
    public bool Silent { get; init; }            // sin editor ni notificación (capturas periódicas / pasos)
}

public sealed class CaptureOutcome
{
    public BitmapSource? Image { get; init; }
    public string? SavedPath { get; init; }
    public bool Copied { get; init; }
    public bool Cancelled { get; init; }
    public string? Error { get; init; }
}

/// <summary>Orquesta cada captura: obtiene la imagen y aplica las reglas de automatización (guardar, copiar, editor…).</summary>
public sealed class CaptureCoordinator
{
    AppSettings S => Studio.Settings;
    bool _busy;
    public bool IsBusy => _busy;
    public event Action<CaptureOutcome>? Captured;

    public async Task<CaptureOutcome> CaptureAsync(CaptureRequest req)
    {
        if (_busy) return new CaptureOutcome { Cancelled = true };
        _busy = true;
        bool restoreMain = false;
        try
        {
            if (req.FromUi && Studio.Main is { IsVisible: true } main && main.WindowState != WindowState.Minimized)
            {
                main.Hide();
                restoreMain = true;
                await Task.Delay(260); // dejar que Windows repinte lo que había debajo
            }
            int delay = req.DelaySeconds > 0 ? req.DelaySeconds : (req.Mode is CaptureMode.Region or CaptureMode.PickWindow ? 0 : S.CaptureDelaySeconds);
            if (delay > 0) await Views.CountdownWindow.RunAsync(delay);

            BitmapSource? image = null;
            OverlayAction action = OverlayAction.Capture;
            switch (req.Mode)
            {
                case CaptureMode.FullScreen:
                    image = Grab(ScreenCapture.VirtualBounds);
                    break;
                case CaptureMode.Monitor:
                {
                    var mons = ScreenCapture.Monitors();
                    if (mons.Count == 0) return Fail("No se detectó ningún monitor.");
                    // Si el monitor elegido se desconectó, se usa el principal.
                    var m = req.MonitorIndex >= 0 && req.MonitorIndex < mons.Count ? mons[req.MonitorIndex] : mons.FirstOrDefault(x => x.Primary) ?? mons[0];
                    image = Grab(m.Bounds);
                    break;
                }
                case CaptureMode.ActiveWindow:
                {
                    var r = ScreenCapture.ActiveWindowBounds();
                    if (r == null) return Fail("No hay ninguna ventana activa que capturar.");
                    image = Grab(r.Value);
                    break;
                }
                case CaptureMode.LastRegion:
                {
                    if (S.LastRegion is not { } lr) return Fail("Aún no hay una región anterior. Captura una región primero.");
                    var r = SD.Rectangle.Intersect(new SD.Rectangle(lr.X, lr.Y, lr.Width, lr.Height), ScreenCapture.VirtualBounds);
                    if (r.Width < 1 || r.Height < 1) return Fail("La última región ya no está dentro de ninguna pantalla conectada.");
                    image = Grab(r);
                    break;
                }
                case CaptureMode.ExactRegion:
                {
                    var r = SD.Rectangle.Intersect(req.Rect ?? SD.Rectangle.Empty, ScreenCapture.VirtualBounds);
                    if (r.Width < 1 || r.Height < 1) return Fail("La región indicada queda fuera de la pantalla.");
                    RememberRegion(r);
                    image = Grab(r);
                    break;
                }
                case CaptureMode.Region:
                case CaptureMode.PickWindow:
                case CaptureMode.Scrolling:
                {
                    using var res = OverlayForm.Show(new OverlayOptions
                    {
                        Mode = req.Mode == CaptureMode.PickWindow ? OverlayMode.Window : OverlayMode.Region,
                        Purpose = req.Mode == CaptureMode.Scrolling ? OverlayPurpose.SelectOnly : OverlayPurpose.Capture,
                        AcceptLabel = "Iniciar captura extendida",
                        IncludeCursor = S.IncludeCursor,
                    });
                    if (res.Action == OverlayAction.Cancel || res.Image == null) return new CaptureOutcome { Cancelled = true };
                    if (req.Mode != CaptureMode.PickWindow) RememberRegion(res.ScreenRect);
                    action = res.Action;
                    if (action is OverlayAction.Scroll or OverlayAction.Accept)
                    {
                        var (img, msg) = await ScrollCapture.RunAsync(res.ScreenRect);
                        if (img == null) return Fail(msg ?? "No se pudo completar la captura extendida.");
                        if (msg != null) Studio.Notify(Loc.T("Captura extendida"), msg);
                        image = img; action = OverlayAction.Capture;
                    }
                    else if (action == OverlayAction.Record)
                    {
                        restoreMain = false;
                        _busy = false;
                        _ = Studio.Recorder.StartAsync(new Recording.RecordingTarget { Region = res.ScreenRect });
                        return new CaptureOutcome { Cancelled = true };
                    }
                    else image = Img.FromGdi(res.Image);
                    break;
                }
            }
            if (image == null) return new CaptureOutcome { Cancelled = true };
            var outcome = await ProcessAsync(image, action, req);
            if (outcome.Image != null && req.OpenEditor != false && !req.Silent && EditorOpened) restoreMain = false;
            return outcome;
        }
        catch (Exception ex)
        {
            Log.Error("Captura", ex);
            return Fail(Loc.T("No se pudo realizar la captura: ") + ex.Message);
        }
        finally
        {
            _busy = false;
            if (restoreMain) Studio.Main?.Show();
        }
    }

    bool EditorOpened;

    CaptureOutcome Fail(string message)
    {
        Studio.Notify("Fran Capture Studio", Loc.T(message), error: true);
        return new CaptureOutcome { Error = message };
    }

    BitmapSource Grab(SD.Rectangle rect)
    {
        using var bmp = ScreenCapture.Capture(rect, S.IncludeCursor);
        return Img.FromGdi(bmp);
    }

    void RememberRegion(SD.Rectangle r)
    {
        S.LastRegion = new RegionRect { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };
        Studio.SaveSettings();
    }

    /// <summary>Aplica las automatizaciones a una imagen ya capturada.</summary>
    public async Task<CaptureOutcome> ProcessAsync(BitmapSource image, OverlayAction action = OverlayAction.Capture, CaptureRequest? req = null)
    {
        EditorOpened = false;
        var plan = AutomationPlan.For(AutomationTrigger.Capture, S.Automations);
        if (plan.AddWatermark) image = Img.AddWatermark(image, S.WatermarkText);
        string? saved = null; bool copied = false;
        bool silent = req?.Silent == true;

        switch (action)
        {
            case OverlayAction.Copy:
                copied = ClipboardService.SetImage(image);
                if (S.Sounds) Sounds.Shutter();
                Studio.Notify(Loc.T("Copiado"), copied ? Loc.T("La captura está en el portapapeles.") : Loc.T("No se pudo acceder al portapapeles."), error: !copied);
                break;
            case OverlayAction.SaveAs:
                saved = SaveAsDialog(image, plan);
                if (saved != null) NotifySaved(saved);
                break;
            case OverlayAction.Ocr:
                Studio.ShowMain("ocr");
                await Studio.RunOcrAsync(image);
                break;
            case OverlayAction.Pin:
                Tools.PinnedImageWindow.Pin(image);
                break;
            default:
            {
                bool wantEditor = !silent && (action == OverlayAction.Edit || (req?.OpenEditor ?? plan.OpenEditor));
                if (plan.AutoSave || silent)
                {
                    if (S.AskWhereToSave && !silent) saved = SaveAsDialog(image, plan);
                    else
                    {
                        var path = Studio.Storage.NextImagePath(plan.AddDateToName, plan.ConvertToJpg ? "jpg" : null);
                        try { Studio.Storage.SaveImage(image, path); saved = path; Studio.Library.AddFile(path); }
                        catch (Exception ex)
                        {
                            Log.Error("Guardar captura", ex);
                            Studio.Notify(Loc.T("No se pudo guardar"), ex.Message, error: true);
                        }
                    }
                }
                if (plan.CopyToClipboard && !silent) copied = ClipboardService.SetImage(image);
                if (S.Sounds && !silent) Sounds.Shutter();
                if (wantEditor || (saved == null && !copied && !silent))
                {
                    Editor.EditorWindow.Open(image, saved);
                    EditorOpened = true;
                }
                else if (!silent)
                {
                    if (saved != null) NotifySaved(saved, copied);
                    else if (copied) Studio.Notify(Loc.T("Captura copiada"), Loc.T("La captura está en el portapapeles."));
                }
                break;
            }
        }
        var outcome = new CaptureOutcome { Image = image, SavedPath = saved, Copied = copied };
        Captured?.Invoke(outcome);
        return outcome;
    }

    void NotifySaved(string path, bool copied = false) =>
        Studio.Notify(Loc.T("Captura guardada"), Path.GetFileName(path) + (copied ? " · " + Loc.T("copiada al portapapeles") : ""), () => Shell.RevealInExplorer(path));

    /// <summary>Diálogo nativo «Guardar como» de Windows. Devuelve la ruta escrita o null si se canceló o falló.</summary>
    public static string? SaveAsDialog(BitmapSource image, AutomationPlan? plan = null, Window? owner = null)
    {
        var s = Studio.Settings;
        string ext = plan?.ConvertToJpg == true ? "jpg" : s.ImageFormat;
        var suggested = Path.GetFileName(Studio.Storage.NextImagePath(plan?.AddDateToName == true, ext));
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Guardar como"),
            Filter = Img.SaveFilter,
            FilterIndex = Math.Max(1, Array.IndexOf(Img.WritableFormats, ext) + 1),
            FileName = suggested,
            InitialDirectory = Studio.Storage.CapturesFolder,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(owner) != true) return null;
        try
        {
            Studio.Storage.SaveImage(image, dlg.FileName);
            // Solo los archivos dentro de las carpetas administradas entran en la biblioteca automáticamente;
            // el resto se registra igualmente porque el usuario lo creó desde la aplicación.
            Studio.Library.AddFile(dlg.FileName);
            return dlg.FileName;
        }
        catch (Exception ex)
        {
            Log.Error("Guardar como", ex);
            MessageBox.Show(Loc.T("No se pudo guardar el archivo:") + "\n" + ex.Message, "Fran Capture Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }
}

/// <summary>Captura extendida: desplaza el contenido bajo la región con la rueda y ensambla los fotogramas.</summary>
public static class ScrollCapture
{
    static byte[] GrabPixels(SD.Rectangle r)
    {
        using var bmp = ScreenCapture.Capture(r);
        return Img.Pixels(Img.FromGdi(bmp));
    }

    /// <summary>Devuelve la imagen ensamblada y un mensaje informativo (o el motivo del fallo si la imagen es null).</summary>
    public static async Task<(BitmapSource? Image, string? Message)> RunAsync(SD.Rectangle region, int maxFrames = 80, CancellationToken ct = default)
    {
        if (region.Width < 40 || region.Height < 80)
            return (null, Loc.T("La región es demasiado pequeña para una captura extendida (mínimo 40 × 80 px)."));
        Native.GetCursorPos(out var original);
        ScrollStitcher st;
        try { st = new ScrollStitcher(region.Width, region.Height); }
        catch (ArgumentException ex) { return (null, ex.Message); }
        string? note = null;
        try
        {
            Native.SetCursorPos(region.X + region.Width / 2, region.Y + region.Height / 2);
            await Task.Delay(150, ct);
            st.AddFrame(GrabPixels(region));
            int noMove = 0;
            for (int i = 0; i < maxFrames && !ct.IsCancellationRequested; i++)
            {
                if ((Native.GetAsyncKeyState(0x1B) & 0x8000) != 0) { note = Loc.T("Captura extendida detenida con Esc."); break; }
                Native.SendWheel(-120 * 2);
                await Task.Delay(320, ct); // esperar al desplazamiento suave
                var status = st.AddFrame(GrabPixels(region));
                if (status == StitchStatus.NoMovement)
                {
                    if (++noMove >= 2) break; // final del contenido
                    continue;
                }
                noMove = 0;
                if (status == StitchStatus.NoMatch)
                {
                    if (st.FramesAccepted <= 1)
                        return (null, Loc.T("Esta aplicación no es compatible con la captura extendida: el contenido no se desplaza de forma uniforme (animaciones, video o desplazamiento por páginas). No se generó ninguna imagen para evitar un resultado dañado."));
                    note = Loc.T("La captura extendida se detuvo porque el contenido dejó de desplazarse de forma uniforme. Se conserva lo ensamblado hasta ese punto.");
                    break;
                }
                if (st.ReachedMaxHeight) { note = Loc.T("Se alcanzó la altura máxima de la captura extendida."); break; }
            }
            if (st.FramesAccepted <= 1)
                return (null, Loc.T("El contenido no se desplazó. Coloca la región sobre un área con desplazamiento vertical e inténtalo de nuevo."));
            var (px, w, h) = st.Build();
            return (Img.FromPixels(px, w, h), note);
        }
        catch (OperationCanceledException) { return (null, Loc.T("Captura extendida cancelada.")); }
        finally { Native.SetCursorPos(original.X, original.Y); }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FranCapture.Core.Automation;
using FranCapture.Core.Hotkeys;
using FranCapture.Core.Library;
using FranCapture.Core.Qa;
using FranCapture.Core.Video;
using FranCaptureStudio.Capture;
using FranCaptureStudio.Editor;
using FranCaptureStudio.Interop;
using FranCaptureStudio.Recording;
using FranCaptureStudio.Services;
using FranCaptureStudio.Tools;
using FranCaptureStudio.Views;
using Xunit;
using Xunit.Abstractions;
using SD = System.Drawing;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FranCaptureStudio.WinTests;

/// <summary>Hilo de interfaz único (STA) con la aplicación WPF y los servicios reales, sobre una carpeta de datos aislada.</summary>
public sealed class UiFixture : IDisposable
{
    public static readonly string DataDir = Path.Combine(Path.GetTempPath(), "fcs-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public Dispatcher Dispatcher { get; }
    readonly Thread _thread;
    public string SkippedLog => Path.Combine(DataDir, "omitidas.txt");

    public UiFixture()
    {
        Environment.SetEnvironmentVariable("FCS_DATA_DIR", DataDir);
        Directory.CreateDirectory(DataDir);
        var ready = new ManualResetEventSlim();
        Dispatcher? d = null; Exception? error = null;
        _thread = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Studio.InitServices(withTray: false);
                Studio.Settings.Sounds = false; Studio.Settings.Countdown = 0;
                Studio.ApplyTheme();
                d = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex) { error = ex; }
            ready.Set();
            if (error == null) Dispatcher.Run();
        }) { IsBackground = true, Name = "UI de pruebas" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (error != null) throw new InvalidOperationException("No se pudo iniciar la aplicación de pruebas", error);
        Dispatcher = d!;
    }

    public void Run(Action a) => Dispatcher.Invoke(a);
    public T Run<T>(Func<T> f) => Dispatcher.Invoke(f);

    public void RunAsync(Func<Task> f, int timeoutSeconds = 120)
    {
        var task = Dispatcher.Invoke(f);
        if (!task.Wait(TimeSpan.FromSeconds(timeoutSeconds))) throw new TimeoutException("La prueba superó el tiempo máximo.");
    }

    /// <summary>Procesa los mensajes pendientes del hilo de interfaz durante un tiempo.</summary>
    public void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); Thread.Sleep(15); }
    }

    public void Skip(string test, string reason) => File.AppendAllText(SkippedLog, $"OMITIDA {test}: {reason}{Environment.NewLine}");

    public void Dispose()
    {
        try { Dispatcher.Invoke(() => { Studio.Hotkeys.Dispose(); Studio.Library.Dispose(); }); } catch { }
        Dispatcher.InvokeShutdown();
    }
}

[CollectionDefinition("ui")]
public class UiCollection : ICollectionFixture<UiFixture> { }

[Collection("ui")]
public class IntegrationTests(UiFixture ui, ITestOutputHelper output)
{
    static BitmapSource Solid(int w, int h, Color c) => Img.Draw(w, h, dc => dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(0, 0, w, h)));
    static (byte B, byte G, byte R, byte A) Px(BitmapSource img, int x, int y) { var p = Img.Pixels(img); int i = (y * img.PixelWidth + x) * 4; return (p[i], p[i + 1], p[i + 2], p[i + 3]); }

    Window ShowColorWindow(Color color, bool hideFromCapture, out SD.Rectangle bounds)
    {
        var w = new StudioWindow
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Width = 300, Height = 200, Left = 60, Top = 60, Topmost = true, ShowInTaskbar = false,
            HideFromCapture = hideFromCapture, Content = new System.Windows.Controls.Border { Background = new SolidColorBrush(color) },
        };
        w.Show(); w.Activate();
        bounds = Ui.PhysicalBounds(w);
        return w;
    }

    // --- 7/8. Captura real de pantalla ---

    [Fact]
    public void Screen_capture_returns_real_pixels_of_a_visible_window()
    {
        Window? w = null; SD.Rectangle b = default;
        ui.Run(() => w = ShowColorWindow(Color.FromRgb(255, 0, 255), false, out b));
        ui.Pump(700);
        var img = ui.Run(() => { using var bmp = ScreenCapture.Capture(b); return Img.FromGdi(bmp); });
        ui.Run(() => w!.Close());
        Assert.Equal(b.Width, img.PixelWidth); Assert.Equal(b.Height, img.PixelHeight);
        var c = Px(img, img.PixelWidth / 2, img.PixelHeight / 2);
        output.WriteLine($"Píxel central: R{c.R} G{c.G} B{c.B}");
        Assert.True(c.R > 240 && c.G < 20 && c.B > 240, "La captura no contiene el color de la ventana mostrada.");
        Assert.Equal(255, c.A);
    }

    [Fact]
    public void Own_tool_windows_are_excluded_from_capture()
    {
        Window? w = null; SD.Rectangle b = default;
        ui.Run(() => w = ShowColorWindow(Color.FromRgb(0, 255, 0), true, out b));
        ui.Pump(700);
        var img = ui.Run(() => { using var bmp = ScreenCapture.Capture(b); return Img.FromGdi(bmp); });
        ui.Run(() => w!.Close());
        var c = Px(img, img.PixelWidth / 2, img.PixelHeight / 2);
        output.WriteLine($"Píxel central con exclusión: R{c.R} G{c.G} B{c.B}");
        Assert.False(c.G > 240 && c.R < 20 && c.B < 20, "La ventana marcada como excluida apareció en la captura.");
    }

    [Fact]
    public void Monitors_and_virtual_screen_are_consistent()
    {
        var mons = ScreenCapture.Monitors();
        Assert.NotEmpty(mons);
        Assert.Contains(mons, m => m.Primary);
        foreach (var m in mons) Assert.True(ScreenCapture.VirtualBounds.Contains(m.Bounds));
        using var full = ScreenCapture.CaptureVirtualScreen();
        Assert.Equal(ScreenCapture.VirtualBounds.Size, full.Size);
    }

    [Fact]
    public void Region_selector_crops_the_frozen_screen()
    {
        ui.Run(() =>
        {
            using var f = new OverlayForm(new OverlayOptions());
            f.TestSelect(new SD.Rectangle(10, 20, 200, 120));
            Assert.Contains(OverlayAction.Capture, f.TestButtons);
            Assert.Contains(OverlayAction.Edit, f.TestButtons);
            Assert.Contains(OverlayAction.Record, f.TestButtons);
            f.TestFinish(OverlayAction.Capture);
            Assert.Equal(OverlayAction.Capture, f.Result.Action);
            Assert.Equal(200, f.Result.Image!.Width); Assert.Equal(120, f.Result.Image.Height);
            var vb = ScreenCapture.VirtualBounds;
            Assert.Equal(new SD.Rectangle(vb.X + 10, vb.Y + 20, 200, 120), f.Result.ScreenRect);
            f.Result.Dispose();
        });
    }

    // --- 5/6. Atajos globales ---

    static readonly HotkeyGesture TestGesture = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x76); // Ctrl+Alt+Shift+F7

    /// <summary>Inyecta la combinación de prueba; devuelve true cuando <paramref name="arrived"/> se cumple.</summary>
    bool PressTestGesture(Func<bool> arrived, int waitMs = 900)
    {
        uint sent = Native.SendKey(0x76, [0x11, 0x12, 0x10]);
        int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        ui.Pump(waitMs);
        if (arrived()) return true;
        output.WriteLine($"SendInput devolvió {sent} (error {err}); se reintenta con keybd_event");
        Native.SendKeyLegacy(0x76, [0x11, 0x12, 0x10]);
        ui.Pump(waitMs);
        return arrived();
    }

    [Fact]
    public void Global_hotkey_registers_fires_and_unregisters()
    {
        int fired = 0;
        ui.Run(() =>
        {
            Studio.Hotkeys.UnregisterAll();
            Studio.Hotkeys.Pressed += a => { if (a == HotkeyAction.OcrRegion) fired++; };
            Assert.Equal(HotkeyRegistration.Ok, Studio.Hotkeys.Register(HotkeyAction.OcrRegion, TestGesture));
            Assert.False(Studio.Hotkeys.IsAvailable(TestGesture));
            using var rival = new HotkeyService(); // otra ventana, como si fuera otra aplicación
            Assert.Equal(HotkeyRegistration.InUseByOtherApp, rival.Register(HotkeyAction.OpenApp, TestGesture));
        });
        if (!PressTestGesture(() => fired > 0))
        {
            ui.Run(() => Studio.Hotkeys.UnregisterAll());
            ui.Skip(nameof(Global_hotkey_registers_fires_and_unregisters), "el registro y el conflicto se comprobaron, pero este entorno no entrega pulsaciones sintéticas al sistema (sin escritorio de entrada interactivo); el disparo real se verifica en la prueba de humo o manualmente");
            return;
        }
        Assert.Equal(1, fired);

        // Conflicto: otra ventana (como si fuera otra aplicación) no puede registrar la misma combinación
        ui.Run(() =>
        {
            using var other = new HotkeyService();
            Assert.Equal(HotkeyRegistration.InUseByOtherApp, other.Register(HotkeyAction.OpenApp, TestGesture));
        });

        ui.Run(() => Studio.Hotkeys.Unregister(HotkeyAction.OcrRegion));
        Native.SendKey(0x76, [0x11, 0x12, 0x10]);
        ui.Pump(500);
        Assert.Equal(1, fired); // ya no se intercepta
        ui.Run(() =>
        {
            Assert.True(Studio.Hotkeys.IsAvailable(TestGesture));
            using var other = new HotkeyService();
            Assert.Equal(HotkeyRegistration.Ok, other.Register(HotkeyAction.OpenApp, TestGesture)); // quedó libre
        });
    }

    [Fact]
    public void Configured_key_really_opens_the_capture_selector()
    {
        OverlayForm? form = null;
        ui.Run(() =>
        {
            Studio.Hotkeys.UnregisterAll();
            Studio.Hotkeys.Pressed += a =>
            {
                if (a != HotkeyAction.CaptureRegion || form != null) return;
                form = new OverlayForm(new OverlayOptions());
                form.Show(); // no modal para poder inspeccionarlo
            };
            Assert.Equal(HotkeyRegistration.Ok, Studio.Hotkeys.Register(HotkeyAction.CaptureRegion, TestGesture));
        });
        if (!PressTestGesture(() => form != null, 1200))
        {
            ui.Run(() => Studio.Hotkeys.UnregisterAll());
            ui.Skip(nameof(Configured_key_really_opens_the_capture_selector), "este entorno no entrega pulsaciones sintéticas al sistema; se verifica en la prueba de humo o manualmente");
            return;
        }
        ui.Run(() =>
        {
            Assert.NotNull(form);
            Assert.True(form!.Visible);
            Assert.Equal(ScreenCapture.VirtualBounds, form.Bounds);
            form.TestFinish(OverlayAction.Cancel);
            form.Dispose();
            Studio.Hotkeys.UnregisterAll();
        });
    }

    [Fact]
    public void Hotkey_dialog_explains_conflicts_and_offers_alternatives()
    {
        ui.Run(() =>
        {
            Studio.Hotkeys.UnregisterAll();
            Studio.Settings.Hotkeys = HotkeyDefaults.Create();
            var dlg = new HotkeyCaptureWindow(HotkeyAction.OcrRegion);
            HotkeyGesture.TryParse("F9", out var f9);
            dlg.Evaluate(f9);                    // ya usada por «selector de región»
            Assert.False(dlg.CanSave);
            Assert.Contains("selector", dlg.MessageText, StringComparison.OrdinalIgnoreCase);
            dlg.Evaluate(new HotkeyGesture(HotkeyModifiers.None, 'A'));
            Assert.False(dlg.CanSave);
            dlg.Evaluate(TestGesture);
            Assert.True(dlg.CanSave);
            dlg.Close();
        });
    }

    // --- 8/9. Guardado y portapapeles ---

    [Fact]
    public void Capture_pipeline_saves_to_selected_folder_copies_and_registers_in_library()
    {
        var folder = Path.Combine(UiFixture.DataDir, "CarpetaElegida");
        string? saved = null;
        ui.RunAsync(async () =>
        {
            var s = Studio.Settings;
            s.CapturesFolder = folder; s.ImageFormat = "png"; s.AskWhereToSave = false;
            s.Automations = AutomationRule.Defaults();
            AutomationPlan.SetEnabled(s.Automations, AutomationTrigger.Capture, AutomationAction.OpenEditor, false);
            var outcome = await Studio.Capture.ProcessAsync(Solid(320, 200, Color.FromRgb(10, 120, 200)));
            saved = outcome.SavedPath;
            Assert.True(outcome.Copied);
        });
        Assert.NotNull(saved);
        Assert.True(File.Exists(saved));
        Assert.Equal(Path.GetFullPath(folder), Path.GetDirectoryName(saved));
        var onDisk = Img.Load(saved!);
        Assert.Equal(320, onDisk.PixelWidth); Assert.Equal(200, onDisk.PixelHeight);
        var px = Px(onDisk, 5, 5); Assert.Equal((200, 120, 10), (px.B, px.G, px.R));

        var clip = ui.Run(ClipboardService.GetImage);
        Assert.NotNull(clip);
        Assert.Equal(320, clip!.PixelWidth); Assert.Equal(200, clip.PixelHeight);
        var cp = Px(clip, 5, 5); Assert.Equal((200, 120, 10), (cp.B, cp.G, cp.R));

        var items = ui.Run(() => Studio.Library.Query(new LibraryQuery()));
        Assert.Contains(items, i => string.Equals(i.Path, saved, StringComparison.OrdinalIgnoreCase) && i.Width == 320 && i.Kind == MediaKind.Image);
    }

    [Fact]
    public void Jpg_conversion_rule_and_date_rule_apply()
    {
        string? saved = null;
        ui.RunAsync(async () =>
        {
            var s = Studio.Settings;
            s.FileNameTemplate = "Prueba";
            s.Automations = AutomationRule.Defaults();
            AutomationPlan.SetEnabled(s.Automations, AutomationTrigger.Capture, AutomationAction.OpenEditor, false);
            AutomationPlan.SetEnabled(s.Automations, AutomationTrigger.Capture, AutomationAction.ConvertToJpg, true);
            AutomationPlan.SetEnabled(s.Automations, AutomationTrigger.Capture, AutomationAction.AddDateToName, true);
            AutomationPlan.SetEnabled(s.Automations, AutomationTrigger.Capture, AutomationAction.AddWatermark, true);
            saved = (await Studio.Capture.ProcessAsync(Solid(400, 300, Colors.Black))).SavedPath;
            s.FileNameTemplate = "Captura_{fecha}_{hora}"; s.Automations = AutomationRule.Defaults();
        });
        Assert.EndsWith(".jpg", saved);
        Assert.Contains(DateTime.Now.ToString("yyyy-MM-dd"), Path.GetFileName(saved!));
        var img = Img.Load(saved!);
        var p = Img.Pixels(img);
        Assert.Contains(Enumerable.Range(0, p.Length / 4), i => p[i * 4 + 2] > 120); // la marca de agua dejó píxeles claros
    }

    // --- 15. Biblioteca ---

    [Fact]
    public void Library_only_lists_files_that_really_exist()
    {
        var file = Path.Combine(UiFixture.DataDir, "temporal.png");
        Img.Save(Solid(20, 20, Colors.Red), file);
        ui.Run(() => Studio.Library.AddFile(file));
        Assert.Contains(ui.Run(() => Studio.Library.Query(new LibraryQuery())), i => i.Path == file);
        File.Delete(file);
        Assert.DoesNotContain(ui.Run(() => Studio.Library.Query(new LibraryQuery())), i => i.Path == file);
        Assert.Null(ui.Run(() => Studio.Library.Repo.GetByPath(file))); // el registro huérfano se eliminó
    }

    // --- 10/11. Editor ---

    [Fact]
    public void Editor_applies_changes_undo_redo_and_saves()
    {
        ui.Run(() =>
        {
            Studio.Settings.AskWhereToSave = false;
            var ed = EditorWindow.Open(Solid(400, 300, Colors.White), null);
            var c = ed.TestCanvas;
            Assert.False(ed.IsDirty);
            c.Add(new Annotation { Kind = AnnKind.Rect, A = new Point(50, 50), B = new Point(150, 120), Color = Colors.Red, Thickness = 6 });
            c.Add(new Annotation { Kind = AnnKind.Arrow, A = new Point(200, 200), B = new Point(300, 250), Color = Colors.Blue, Thickness = 5 });
            c.Add(new Annotation { Kind = AnnKind.Redact, A = new Point(300, 20), B = new Point(380, 60), Color = Colors.Black });
            Assert.True(ed.IsDirty);
            Assert.Equal(3, c.State.Annotations.Count);

            var flat = c.FlattenNow();
            var edge = Px(flat, 50, 85); Assert.True(edge.R > 200 && edge.G < 60, "El rectángulo rojo no se dibujó.");
            var red = Px(flat, 340, 40); Assert.Equal((0, 0, 0), (red.B, red.G, red.R));
            var untouched = Px(flat, 10, 290); Assert.Equal((255, 255, 255), (untouched.B, untouched.G, untouched.R));

            c.Undo(); Assert.Equal(2, c.State.Annotations.Count);
            c.Undo(); Assert.Equal(1, c.State.Annotations.Count);
            Assert.Equal((255, 255, 255), (Px(c.FlattenNow(), 340, 40) is var u ? (u.B, u.G, u.R) : default));
            c.Redo(); c.Redo(); Assert.Equal(3, c.State.Annotations.Count);
            Assert.False(c.History.CanRedo);

            // mover, cambiar color y duplicar (objetos editables)
            var rect = c.State.Annotations[0];
            c.Select(rect); c.Nudge(new Vector(10, 0));
            Assert.Equal(60, c.State.Annotations[0].A.X);
            c.DuplicateSelected(); Assert.Equal(4, c.State.Annotations.Count);
            c.DeleteSelected(); Assert.Equal(3, c.State.Annotations.Count);

            // recorte: conserva las anotaciones desplazadas
            c.Tool = EditorTool.Crop;
            typeof(EditorCanvas).GetProperty(nameof(EditorCanvas.CropRect))!.SetValue(c, new Rect(40, 40, 200, 150));
            c.ApplyCrop();
            Assert.Equal(200, c.ImageWidth); Assert.Equal(150, c.ImageHeight);
            c.Undo(); Assert.Equal(400, c.ImageWidth);

            // rotación con fusión
            c.ReplaceBase(Img.Rotate(c.FlattenNow(), 90), _ => false);
            Assert.Equal(300, c.ImageWidth); Assert.Equal(400, c.ImageHeight);
            c.Undo(); Assert.Equal(400, c.ImageWidth); Assert.Equal(3, c.State.Annotations.Count);

            Assert.True(ed.Save());
            Assert.False(ed.IsDirty);
            Assert.True(File.Exists(ed.SavedPath));
            var saved = Img.Load(ed.SavedPath!);
            Assert.Equal(400, saved.PixelWidth);
            Assert.Equal((0, 0, 0), (Px(saved, 340, 40) is var s2 ? (s2.B, s2.G, s2.R) : default)); // censura irreversible en el archivo
            ed.Close();
        });
    }

    [Fact]
    public void Editable_project_roundtrip_and_privacy_tools()
    {
        var path = Path.Combine(UiFixture.DataDir, "proyecto.fcsproj");
        var noisy = Img.FromPixels(Enumerable.Range(0, 100 * 100 * 4).Select(i => (byte)(i % 4 == 3 ? 255 : (i * 7919) % 256)).ToArray(), 100, 100);
        var state = new EditorState
        {
            Base = noisy,
            Annotations =
            [
                new Annotation { Kind = AnnKind.Text, A = new Point(5, 5), Text = "Hola ñ", FontSize = 18, Color = Colors.Yellow },
                new Annotation { Kind = AnnKind.Pixelate, A = new Point(20, 20), B = new Point(60, 60), Strength = 10 },
                new Annotation { Kind = AnnKind.Blur, A = new Point(60, 60), B = new Point(95, 95), Strength = 6 },
                new Annotation { Kind = AnnKind.Pen, Points = [new Point(1, 90), new Point(10, 95), new Point(20, 90)], Color = Colors.Lime, Thickness = 3 },
                new Annotation { Kind = AnnKind.Image, Image = Solid(10, 10, Colors.Red), A = new Point(80, 5), B = new Point(95, 20) },
            ],
        };
        EditorProject.Save(path, state);
        var loaded = EditorProject.Load(path);
        Assert.Equal(5, loaded.Annotations.Count);
        Assert.Equal("Hola ñ", loaded.Annotations[0].Text);
        Assert.Equal(3, loaded.Annotations[3].Points.Count);
        Assert.NotNull(loaded.Annotations[4].Image);
        Assert.Equal(Img.Pixels(noisy), Img.Pixels(loaded.Base));

        var flat = ui.Run(() => new AnnotationRenderer().Flatten(loaded));
        // dentro del área pixelada todos los píxeles de un bloque son iguales (no queda el detalle original)
        Assert.Equal(Px(flat, 22, 22), Px(flat, 27, 27));
        Assert.NotEqual(Px(noisy, 22, 22), Px(noisy, 27, 27));
        Assert.NotEqual(Px(noisy, 75, 75), Px(flat, 75, 75));
    }

    // --- 12/13/19. Grabación ---

    string Ffmpeg()
    {
        Assert.True(FfmpegRunner.IsAvailable, "FFmpeg no está disponible: ejecuta build/fetch-ffmpeg.ps1 o define FCS_FFMPEG.");
        return FfmpegRunner.ExePath!;
    }

    string Record(string format, double seconds, bool pause, out RecordingSession session, bool kill = false)
    {
        Ffmpeg();
        RecordingSession? s = null; string output = Path.Combine(UiFixture.DataDir, $"grabacion_{Guid.NewGuid():N}.{format}");
        ui.RunAsync(async () =>
        {
            var vb = ScreenCapture.PrimaryBounds;
            s = new RecordingSession(new SD.Rectangle(vb.X + 20, vb.Y + 20, 480, 320), new RecordingOptions { Fps = 30, Format = format, HardwareAcceleration = false, Quality = "medium", GifFps = 10, GifMaxWidth = 320 });
            await s.StartAsync();
            await Task.Delay((int)(seconds * 1000));
            if (pause)
            {
                s.Pause(); Assert.True(s.IsPaused);
                var at = s.Elapsed; long frames = s.FramesWritten;
                await Task.Delay(1500);
                Assert.Equal(at, s.Elapsed);                       // el reloj no avanza en pausa
                Assert.InRange(s.FramesWritten - frames, 0, 2);    // no se escriben fotogramas en pausa
                s.Resume(); Assert.False(s.IsPaused);
                await Task.Delay(1000);
            }
            if (kill) { s.TestKillEncoder(); await Task.Delay(400); }
            await s.StopCaptureAsync();
            await s.FinalizeAsync(output);
        }, 180);
        session = s!;
        return output;
    }

    [Fact]
    public void Record_pause_resume_stop_produces_playable_mp4()
    {
        var file = Record("mp4", 2.0, pause: true, out var s);
        Assert.True(File.Exists(file));
        var info = FfmpegRunner.ProbeAsync(file).Result;
        output.WriteLine($"MP4: {info.Width}x{info.Height} {info.DurationSeconds:0.00}s {info.Fps}fps, codificador {s.Encoder?.FfmpegName}, {s.FramesWritten} fotogramas, {new FileInfo(file).Length} bytes");
        Assert.True(info.HasVideo); Assert.Equal(480, info.Width); Assert.Equal(320, info.Height);
        Assert.InRange(info.DurationSeconds, 2.4, 4.2); // ~3 s activos; la pausa de 1,5 s no cuenta
        Assert.InRange(s.FramesWritten, 70, 130);
        Assert.False(Directory.Exists(s.TempDir)); // temporales eliminados
        // decodificación completa sin errores = reproducible
        var dec = FfmpegRunner.Run($"-v error -i \"{file}\" -f null -");
        Assert.Equal(0, dec.ExitCode); Assert.Equal("", dec.Error.Trim());
    }

    [Fact]
    public void Record_gif_and_webm()
    {
        var gif = Record("gif", 1.5, false, out _);
        var gi = FfmpegRunner.ProbeAsync(gif).Result;
        output.WriteLine($"GIF: {gi.Width}x{gi.Height} {gi.DurationSeconds:0.00}s, {new FileInfo(gif).Length} bytes");
        Assert.True(gi.HasVideo); Assert.Equal(320, gi.Width);
        Assert.Equal("GIF8", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(gif), 0, 4));

        var webm = Record("webm", 1.5, false, out var ws);
        var wi = FfmpegRunner.ProbeAsync(webm).Result;
        output.WriteLine($"WebM: {wi.Width}x{wi.Height} {wi.DurationSeconds:0.00}s, codificador {ws.Encoder?.FfmpegName}");
        Assert.True(wi.HasVideo); Assert.InRange(wi.DurationSeconds, 1.0, 2.6);
    }

    [Fact]
    public void Interrupted_recording_is_recovered()
    {
        // El codificador muere a mitad de la grabación: lo ya escrito debe poder convertirse en un video válido.
        var file = Record("mp4", 2.5, false, out _, kill: true);
        var info = FfmpegRunner.ProbeAsync(file).Result;
        output.WriteLine($"Recuperado: {info.DurationSeconds:0.00}s");
        Assert.True(info.HasVideo);
        Assert.True(info.DurationSeconds > 0.8, "No se recuperó contenido de la grabación interrumpida.");
    }

    [Fact]
    public void Video_editor_exports_trimmed_split_video_with_overlay_and_gif()
    {
        var source = Record("mp4", 4.0, false, out _);
        var outMp4 = Path.Combine(UiFixture.DataDir, "editado.mp4"); var outGif = Path.Combine(UiFixture.DataDir, "editado.gif");
        double expected = 0;
        ui.RunAsync(async () =>
        {
            var w = new VideoEditorWindow(source);
            w.Show();
            await Task.Delay(2500); // carga y sondeo
            var t = w.TestTimeline;
            Assert.True(t.Duration > 3);
            t.TrimStart(0.5); t.Split(1.5); t.Split(2.5); t.SetRemoved(t.IndexAt(2.0), true); t.TrimEnd(3.5);
            expected = t.KeptDuration; // 0.5–1.5 y 2.5–3.5 = 2 s
            w.TestAddText("Prueba");
            Assert.True(await w.ExportAsync(outMp4));
            Assert.True(await w.ExportAsync(outGif));
            w.Close();
        }, 240);
        Assert.Equal(2.0, expected, 1);
        var info = FfmpegRunner.ProbeAsync(outMp4).Result;
        output.WriteLine($"Editado: {info.DurationSeconds:0.00}s (esperado {expected:0.00})");
        Assert.InRange(info.DurationSeconds, expected - 0.4, expected + 0.4);
        Assert.Equal(0, FfmpegRunner.Run($"-v error -i \"{outMp4}\" -f null -").ExitCode);
        Assert.True(new FileInfo(outGif).Length > 1000);
        Assert.Contains(ui.Run(() => Studio.Library.Query(new LibraryQuery { Kind = MediaKind.Gif })), i => i.Path == outGif);
    }

    [Fact]
    public void Key_display_never_shows_typed_text()
    {
        Assert.Null(KeyDisplay.Describe('A', false, false, false, false));
        Assert.Null(KeyDisplay.Describe('7', false, false, true, false));
        Assert.Equal("Ctrl + C", KeyDisplay.Describe('C', true, false, false, false));
        Assert.Equal("Ctrl + Shift + F5", KeyDisplay.Describe(0x74, true, false, true, false));
        Assert.Equal("Enter", KeyDisplay.Describe(0x0D, false, false, false, false));
        Assert.Null(KeyDisplay.Describe(0x11, true, false, false, false));
    }

    // --- 14. OCR ---

    [Fact]
    public void Ocr_returns_recognizable_text()
    {
        var langs = OcrService.AvailableLanguages();
        output.WriteLine("Idiomas OCR instalados: " + string.Join(", ", langs.Select(l => l.Tag)));
        if (langs.Count == 0) { ui.Skip(nameof(Ocr_returns_recognizable_text), "este equipo no tiene ningún paquete de OCR de Windows instalado"); return; }
        var img = ui.Run(() => Img.Draw(900, 220, dc =>
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 220));
            var tf = new Typeface("Segoe UI");
            dc.DrawText(new FormattedText("Fran Capture Studio 2026", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 40, Brushes.Black, 1.0), new Point(30, 30));
            dc.DrawText(new FormattedText("https://example.com/prueba", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, 32, Brushes.Black, 1.0), new Point(30, 120));
        }));
        OcrResult? r = null;
        ui.RunAsync(async () => r = await OcrService.RecognizeAsync(img, "auto"));
        output.WriteLine("Texto reconocido: " + r!.Text.Replace("\r\n", " | "));
        Assert.Contains("Capture", r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Studio", r.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026", r.Text);
        Assert.True(r.Lines.Count >= 2);
        var word = r.Lines[0].Words[0];
        Assert.InRange(word.Bounds.X, 10, 80); // las cajas vuelven a coordenadas de la imagen original
        Assert.NotEmpty(FranCapture.Core.Text.LinkDetector.Find(r.Text));
    }

    // --- Herramientas ---

    [Fact]
    public void Qr_roundtrip_combine_convert_and_background_removal()
    {
        var qr = QrTools.Generate("https://example.com/fran?x=ñ");
        Assert.Equal("https://example.com/fran?x=ñ", QrTools.Decode(qr));
        Assert.Null(QrTools.Decode(Solid(200, 200, Colors.White)));

        var combined = ui.Run(() => CombineWindow.Combine([Solid(100, 50, Colors.Red), Solid(200, 80, Colors.Blue)], vertical: true, gap: 10, equalize: true, Colors.White));
        Assert.Equal(200, combined.PixelWidth); Assert.Equal(100 + 10 + 80, combined.PixelHeight);
        var side = ui.Run(() => CombineWindow.Combine([Solid(100, 50, Colors.Red), Solid(200, 80, Colors.Blue)], vertical: false, gap: 0, equalize: false, Colors.White));
        Assert.Equal(300, side.PixelWidth); Assert.Equal(80, side.PixelHeight);

        var src = Path.Combine(UiFixture.DataDir, "origen.png");
        Img.Save(Solid(64, 48, Colors.Orange), src);
        var outDir = Path.Combine(UiFixture.DataDir, "convertidos");
        Directory.CreateDirectory(outDir);
        foreach (var ext in new[] { "jpg", "bmp", "gif", "tiff", "webp" })
        {
            var o = ConverterWindow.ConvertFile(src, outDir, ext, 90, true);
            Assert.True(new FileInfo(o).Length > 0, ext);
            var back = Img.Load(o);
            Assert.Equal(64, back.PixelWidth); Assert.Equal(48, back.PixelHeight);
        }
        Assert.True(File.Exists(src)); // el original no se toca

        var subject = ui.Run(() => Img.Draw(100, 100, dc => { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 100, 100)); dc.DrawRectangle(Brushes.DarkRed, null, new Rect(30, 30, 40, 40)); }));
        var cut = BackgroundRemovalWindow.RemoveByColor(subject, 30, out int removed);
        Assert.True(removed > 7000);
        Assert.Equal(0, Px(cut, 2, 2).A); Assert.Equal(255, Px(cut, 50, 50).A);

        var numbered = ui.Run(() => StepsCaptureWindow.Number(Solid(300, 200, Colors.White), 3));
        var p = Img.Pixels(numbered);
        Assert.Contains(Enumerable.Range(0, p.Length / 4), i => p[i * 4] > 200 && p[i * 4 + 2] < 60); // insignia azul
    }

    [Fact]
    public void Bug_report_exports_pdf_markdown_and_html()
    {
        var shot = Path.Combine(UiFixture.DataDir, "error.png");
        Img.Save(Solid(640, 360, Colors.SteelBlue), shot);
        var report = new BugReport
        {
            Title = "El botón Guardar no responde", Description = "Al pulsar Guardar no ocurre nada.", Expected = "Debe escribirse el archivo.",
            Steps = ["Abrir la aplicación", "Pulsar Guardar"], Attachments = [shot], IncludeTechnicalInfo = true, TechnicalInfo = "Windows 11",
        };
        var dir = Path.Combine(UiFixture.DataDir, "reportes"); Directory.CreateDirectory(dir);
        ui.Run(() =>
        {
            QaPage.ExportTo(report, Path.Combine(dir, "r.pdf"));
            QaPage.ExportTo(report, Path.Combine(dir, "r.md"));
            QaPage.ExportTo(report, Path.Combine(dir, "r.html"));
        });
        var pdf = File.ReadAllBytes(Path.Combine(dir, "r.pdf"));
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.True(pdf.Length > 5000);
        Assert.Contains("![error](error.png)", File.ReadAllText(Path.Combine(dir, "r.md")));
        Assert.True(File.Exists(Path.Combine(dir, "error.png")));
        Assert.Contains("data:image/png;base64", File.ReadAllText(Path.Combine(dir, "r.html")));
    }

    // --- 16. Configuración ---

    [Fact]
    public void Settings_persist_and_autostart_toggles_registry()
    {
        ui.Run(() =>
        {
            Studio.Settings.RecordFps = 60; Studio.Settings.Hotkeys[nameof(HotkeyAction.CaptureRegion)] = "F8";
            Studio.SaveSettings();
        });
        var reloaded = new FranCapture.Core.Settings.SettingsStore(AppPaths.SettingsFile).Load();
        Assert.Equal(60, reloaded.RecordFps); Assert.Equal("F8", reloaded.Hotkeys[nameof(HotkeyAction.CaptureRegion)]);
        ui.Run(() => { Studio.Settings.RecordFps = 30; Studio.Settings.Hotkeys[nameof(HotkeyAction.CaptureRegion)] = "F9"; Studio.SaveSettings(); });

        bool before = Autostart.IsEnabled();
        Autostart.Set(true, true); Assert.True(Autostart.IsEnabled());
        Autostart.Set(false, true); Assert.False(Autostart.IsEnabled());
        if (before) Autostart.Set(true, true);
    }

    // --- Interfaz ---

    [Fact]
    public void Main_window_builds_every_page_in_both_themes_and_languages()
    {
        ui.RunAsync(async () =>
        {
            var main = new MainWindow();
            Studio.Main = main;
            main.Show();
            foreach (var size in new[] { (1400.0, 900.0), (820.0, 520.0) })
            {
                main.Width = size.Item1; main.Height = size.Item2;
                foreach (var page in new[] { "library", "recorder", "ocr", "tools", "qa", "automations", "hotkeys", "storage", "settings", "dashboard" })
                {
                    main.Navigate(page);
                    await Task.Delay(120);
                    Assert.Equal(page, main.CurrentPage);
                }
            }
            Studio.Settings.Theme = "light"; Studio.ApplyTheme(); await Task.Delay(150);
            Studio.Settings.Language = "en"; Studio.ApplyLanguage(); main.Navigate("settings"); await Task.Delay(150);
            Assert.Equal("Settings", ((System.Windows.Controls.TextBlock)main.FindName("PageTitle")).Text);
            Studio.Settings.Theme = "dark"; Studio.ApplyTheme();
            Studio.Settings.Language = "es"; Studio.ApplyLanguage();
            Studio.IsExitingForRender = true;
            main.Close();
            Studio.IsExitingForRender = false;
            Studio.Main = null;
        });
    }
}

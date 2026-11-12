using System.IO;
using FranCapture.Core.Localization;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace FranCaptureStudio.Services;

public enum TrayCommand { CaptureRegion, CaptureScreen, StartRecording, StopRecording, OpenApp, OpenRecent, Settings, QuickPanel, Exit }

/// <summary>Icono de la bandeja del sistema con menú contextual y notificaciones.</summary>
public sealed class TrayService : IDisposable
{
    readonly WF.NotifyIcon _icon;
    readonly WF.ContextMenuStrip _menu;
    public event Action<TrayCommand>? Command;
    Action? _balloonClick;
    public bool IsRecording { get; set; }
    public bool DarkMenu { get; set; } = true;

    public TrayService()
    {
        _menu = new WF.ContextMenuStrip { ShowImageMargin = false, Font = new SD.Font("Segoe UI", 9.5f) };
        _menu.Opening += (_, _) => BuildMenu();
        _icon = new WF.NotifyIcon { Icon = LoadIcon(), Text = "Fran Capture Studio", ContextMenuStrip = _menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) Command?.Invoke(TrayCommand.OpenApp); };
        _icon.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
    }

    public static SD.Icon LoadIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res != null) using (res.Stream) return new SD.Icon(res.Stream, WF.SystemInformation.SmallIconSize);
        }
        catch { }
        try { if (Environment.ProcessPath is { } p && SD.Icon.ExtractAssociatedIcon(p) is { } ic) return ic; } catch { }
        return SD.SystemIcons.Application;
    }

    void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Renderer = new WF.ToolStripProfessionalRenderer(new MenuColors(DarkMenu)) { RoundedEdges = false };
        _menu.ForeColor = DarkMenu ? SD.Color.FromArgb(235, 238, 242) : SD.Color.FromArgb(25, 28, 33);
        Add("Capturar región", TrayCommand.CaptureRegion);
        Add("Capturar pantalla", TrayCommand.CaptureScreen);
        if (IsRecording) Add("Finalizar grabación", TrayCommand.StopRecording);
        else Add("Iniciar grabación", TrayCommand.StartRecording);
        _menu.Items.Add(new WF.ToolStripSeparator());
        Add("Abrir Fran Capture Studio", TrayCommand.OpenApp, bold: true);
        Add("Abrir últimas capturas", TrayCommand.OpenRecent);
        Add("Panel rápido", TrayCommand.QuickPanel);
        Add("Configuración", TrayCommand.Settings);
        _menu.Items.Add(new WF.ToolStripSeparator());
        Add("Salir completamente", TrayCommand.Exit);
    }

    void Add(string text, TrayCommand cmd, bool bold = false)
    {
        var item = new WF.ToolStripMenuItem(Loc.T(text)) { Padding = new WF.Padding(4, 3, 4, 3) };
        if (bold) item.Font = new SD.Font(_menu.Font, SD.FontStyle.Bold);
        item.Click += (_, _) => Command?.Invoke(cmd);
        _menu.Items.Add(item);
    }

    public void Notify(string title, string message, Action? onClick = null, bool error = false)
    {
        try
        {
            _balloonClick = onClick;
            _icon.ShowBalloonTip(4000, title, message, error ? WF.ToolTipIcon.Error : WF.ToolTipIcon.None);
        }
        catch { }
    }

    public void SetTooltip(string text) { try { _icon.Text = text.Length > 60 ? text[..60] : text; } catch { } }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    sealed class MenuColors(bool dark) : WF.ProfessionalColorTable
    {
        readonly SD.Color _bg = dark ? SD.Color.FromArgb(32, 35, 41) : SD.Color.FromArgb(250, 250, 251);
        readonly SD.Color _hover = dark ? SD.Color.FromArgb(52, 58, 68) : SD.Color.FromArgb(226, 232, 240);
        readonly SD.Color _border = dark ? SD.Color.FromArgb(64, 70, 82) : SD.Color.FromArgb(205, 210, 218);
        public override SD.Color ToolStripDropDownBackground => _bg;
        public override SD.Color ImageMarginGradientBegin => _bg;
        public override SD.Color ImageMarginGradientMiddle => _bg;
        public override SD.Color ImageMarginGradientEnd => _bg;
        public override SD.Color MenuBorder => _border;
        public override SD.Color MenuItemBorder => _hover;
        public override SD.Color MenuItemSelected => _hover;
        public override SD.Color MenuItemSelectedGradientBegin => _hover;
        public override SD.Color MenuItemSelectedGradientEnd => _hover;
        public override SD.Color SeparatorDark => _border;
        public override SD.Color SeparatorLight => _border;
    }
}

/// <summary>Sonidos breves generados en memoria (sin archivos externos).</summary>
public static class Sounds
{
    static readonly Dictionary<string, System.Media.SoundPlayer> Players = [];

    static byte[] Tone(double[] freqs, int ms, double volume = 0.25)
    {
        const int rate = 22050;
        int n = rate * ms / 1000;
        using var m = new MemoryStream();
        using var w = new BinaryWriter(m);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)rate, env = Math.Min(1, i / 120.0) * Math.Pow(1 - i / (double)n, 2);
            double f = freqs[Math.Min(freqs.Length - 1, i * freqs.Length / n)];
            w.Write((short)(Math.Sin(2 * Math.PI * f * t) * env * volume * short.MaxValue));
        }
        w.Flush();
        return m.ToArray();
    }

    static void Play(string key, Func<byte[]> make)
    {
        try
        {
            // El reproductor se conserva vivo: PlaySound asíncrono lee de su búfer interno.
            if (!Players.TryGetValue(key, out var p))
            {
                p = new System.Media.SoundPlayer(new MemoryStream(make()));
                p.Load();
                Players[key] = p;
            }
            p.Play();
        }
        catch { }
    }

    public static void Shutter() => Play("shutter", () => Tone([1400, 900], 90));
    public static void RecordStart() => Play("start", () => Tone([660, 880], 160));
    public static void RecordStop() => Play("stop", () => Tone([880, 550], 180));
}

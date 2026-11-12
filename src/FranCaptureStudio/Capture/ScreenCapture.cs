using System.Diagnostics;
using FranCaptureStudio.Interop;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;
using WF = System.Windows.Forms;

namespace FranCaptureStudio.Capture;

public sealed record MonitorInfo(int Index, string Name, SD.Rectangle Bounds, bool Primary)
{
    public override string ToString() => $"Monitor {Index + 1} ({Bounds.Width}×{Bounds.Height}){(Primary ? " · principal" : "")}";
}

public sealed record WindowInfo(IntPtr Handle, string Title, SD.Rectangle Bounds);

/// <summary>
/// Captura de pantalla mediante GDI (BitBlt). La aplicación es «Per-Monitor DPI Aware v2», así que todas las
/// coordenadas son píxeles físicos del escritorio virtual y funcionan con varios monitores y escalados distintos.
/// El contenido protegido por Windows (DRM, ventanas con afinidad de exclusión) aparece en negro: no se elude.
/// </summary>
public static class ScreenCapture
{
    public static SD.Rectangle VirtualBounds => WF.SystemInformation.VirtualScreen;

    public static List<MonitorInfo> Monitors() =>
        WF.Screen.AllScreens.Select((s, i) => new MonitorInfo(i, s.DeviceName, s.Bounds, s.Primary)).ToList();

    public static SD.Rectangle PrimaryBounds => WF.Screen.PrimaryScreen?.Bounds ?? VirtualBounds;

    public static SD.Bitmap Capture(SD.Rectangle rect, bool includeCursor = false)
    {
        if (rect.Width < 1 || rect.Height < 1) throw new ArgumentException("La región de captura está vacía.");
        var bmp = new SD.Bitmap(rect.Width, rect.Height, SDI.PixelFormat.Format32bppRgb);
        try
        {
            using var g = SD.Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.X, rect.Y, 0, 0, rect.Size, SD.CopyPixelOperation.SourceCopy);
            if (includeCursor) DrawCursor(g, rect.Location);
            return bmp;
        }
        catch { bmp.Dispose(); throw; }
    }

    public static SD.Bitmap CaptureVirtualScreen(bool includeCursor = false) => Capture(VirtualBounds, includeCursor);

    /// <summary>Dibuja el cursor actual del sistema dentro de una captura cuyo origen en pantalla es <paramref name="origin"/>.</summary>
    public static void DrawCursor(SD.Graphics g, SD.Point origin)
    {
        var ci = new Native.CURSORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci) || (ci.flags & Native.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
        int hx = 0, hy = 0;
        if (Native.GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
        }
        var hdc = g.GetHdc();
        try { Native.DrawIconEx(hdc, ci.ptScreenPos.X - origin.X - hx, ci.ptScreenPos.Y - origin.Y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL); }
        finally { g.ReleaseHdc(hdc); }
    }

    public static SD.Rectangle? ActiveWindowBounds()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero || !Native.TryGetWindowBounds(h, out var r)) return null;
        var rect = SD.Rectangle.Intersect(new SD.Rectangle(r.Left, r.Top, r.Width, r.Height), VirtualBounds);
        return rect.Width > 0 && rect.Height > 0 ? rect : null;
    }

    /// <summary>Ventanas visibles de nivel superior en orden Z (la primera es la de más arriba).</summary>
    public static List<WindowInfo> TopLevelWindows(bool excludeOwnProcess = true)
    {
        var list = new List<WindowInfo>();
        uint self = (uint)Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h)) return true;
            if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && Native.GetWindowTextLength(h) == 0) return true;
            if (excludeOwnProcess) { Native.GetWindowThreadProcessId(h, out var pid); if (pid == self) return true; }
            if (!Native.TryGetWindowBounds(h, out var r) || r.Width < 40 || r.Height < 30) return true;
            var title = Native.WindowTitle(h);
            if (title.Length == 0) return true;
            list.Add(new WindowInfo(h, title, new SD.Rectangle(r.Left, r.Top, r.Width, r.Height)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static SD.Color PixelAt(int x, int y)
    {
        using var bmp = Capture(new SD.Rectangle(x, y, 1, 1));
        return bmp.GetPixel(0, 0);
    }
}

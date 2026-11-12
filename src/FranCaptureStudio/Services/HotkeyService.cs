using System.Runtime.InteropServices;
using System.Windows.Interop;
using FranCapture.Core.Diagnostics;
using FranCapture.Core.Hotkeys;
using FranCaptureStudio.Interop;

namespace FranCaptureStudio.Services;

public enum HotkeyRegistration { Ok, InUseByOtherApp, Invalid, Failed }

/// <summary>
/// Atajos globales con la API oficial RegisterHotKey sobre una ventana de solo mensajes.
/// Windows únicamente notifica las combinaciones registradas: no se observa ninguna otra pulsación.
/// Al salir (Dispose) se liberan todos los registros.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    HwndSource? _source;
    readonly Dictionary<int, HotkeyAction> _ids = [];
    readonly Dictionary<HotkeyAction, HotkeyGesture> _registered = [];
    int _nextId = 0x4600;
    const int ProbeId = 0x45FF;

    public event Action<HotkeyAction>? Pressed;
    /// <summary>Si es true, las pulsaciones se ignoran (p. ej. mientras el usuario configura un atajo).</summary>
    public bool Suspended { get; set; }

    public IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    void EnsureWindow()
    {
        if (_source != null) return;
        var p = new HwndSourceParameters("FranCaptureStudio.Hotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && _ids.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            if (!Suspended)
                try { Pressed?.Invoke(action); } catch (Exception ex) { Log.Error("Atajo " + action, ex); }
        }
        return IntPtr.Zero;
    }

    public IReadOnlyDictionary<HotkeyAction, HotkeyGesture> Registered => _registered;

    public HotkeyRegistration Register(HotkeyAction action, HotkeyGesture gesture)
    {
        Unregister(action);
        if (gesture.IsEmpty || gesture.Validate() != HotkeyProblem.None) return HotkeyRegistration.Invalid;
        EnsureWindow();
        int id = _nextId++;
        if (Native.RegisterHotKey(_source!.Handle, id, (uint)gesture.Modifiers | Native.MOD_NOREPEAT, (uint)gesture.VirtualKey))
        {
            _ids[id] = action; _registered[action] = gesture;
            return HotkeyRegistration.Ok;
        }
        int err = Marshal.GetLastWin32Error();
        Log.Warn($"No se pudo registrar {gesture} para {action} (error {err})");
        return err == Native.ERROR_HOTKEY_ALREADY_REGISTERED ? HotkeyRegistration.InUseByOtherApp : HotkeyRegistration.Failed;
    }

    public void Unregister(HotkeyAction action)
    {
        if (_source == null) return;
        foreach (var kv in _ids.Where(k => k.Value == action).ToList())
        {
            Native.UnregisterHotKey(_source.Handle, kv.Key);
            _ids.Remove(kv.Key);
        }
        _registered.Remove(action);
    }

    public void UnregisterAll()
    {
        if (_source != null) foreach (var id in _ids.Keys) Native.UnregisterHotKey(_source.Handle, id);
        _ids.Clear(); _registered.Clear();
    }

    /// <summary>Comprueba si un gesto puede registrarse ahora mismo (lo registra y lo libera de inmediato).</summary>
    public bool IsAvailable(HotkeyGesture gesture)
    {
        if (gesture.IsEmpty || gesture.Validate() != HotkeyProblem.None) return false;
        if (_registered.Values.Contains(gesture)) return false;
        EnsureWindow();
        if (!Native.RegisterHotKey(_source!.Handle, ProbeId, (uint)gesture.Modifiers | Native.MOD_NOREPEAT, (uint)gesture.VirtualKey)) return false;
        Native.UnregisterHotKey(_source.Handle, ProbeId);
        return true;
    }

    /// <summary>Aplica el mapa completo de la configuración. Devuelve las acciones que no pudieron registrarse.</summary>
    public Dictionary<HotkeyAction, HotkeyRegistration> Apply(IReadOnlyDictionary<string, string> map)
    {
        UnregisterAll();
        var failures = new Dictionary<HotkeyAction, HotkeyRegistration>();
        foreach (var kv in map)
        {
            if (string.IsNullOrEmpty(kv.Value) || !Enum.TryParse<HotkeyAction>(kv.Key, out var action)) continue;
            if (!HotkeyGesture.TryParse(kv.Value, out var g)) { failures[action] = HotkeyRegistration.Invalid; continue; }
            var r = Register(action, g);
            if (r != HotkeyRegistration.Ok) failures[action] = r;
        }
        return failures;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.Dispose();
        _source = null;
    }
}

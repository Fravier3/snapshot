namespace FranCapture.Core.Hotkeys;

public enum HotkeyAction
{
    OpenApp,
    CaptureRegion,
    CaptureFullScreen,
    CaptureActiveWindow,
    StartRecording,
    PauseResumeRecording,
    StopRecording,
    StartGifRecording,
    OcrRegion,
    OpenEditor,
    ShowLibrary,
    RepeatLastRegion,
    QuickPanel,
}

[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 } // coincide con MOD_* de Win32

public enum HotkeyProblem
{
    None,
    NoKey,
    NeedsModifier,   // letras, números, etc. sin modificador romperían la escritura normal
    Reserved,        // combinaciones que Windows reserva
    WinNotAllowed,
}

public static class HotkeyDefaults
{
    public static Dictionary<string, string> Create() => new()
    {
        [nameof(HotkeyAction.OpenApp)] = "",
        [nameof(HotkeyAction.CaptureRegion)] = "F9",
        [nameof(HotkeyAction.CaptureFullScreen)] = "Ctrl+Shift+F9",
        [nameof(HotkeyAction.CaptureActiveWindow)] = "",
        [nameof(HotkeyAction.StartRecording)] = "Ctrl+Shift+F10",
        [nameof(HotkeyAction.PauseResumeRecording)] = "Ctrl+Shift+F11",
        [nameof(HotkeyAction.StopRecording)] = "Ctrl+Shift+F12",
        [nameof(HotkeyAction.StartGifRecording)] = "",
        [nameof(HotkeyAction.OcrRegion)] = "",
        [nameof(HotkeyAction.OpenEditor)] = "",
        [nameof(HotkeyAction.ShowLibrary)] = "",
        [nameof(HotkeyAction.RepeatLastRegion)] = "",
        [nameof(HotkeyAction.QuickPanel)] = "",
    };

    public static string DisplayName(HotkeyAction a) => a switch
    {
        HotkeyAction.OpenApp => "Abrir o restaurar Fran Capture Studio",
        HotkeyAction.CaptureRegion => "Abrir el selector de región",
        HotkeyAction.CaptureFullScreen => "Capturar pantalla completa",
        HotkeyAction.CaptureActiveWindow => "Capturar ventana activa",
        HotkeyAction.StartRecording => "Iniciar grabación",
        HotkeyAction.PauseResumeRecording => "Pausar o continuar grabación",
        HotkeyAction.StopRecording => "Finalizar grabación",
        HotkeyAction.StartGifRecording => "Comenzar grabación GIF",
        HotkeyAction.OcrRegion => "OCR sobre una región",
        HotkeyAction.OpenEditor => "Abrir editor",
        HotkeyAction.ShowLibrary => "Mostrar biblioteca",
        HotkeyAction.RepeatLastRegion => "Capturar de nuevo la última región",
        HotkeyAction.QuickPanel => "Mostrar panel rápido",
        _ => a.ToString(),
    };
}

/// <summary>Combinación de teclas (modificadores + tecla virtual de Windows).</summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    public const int VK_SNAPSHOT = 0x2C, VK_PAUSE = 0x13, VK_SCROLL = 0x91, VK_INSERT = 0x2D, VK_F1 = 0x70, VK_F12 = 0x7B, VK_F24 = 0x87;

    static readonly Dictionary<int, string> Names = BuildNames();
    static readonly Dictionary<string, int> Codes = Names.ToDictionary(k => k.Value, k => k.Key, StringComparer.OrdinalIgnoreCase);

    static Dictionary<int, string> BuildNames()
    {
        var d = new Dictionary<int, string>();
        for (int i = 0; i < 24; i++) d[VK_F1 + i] = "F" + (i + 1);
        for (int c = 'A'; c <= 'Z'; c++) d[c] = ((char)c).ToString();
        for (int c = '0'; c <= '9'; c++) d[c] = ((char)c).ToString();
        for (int i = 0; i <= 9; i++) d[0x60 + i] = "Num" + i;
        d[VK_SNAPSHOT] = "ImprPant"; d[VK_PAUSE] = "Pausa"; d[VK_SCROLL] = "BloqDespl"; d[VK_INSERT] = "Insert";
        d[0x2E] = "Supr"; d[0x24] = "Inicio"; d[0x23] = "Fin"; d[0x21] = "RePág"; d[0x22] = "AvPág";
        d[0x25] = "Izquierda"; d[0x26] = "Arriba"; d[0x27] = "Derecha"; d[0x28] = "Abajo";
        d[0x20] = "Espacio"; d[0x09] = "Tab"; d[0x0D] = "Enter"; d[0x1B] = "Esc"; d[0x08] = "Retroceso";
        d[0xBA] = "Ñ/;"; d[0xBB] = "+"; d[0xBC] = ","; d[0xBD] = "-"; d[0xBE] = "."; d[0xC0] = "º";
        d[0x6A] = "Num*"; d[0x6B] = "Num+"; d[0x6D] = "Num-"; d[0x6F] = "Num/";
        return d;
    }

    public static bool IsKnownKey(int vk) => Names.ContainsKey(vk);
    public static string KeyName(int vk) => Names.TryGetValue(vk, out var n) ? n : "VK" + vk;
    public bool IsEmpty => VirtualKey == 0;

    public override string ToString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = HotkeyModifiers.None;
        int vk = 0;
        // "Ctrl++" debe leerse como Ctrl y la tecla "+"
        var t = text.Trim();
        var tokens = new List<string>();
        int start = 0;
        for (int i = 0; i < t.Length; i++)
            if (t[i] == '+' && i > start) { tokens.Add(t[start..i]); start = i + 1; }
        tokens.Add(t[start..]);
        foreach (var raw in tokens)
        {
            var tok = raw.Trim();
            switch (tok.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= HotkeyModifiers.Ctrl; continue;
                case "alt": mods |= HotkeyModifiers.Alt; continue;
                case "shift" or "mayús" or "mayus": mods |= HotkeyModifiers.Shift; continue;
                case "win" or "windows": mods |= HotkeyModifiers.Win; continue;
                case "printscreen" or "prtsc" or "prtscn": tok = "ImprPant"; break;
            }
            if (vk != 0) return false;
            if (Codes.TryGetValue(tok, out var code)) vk = code;
            else if (tok.StartsWith("VK", StringComparison.OrdinalIgnoreCase) && int.TryParse(tok.AsSpan(2), out var n) && n is > 0 and < 256) vk = n;
            else return false;
        }
        if (vk == 0) return false;
        gesture = new HotkeyGesture(mods, vk);
        return true;
    }

    /// <summary>Comprueba si el gesto es seguro como atajo global.</summary>
    public HotkeyProblem Validate()
    {
        if (IsEmpty) return HotkeyProblem.NoKey;
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) return HotkeyProblem.WinNotAllowed;
        bool fkey = VirtualKey is >= VK_F1 and <= VK_F24;
        bool standalone = fkey || VirtualKey is VK_SNAPSHOT or VK_PAUSE or VK_SCROLL;
        var m = Modifiers;
        if (m == HotkeyModifiers.None)
        {
            if (VirtualKey == VK_F12) return HotkeyProblem.Reserved;            // reservada por Windows para depuradores
            if (VirtualKey == VK_F1) return HotkeyProblem.Reserved;             // Ayuda
            return standalone ? HotkeyProblem.None : HotkeyProblem.NeedsModifier;
        }
        if (m == HotkeyModifiers.Shift && !standalone && VirtualKey != VK_INSERT) return HotkeyProblem.NeedsModifier; // Shift+letra = mayúscula
        if (m == HotkeyModifiers.Alt && VirtualKey == 0x73) return HotkeyProblem.Reserved;          // Alt+F4
        if (m == HotkeyModifiers.Alt && VirtualKey is 0x09 or 0x1B or 0x20) return HotkeyProblem.Reserved; // Alt+Tab/Esc/Espacio
        if (m == HotkeyModifiers.Ctrl && VirtualKey == 0x1B) return HotkeyProblem.Reserved;         // Ctrl+Esc
        if (m == (HotkeyModifiers.Ctrl | HotkeyModifiers.Shift) && VirtualKey == 0x1B) return HotkeyProblem.Reserved;
        if (m == (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt) && VirtualKey == 0x2E) return HotkeyProblem.Reserved; // Ctrl+Alt+Supr
        if (m == HotkeyModifiers.Ctrl && VirtualKey is 'C' or 'V' or 'X' or 'Z' or 'A' or 'S') return HotkeyProblem.Reserved; // edición básica
        return HotkeyProblem.None;
    }

    public static string Explain(HotkeyProblem p) => p switch
    {
        HotkeyProblem.None => "",
        HotkeyProblem.NoKey => "Pulsa una tecla que no sea solo Ctrl, Alt o Shift.",
        HotkeyProblem.NeedsModifier => "Esta tecla necesita combinarse con Ctrl o Alt; usarla sola interferiría con la escritura normal. Las teclas de función (F2–F11, F13–F24), ImprPant y Pausa sí pueden usarse solas.",
        HotkeyProblem.Reserved => "Windows u otras funciones básicas reservan esta combinación. Elige otra.",
        HotkeyProblem.WinNotAllowed => "Las combinaciones con la tecla Windows están reservadas por el sistema.",
        _ => p.ToString(),
    };
}

/// <summary>Mapa acción→gesto con detección de conflictos internos y sugerencia de alternativas.</summary>
public static class HotkeyMap
{
    /// <summary>Devuelve la acción que ya usa ese gesto (distinta de <paramref name="forAction"/>), o null.</summary>
    public static HotkeyAction? FindConflict(IReadOnlyDictionary<string, string> map, HotkeyAction forAction, HotkeyGesture gesture)
    {
        var text = gesture.ToString();
        foreach (var kv in map)
        {
            if (kv.Key == forAction.ToString() || string.IsNullOrEmpty(kv.Value)) continue;
            if (HotkeyGesture.TryParse(kv.Value, out var g) && g.ToString() == text && Enum.TryParse<HotkeyAction>(kv.Key, out var a)) return a;
        }
        return null;
    }

    /// <summary>Alternativas cercanas al gesto pedido que no chocan con los atajos propios.</summary>
    public static List<HotkeyGesture> Suggest(IReadOnlyDictionary<string, string> map, HotkeyGesture wanted, Func<HotkeyGesture, bool>? isAvailable = null, int max = 3)
    {
        var used = new HashSet<string>(map.Values.Where(v => !string.IsNullOrEmpty(v)));
        var candidates = new List<HotkeyGesture>();
        var modSets = new[]
        {
            HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt,
            HotkeyModifiers.Alt | HotkeyModifiers.Shift, HotkeyModifiers.Ctrl, HotkeyModifiers.None,
        };
        if (!wanted.IsEmpty)
            foreach (var m in modSets) if (m != wanted.Modifiers) candidates.Add(new HotkeyGesture(m, wanted.VirtualKey));
        foreach (var vk in new[] { 0x78, 0x77, 0x79, 0x76, 0x7A, 0x75 }) // F9 F8 F10 F7 F11 F6
            foreach (var m in new[] { HotkeyModifiers.None, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt })
                candidates.Add(new HotkeyGesture(m, vk));
        var result = new List<HotkeyGesture>();
        foreach (var c in candidates)
        {
            if (result.Count >= max) break;
            if (c == wanted || c.Validate() != HotkeyProblem.None || used.Contains(c.ToString()) || result.Contains(c)) continue;
            if (isAvailable != null && !isAvailable(c)) continue;
            result.Add(c);
        }
        return result;
    }
}

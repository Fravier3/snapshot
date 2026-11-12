namespace FranCapture.Core.Automation;

public enum AutomationTrigger { Capture, Recording, Ocr }

public enum AutomationAction
{
    OpenEditor,        // Capturar → abrir editor
    CopyToClipboard,   // Capturar → copiar al portapapeles
    AutoSave,          // Capturar → guardar automáticamente
    AddDateToName,     // Capturar → agregar fecha al nombre
    ConvertToJpg,      // Capturar → convertir a JPG
    AddWatermark,      // Capturar → agregar marca de agua
    OpenVideoEditor,   // Grabar → abrir editor de video
    CopyText,          // OCR → copiar texto
}

public sealed class AutomationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AutomationTrigger Trigger { get; set; }
    public AutomationAction Action { get; set; }
    public bool Enabled { get; set; } = true;

    public static bool IsCompatible(AutomationTrigger t, AutomationAction a) => t switch
    {
        AutomationTrigger.Capture => a is AutomationAction.OpenEditor or AutomationAction.CopyToClipboard or AutomationAction.AutoSave
            or AutomationAction.AddDateToName or AutomationAction.ConvertToJpg or AutomationAction.AddWatermark,
        AutomationTrigger.Recording => a is AutomationAction.OpenVideoEditor,
        AutomationTrigger.Ocr => a is AutomationAction.CopyText,
        _ => false,
    };

    public static IEnumerable<AutomationAction> ActionsFor(AutomationTrigger t) =>
        Enum.GetValues<AutomationAction>().Where(a => IsCompatible(t, a));

    public static List<AutomationRule> Defaults() =>
    [
        new() { Trigger = AutomationTrigger.Capture, Action = AutomationAction.AutoSave, Enabled = true },
        new() { Trigger = AutomationTrigger.Capture, Action = AutomationAction.CopyToClipboard, Enabled = true },
        new() { Trigger = AutomationTrigger.Capture, Action = AutomationAction.OpenEditor, Enabled = true },
        new() { Trigger = AutomationTrigger.Capture, Action = AutomationAction.AddDateToName, Enabled = false },
        new() { Trigger = AutomationTrigger.Capture, Action = AutomationAction.ConvertToJpg, Enabled = false },
        new() { Trigger = AutomationTrigger.Capture, Action = AutomationAction.AddWatermark, Enabled = false },
        new() { Trigger = AutomationTrigger.Recording, Action = AutomationAction.OpenVideoEditor, Enabled = true },
        new() { Trigger = AutomationTrigger.Ocr, Action = AutomationAction.CopyText, Enabled = false },
    ];

    public static string TriggerName(AutomationTrigger t) => t switch
    {
        AutomationTrigger.Capture => "Capturar",
        AutomationTrigger.Recording => "Grabar",
        AutomationTrigger.Ocr => "OCR",
        _ => t.ToString(),
    };

    public static string ActionName(AutomationAction a) => a switch
    {
        AutomationAction.OpenEditor => "abrir editor",
        AutomationAction.CopyToClipboard => "copiar al portapapeles",
        AutomationAction.AutoSave => "guardar automáticamente",
        AutomationAction.AddDateToName => "agregar fecha al nombre",
        AutomationAction.ConvertToJpg => "convertir a JPG",
        AutomationAction.AddWatermark => "agregar marca de agua",
        AutomationAction.OpenVideoEditor => "abrir editor de video",
        AutomationAction.CopyText => "copiar texto",
        _ => a.ToString(),
    };
}

/// <summary>Resultado de evaluar las reglas activas para un disparador.</summary>
public sealed class AutomationPlan
{
    public bool OpenEditor, CopyToClipboard, AutoSave, AddDateToName, ConvertToJpg, AddWatermark, OpenVideoEditor, CopyText;

    public static AutomationPlan For(AutomationTrigger trigger, IEnumerable<AutomationRule>? rules)
    {
        var p = new AutomationPlan();
        foreach (var r in rules ?? [])
        {
            if (!r.Enabled || r.Trigger != trigger || !AutomationRule.IsCompatible(r.Trigger, r.Action)) continue;
            switch (r.Action)
            {
                case AutomationAction.OpenEditor: p.OpenEditor = true; break;
                case AutomationAction.CopyToClipboard: p.CopyToClipboard = true; break;
                case AutomationAction.AutoSave: p.AutoSave = true; break;
                case AutomationAction.AddDateToName: p.AddDateToName = true; break;
                case AutomationAction.ConvertToJpg: p.ConvertToJpg = true; break;
                case AutomationAction.AddWatermark: p.AddWatermark = true; break;
                case AutomationAction.OpenVideoEditor: p.OpenVideoEditor = true; break;
                case AutomationAction.CopyText: p.CopyText = true; break;
            }
        }
        return p;
    }

    public static bool IsEnabled(IEnumerable<AutomationRule> rules, AutomationTrigger t, AutomationAction a) =>
        rules.Any(r => r.Enabled && r.Trigger == t && r.Action == a);

    /// <summary>Activa o desactiva (creándola si no existe) la regla indicada.</summary>
    public static void SetEnabled(List<AutomationRule> rules, AutomationTrigger t, AutomationAction a, bool enabled)
    {
        var matches = rules.Where(r => r.Trigger == t && r.Action == a).ToList();
        if (matches.Count == 0) { if (enabled) rules.Add(new AutomationRule { Trigger = t, Action = a, Enabled = true }); return; }
        foreach (var m in matches) m.Enabled = enabled;
    }
}

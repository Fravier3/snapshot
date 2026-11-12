using System.ComponentModel;

namespace FranCapture.Core.Localization;

/// <summary>
/// Localización: el español es el idioma base y la propia cadena en español es la clave.
/// Para inglés se busca la traducción en <see cref="English"/>; si falta, se muestra el español.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    string _language = "es";

    public string Language
    {
        get => _language;
        set
        {
            value = value == "en" ? "en" : "es";
            if (_language == value) return;
            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    public string this[string key] => Translate(key);

    public string Translate(string key) =>
        _language == "en" && English.Map.TryGetValue(key, out var en) ? en : key;

    public static string T(string key) => Instance.Translate(key);
    public static string T(string key, params object?[] args) => string.Format(Instance.Translate(key), args);
}

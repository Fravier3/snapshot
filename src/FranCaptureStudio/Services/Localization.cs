using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using FranCapture.Core.Localization;

namespace FranCaptureStudio;

/// <summary>Extensión de marcado: <c>{l:T 'Texto en español'}</c>. Se actualiza al cambiar de idioma.</summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public string Key { get; set; } = "";
    public TExtension() { }
    public TExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(Loc.Language)) { Source = Loc.Instance, Mode = BindingMode.OneWay, Converter = Conv.Instance, ConverterParameter = Key }.ProvideValue(serviceProvider);

    sealed class Conv : IValueConverter
    {
        public static readonly Conv Instance = new();
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Loc.T((string)parameter);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}

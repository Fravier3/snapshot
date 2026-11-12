using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FranCapture.Core.Localization;

namespace FranCaptureStudio.Views;

/// <summary>Constructores de interfaz para las páginas de ajustes (texto localizado en vivo).</summary>
public static class Form
{
    sealed class LocConv : IValueConverter
    {
        public static readonly LocConv Instance = new();
        public object Convert(object value, Type t, object parameter, System.Globalization.CultureInfo c) => Loc.T((string)parameter);
        public object ConvertBack(object value, Type t, object parameter, System.Globalization.CultureInfo c) => Binding.DoNothing;
    }

    /// <summary>Enlaza una propiedad de texto a una cadena localizable.</summary>
    public static T Tr<T>(T target, DependencyProperty prop, string key) where T : DependencyObject
    {
        BindingOperations.SetBinding(target, prop, new Binding(nameof(Loc.Language)) { Source = Loc.Instance, Converter = LocConv.Instance, ConverterParameter = key, Mode = BindingMode.OneWay });
        return target;
    }

    public static TextBlock Text(string key, string style = "Body", bool localized = true)
    {
        var t = new TextBlock { Style = Ui.S(style) };
        if (localized) Tr(t, TextBlock.TextProperty, key); else t.Text = key;
        return t;
    }

    public static Border Card(string? title, params UIElement?[] rows)
    {
        var sp = new StackPanel();
        if (title != null) sp.Children.Add(Text(title, "SectionTitle"));
        foreach (var r in rows) if (r != null) sp.Children.Add(r);
        return new Border { Style = Ui.S("Card"), Child = sp };
    }

    public static FrameworkElement Row(string label, UIElement control, string? hint = null)
    {
        var g = new Grid { Margin = new Thickness(0, 5, 0, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 160 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(Text(label, "FieldLabel"));
        if (hint != null) left.Children.Add(Text(hint, "Muted"));
        g.Children.Add(left);
        if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.HorizontalAlignment = HorizontalAlignment.Right; }
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    public static CheckBox Toggle(bool value, Action<bool> set)
    {
        var c = new CheckBox { IsChecked = value, MinWidth = 0 };
        c.Click += (_, _) => set(c.IsChecked == true);
        return c;
    }

    public static FrameworkElement ToggleRow(string label, bool value, Action<bool> set, string? hint = null) => Row(label, Toggle(value, set), hint);

    public static ComboBox Combo<T>(IEnumerable<(T Value, string Label)> options, T current, Action<T> set, double width = 200, bool localized = true)
    {
        var cb = new ComboBox { Width = width };
        foreach (var (v, label) in options)
        {
            var item = new ComboBoxItem { Tag = v };
            if (localized) Tr(item, ContentControl.ContentProperty, label); else item.Content = label;
            cb.Items.Add(item);
            if (EqualityComparer<T>.Default.Equals(v, current)) cb.SelectedItem = item;
        }
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is ComboBoxItem { Tag: T v }) set(v); };
        return cb;
    }

    public static Button Button(string key, Action click, string style = "SecondaryButton")
    {
        var b = new Button { Style = Ui.S(style), Margin = new Thickness(0, 0, 8, 0) };
        Tr(b, ContentControl.ContentProperty, key);
        b.Click += (_, _) => click();
        return b;
    }

    public static StackPanel Buttons(params UIElement[] buttons)
    {
        var sp = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var b in buttons) sp.Children.Add(b);
        var host = new StackPanel();
        host.Children.Add(sp);
        return host;
    }

    public static TextBox Input(string value, Action<string> set, double width = 260)
    {
        var t = new TextBox { Text = value, Width = width };
        t.LostFocus += (_, _) => set(t.Text);
        return t;
    }

    public static ScrollViewer Page(params UIElement?[] children)
    {
        var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 24), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in children) if (c != null) sp.Children.Add(c);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = sp };
    }

    /// <summary>Mosaico con icono, título y descripción.</summary>
    public static Button Tile(string glyph, string title, string? subtitle, Action click, double width = 208, bool subtitleLocalized = true)
    {
        var icon = new TextBlock { Text = glyph, Style = Ui.S("Icon"), FontSize = 22, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        var t = Text(title, "Body"); t.FontWeight = FontWeights.SemiBold;
        var sp = new StackPanel { Children = { icon, t } };
        if (subtitle != null) { var s = Text(subtitle, "Muted", subtitleLocalized); s.Margin = new Thickness(0, 3, 0, 0); sp.Children.Add(s); }
        var b = new Button { Style = Ui.S("Tile"), Content = sp, Width = width, MinHeight = 104 };
        b.Click += (_, _) => click();
        return b;
    }
}

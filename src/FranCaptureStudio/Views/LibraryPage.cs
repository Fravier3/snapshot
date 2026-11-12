using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FranCapture.Core.Diagnostics;
using FranCapture.Core.Library;
using FranCapture.Core.Localization;
using FranCapture.Core.Storage;
using FranCaptureStudio.Services;

namespace FranCaptureStudio.Views;

/// <summary>Biblioteca local: miniaturas, búsqueda, filtros, favoritos, etiquetas y colecciones.</summary>
public sealed class LibraryPage : UserControl, IRefreshable
{
    readonly TextBox _search = new() { Width = 220, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly Panel _itemsHost = new WrapPanel();
    readonly StackPanel _listHost = new();
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly StackPanel _details = new() { Margin = new Thickness(16) };
    readonly TextBlock _count = Form.Text("", "Muted", false);
    MediaKind? _kind; bool _favOnly, _newest = true, _grid = true; string? _collection;
    readonly ComboBox _collectionBox = new() { Width = 170, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    LibraryItem? _selected; Border? _selectedVisual;
    const int PageSize = 300;
    readonly SemaphoreSlim _thumbs = new(3);
    int _generation;
    public IReadOnlyList<LibraryItem> Shown { get; private set; } = [];

    public LibraryPage()
    {
        var bar = new WrapPanel { Margin = new Thickness(24, 14, 24, 10) };
        _search.TextChanged += (_, _) => Reload();
        _search.ToolTip = Loc.T("Buscar por nombre o etiqueta");
        bar.Children.Add(_search);
        void Chip(string text, MediaKind? k, bool check = false)
        {
            var rb = new RadioButton { Style = Ui.S("Chip"), GroupName = "libkind", IsChecked = check, Margin = new Thickness(0, 0, 6, 0) };
            Form.Tr(rb, ContentProperty, text);
            rb.Checked += (_, _) => { _kind = k; Reload(); };
            bar.Children.Add(rb);
        }
        Chip("Todo", null, true); Chip("Imágenes", MediaKind.Image); Chip("Videos", MediaKind.Video); Chip("GIF", MediaKind.Gif);
        var fav = new CheckBox { Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        Form.Tr(fav, ContentProperty, "Solo favoritos");
        fav.Click += (_, _) => { _favOnly = fav.IsChecked == true; Reload(); };
        bar.Children.Add(fav);
        _collectionBox.SelectionChanged += (_, _) => { if (_filling) return; _collection = (_collectionBox.SelectedItem as ComboBoxItem)?.Tag as string; Reload(); };
        bar.Children.Add(_collectionBox);
        var sort = Form.Combo([(true, "Más recientes primero"), (false, "Más antiguas primero")], true, v => { _newest = v; Reload(); }, 190);
        sort.Margin = new Thickness(0, 0, 10, 0); sort.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(sort);
        var view = Form.Combo([(true, "Cuadrícula"), (false, "Lista")], true, v => { _grid = v; Reload(); }, 130);
        view.Margin = new Thickness(0, 0, 10, 0); view.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(view);
        bar.Children.Add(Form.Button("Agregar archivos…", Import));
        _count.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(_count);

        var detailsBorder = new Border { Width = 290, BorderThickness = new Thickness(1, 0, 0, 0), Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _details } };
        detailsBorder.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        detailsBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");

        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top); dock.Children.Add(bar);
        DockPanel.SetDock(detailsBorder, Dock.Right); dock.Children.Add(detailsBorder);
        dock.Children.Add(_scroll);
        Content = dock;
        Studio.Library.Changed += () => { if (IsVisible) Reload(); };
        ShowDetails(null);
    }

    public void Refresh() => Reload();

    bool _filling;

    void FillCollections()
    {
        _filling = true;
        _collectionBox.Items.Clear();
        _collectionBox.Items.Add(new ComboBoxItem { Content = Loc.T("Todas las colecciones"), Tag = null });
        foreach (var c in Studio.Library.Repo.Collections()) _collectionBox.Items.Add(new ComboBoxItem { Content = c, Tag = c });
        _collectionBox.SelectedItem = _collectionBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == _collection) ?? _collectionBox.Items[0];
        if ((_collectionBox.SelectedItem as ComboBoxItem)?.Tag == null) _collection = null;
        _filling = false;
    }

    public void Reload()
    {
        int gen = ++_generation;
        FillCollections();
        var items = Studio.Library.Query(new LibraryQuery { Search = _search.Text, Kind = _kind, FavoritesOnly = _favOnly, Collection = _collection, NewestFirst = _newest, Limit = PageSize });
        Shown = items;
        _count.Text = items.Count >= PageSize ? Loc.T("Mostrando los primeros {0}", PageSize) : Loc.T("{0} elementos", items.Count);
        _itemsHost.Children.Clear(); _listHost.Children.Clear();
        _selectedVisual = null;
        Panel host = _grid ? _itemsHost : _listHost;
        if (_grid) _itemsHost.Margin = new Thickness(24, 0, 12, 24); else _listHost.Margin = new Thickness(24, 0, 24, 24);
        _scroll.Content = items.Count > 0 ? host : new TextBlock { Text = Loc.T("No hay elementos que coincidan. Las capturas y grabaciones que hagas aparecerán aquí."), Style = Ui.S("Muted"), Margin = new Thickness(26, 10, 24, 0) };
        foreach (var it in items) host.Children.Add(MakeItem(it, gen));
        var keep = _selected != null ? items.FirstOrDefault(i => i.Id == _selected.Id) : null;
        ShowDetails(keep);
    }

    static string KindGlyph(MediaKind k) => k switch { MediaKind.Video => "", MediaKind.Gif => "", _ => "" };

    static string Details(LibraryItem it) =>
        $"{(it.Width > 0 ? $"{it.Width} × {it.Height} · " : "")}{FileNaming.FormatSize(it.SizeBytes)} · {it.CreatedUtc.ToLocalTime():g}";

    Border MakeItem(LibraryItem it, int gen)
    {
        var img = new Image { Stretch = Stretch.UniformToFill, Width = _grid ? 190 : 64, Height = _grid ? 118 : 42 };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        _ = LoadThumbAsync(img, it, gen);
        var thumb = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = img };
        thumb.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
        var name = Form.Text(it.Name, "Body", false); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
        var meta = Form.Text((it.Favorite ? "★ " : "") + Details(it), "Muted", false); meta.TextWrapping = TextWrapping.NoWrap; meta.TextTrimming = TextTrimming.CharacterEllipsis;
        var kind = new TextBlock { Text = KindGlyph(it.Kind), Style = Ui.S("Icon"), FontSize = 12, Margin = new Thickness(0, 0, 6, 0) };
        kind.SetResourceReference(TextBlock.ForegroundProperty, "Text2Brush");
        FrameworkElement content;
        if (_grid)
        {
            name.Width = 170; meta.Width = 190;
            content = new StackPanel { Children = { thumb, new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 1), Children = { kind, name } }, meta } };
        }
        else
        {
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Children = { new StackPanel { Orientation = Orientation.Horizontal, Children = { kind, name } }, meta } };
            var dp = new DockPanel(); DockPanel.SetDock(thumb, Dock.Left); dp.Children.Add(thumb); dp.Children.Add(texts);
            content = dp;
        }
        var b = new Border { Padding = new Thickness(8), Margin = _grid ? new Thickness(0, 0, 10, 10) : new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.5), Child = content, Cursor = Cursors.Hand, Tag = it, Focusable = true };
        b.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        b.BorderBrush = Brushes.Transparent;
        b.MouseLeftButtonDown += (_, e) => { Select(b, it); if (e.ClickCount == 2) OpenItem(it); };
        b.MouseRightButtonDown += (_, _) => Select(b, it);
        b.ContextMenu = Menu(it);
        return b;
    }

    async Task LoadThumbAsync(Image img, LibraryItem it, int gen)
    {
        await _thumbs.WaitAsync();
        try
        {
            if (gen != _generation) return;
            var src = await Task.Run(() => Studio.Library.GetThumbnail(it));
            if (gen == _generation) img.Source = src;
        }
        catch { }
        finally { _thumbs.Release(); }
    }

    void Select(Border visual, LibraryItem it)
    {
        if (_selectedVisual != null) _selectedVisual.BorderBrush = Brushes.Transparent;
        _selectedVisual = visual;
        visual.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        ShowDetails(it);
    }

    public static void OpenItem(LibraryItem it)
    {
        try
        {
            if (!File.Exists(it.Path)) { Ui.Error("El archivo ya no existe en su ubicación."); Studio.Library.RaiseChanged(); return; }
            if (it.Kind == MediaKind.Image) Editor.EditorWindow.Open(Img.Load(it.Path), it.Path);
            else if (it.Kind == MediaKind.Video) VideoEditorWindow.Open(it.Path);
            else Shell.OpenFile(it.Path);
        }
        catch (Exception ex) { Log.Error("Abrir elemento", ex); Ui.Error(Loc.T("No se pudo abrir:") + "\n" + ex.Message); }
    }

    ContextMenu Menu(LibraryItem it)
    {
        var m = new ContextMenu();
        void Item(string text, Action a) { var mi = new MenuItem { Header = Loc.T(text) }; mi.Click += (_, _) => Guard(a); m.Items.Add(mi); }
        Item(it.Kind == MediaKind.Gif ? "Abrir" : "Abrir en el editor", () => OpenItem(it));
        Item("Abrir con la aplicación predeterminada", () => Shell.OpenFile(it.Path));
        if (it.Kind == MediaKind.Gif) Item("Editar / convertir GIF", () => VideoEditorWindow.Open(it.Path));
        Item("Copiar", () => Copy(it));
        Item("Abrir ubicación en el Explorador", () => Shell.RevealInExplorer(it.Path));
        m.Items.Add(new Separator());
        Item(it.Favorite ? "Quitar de favoritos" : "Marcar como favorito", () => { Studio.Library.Repo.SetFavorite(it.Id, !it.Favorite); Studio.Library.RaiseChanged(); });
        Item("Etiquetas…", () => EditTags(it));
        Item("Colección…", () => EditCollection(it));
        Item("Renombrar…", () => Rename(it));
        if (it.Kind == MediaKind.Image) Item("Agregar al reporte de errores", () => { Studio.ShowMain("qa"); Studio.Main?.QaPage.AddAttachment(it.Path); });
        m.Items.Add(new Separator());
        Item("Quitar de la biblioteca (conserva el archivo)", () => { Studio.Library.Repo.Remove(it.Id); Studio.Library.RaiseChanged(); });
        Item("Eliminar archivo…", () => Delete(it));
        return m;
    }

    static void Guard(Action a)
    {
        try { a(); } catch (Exception ex) { Log.Error("Biblioteca", ex); Ui.Error(ex.Message); }
    }

    static void Copy(LibraryItem it)
    {
        bool ok = it.Kind == MediaKind.Image ? ClipboardService.SetImage(Img.Load(it.Path)) : ClipboardService.SetFiles([it.Path]);
        if (!ok) Ui.Error("No se pudo acceder al portapapeles.");
    }

    static void EditTags(LibraryItem it)
    {
        var t = Ui.Prompt("Etiquetas", "Etiquetas separadas por comas:", it.Tags.Replace(",", ", "));
        if (t == null) return;
        Studio.Library.Repo.SetTags(it.Id, t); Studio.Library.RaiseChanged();
    }

    static void EditCollection(LibraryItem it)
    {
        var existing = Studio.Library.Repo.Collections();
        var t = Ui.Prompt("Colección", Loc.T("Nombre de la colección (vacío para quitarla).") + (existing.Count > 0 ? "\n" + Loc.T("Existentes:") + " " + string.Join(", ", existing) : ""), it.Collection);
        if (t == null) return;
        Studio.Library.Repo.SetCollection(it.Id, t); Studio.Library.RaiseChanged();
    }

    static void Rename(LibraryItem it)
    {
        var t = Ui.Prompt("Renombrar", "Nuevo nombre (sin extensión):", Path.GetFileNameWithoutExtension(it.Path));
        if (string.IsNullOrWhiteSpace(t)) return;
        Studio.Library.Rename(it, t);
    }

    static void Delete(LibraryItem it)
    {
        if (!Ui.Confirm(Loc.T("¿Eliminar «{0}»? El archivo se enviará a la Papelera de reciclaje.", it.Name), "Eliminar")) return;
        Studio.Library.Delete(it);
    }

    void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = Loc.T("Agregar archivos a la biblioteca"), Multiselect = true, Filter = "Imágenes, videos y GIF|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.mp4;*.webm;*.mkv;*.mov" };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames) Studio.Library.AddFile(f, notify: false);
        Studio.Library.RaiseChanged();
    }

    void ShowDetails(LibraryItem? it)
    {
        _selected = it;
        _details.Children.Clear();
        if (it == null) { _details.Children.Add(Form.Text("Selecciona un elemento para ver sus detalles y acciones. Doble clic para abrirlo.", "Muted")); return; }
        var img = new Image { Stretch = Stretch.Uniform, MaxHeight = 170 };
        _ = Task.Run(() => Studio.Library.GetThumbnail(it, 520)).ContinueWith(t => Dispatcher.BeginInvoke(() => img.Source = t.Result), TaskScheduler.Default);
        _details.Children.Add(img);
        var name = Form.Text(it.Name, "Body", false); name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(0, 10, 0, 4);
        _details.Children.Add(name);
        void Line(string label, string value) { if (!string.IsNullOrEmpty(value)) _details.Children.Add(Form.Text($"{Loc.T(label)}: {value}", "Muted", false)); }
        Line("Tipo", Loc.T(it.Kind switch { MediaKind.Video => "Video", MediaKind.Gif => "GIF", _ => "Imagen" }));
        Line("Resolución", it.Width > 0 ? $"{it.Width} × {it.Height} px" : "");
        Line("Tamaño", FileNaming.FormatSize(it.SizeBytes));
        Line("Fecha", it.CreatedUtc.ToLocalTime().ToString("g"));
        Line("Etiquetas", it.Tags.Replace(",", ", "));
        Line("Colección", it.Collection);
        Line("Ubicación", Path.GetDirectoryName(it.Path) ?? "");
        var wp = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        Button B(string text, Action a, string style = "SecondaryButton") { var b = Form.Button(text, () => Guard(a), style); b.Margin = new Thickness(0, 0, 6, 6); return b; }
        wp.Children.Add(B(it.Kind == MediaKind.Gif ? "Abrir" : "Editar", () => OpenItem(it), "PrimaryButton"));
        wp.Children.Add(B("Copiar", () => Copy(it)));
        wp.Children.Add(B(it.Favorite ? "★ Favorito" : "☆ Favorito", () => { Studio.Library.Repo.SetFavorite(it.Id, !it.Favorite); Studio.Library.RaiseChanged(); }));
        wp.Children.Add(B("Etiquetas…", () => EditTags(it)));
        wp.Children.Add(B("Colección…", () => EditCollection(it)));
        wp.Children.Add(B("Renombrar…", () => Rename(it)));
        wp.Children.Add(B("Explorador", () => Shell.RevealInExplorer(it.Path)));
        wp.Children.Add(B("Eliminar…", () => Delete(it), "DangerButton"));
        _details.Children.Add(wp);
    }
}

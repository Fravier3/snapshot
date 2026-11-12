using System.IO;
using System.Windows.Media.Imaging;
using FranCapture.Core.Diagnostics;
using FranCapture.Core.Library;
using FranCapture.Core.Settings;
using FranCapture.Core.Storage;

namespace FranCaptureStudio.Services;

public sealed class StorageService(Func<AppSettings> settings)
{
    AppSettings S => settings();

    public string CapturesFolder => Resolve(S.CapturesFolder, AppPaths.DefaultCapturesFolder);
    public string VideosFolder => Resolve(S.VideosFolder, AppPaths.DefaultVideosFolder);
    public string GifsFolder => Resolve(S.GifsFolder, AppPaths.DefaultGifsFolder);

    static string Resolve(string configured, string fallback)
    {
        var dir = string.IsNullOrWhiteSpace(configured) ? fallback : Environment.ExpandEnvironmentVariables(configured);
        try { Directory.CreateDirectory(dir); return dir; }
        catch (Exception ex)
        {
            Log.Warn($"Carpeta no disponible, se usa la predeterminada: {ex.Message}");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    int _counter;

    public string NextImagePath(bool forceDate = false, string? extension = null)
    {
        var name = FileNaming.Expand(S.FileNameTemplate, DateTime.Now, ++_counter, "Captura", forceDate);
        return FileNaming.UniquePath(CapturesFolder, name, extension ?? S.ImageFormat);
    }

    public string NextVideoPath(string extension)
    {
        var name = FileNaming.Expand("Grabación_{fecha}_{hora}", DateTime.Now);
        return FileNaming.UniquePath(extension == "gif" ? GifsFolder : VideosFolder, name, extension);
    }

    public void SaveImage(BitmapSource img, string path) => Img.Save(img, path, S.JpegQuality);

    /// <summary>Espacio ocupado por las carpetas administradas.</summary>
    public long UsedBytes()
    {
        long total = 0;
        foreach (var dir in new[] { CapturesFolder, VideosFolder, GifsFolder }.Distinct(StringComparer.OrdinalIgnoreCase))
            try { total += new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.TopDirectoryOnly).Sum(f => f.Length); } catch { }
        return total;
    }
}

/// <summary>Biblioteca local: registra los archivos creados por la aplicación y genera miniaturas.</summary>
public sealed class LibraryService : IDisposable
{
    public LibraryRepository Repo { get; }
    public event Action? Changed;

    public LibraryService(string dbPath) => Repo = new LibraryRepository(dbPath);

    public LibraryItem? AddFile(string path, bool notify = true)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            var kind = LibraryItem.KindFromPath(path);
            var (w, h) = kind == MediaKind.Video ? (0, 0) : Img.ReadSize(path);
            var item = Repo.Upsert(new LibraryItem { Path = fi.FullName, Kind = kind, CreatedUtc = fi.LastWriteTimeUtc, Width = w, Height = h, SizeBytes = fi.Length });
            if (kind == MediaKind.Video) _ = FillVideoInfoAsync(item);
            if (notify) RaiseChanged();
            return item;
        }
        catch (Exception ex) { Log.Error("Biblioteca: añadir", ex); return null; }
    }

    async Task FillVideoInfoAsync(LibraryItem item)
    {
        try
        {
            if (!FfmpegRunner.IsAvailable) return;
            var info = await FfmpegRunner.ProbeAsync(item.Path);
            if (info.Width > 0)
            {
                item.Width = info.Width; item.Height = info.Height;
                Repo.Upsert(item);
                RaiseChanged();
            }
        }
        catch { }
    }

    public void RaiseChanged()
    {
        var app = System.Windows.Application.Current;
        if (app == null || app.Dispatcher.CheckAccess()) Changed?.Invoke();
        else app.Dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    /// <summary>Elementos cuyo archivo existe realmente; los registros huérfanos se eliminan de la base de datos.</summary>
    public List<LibraryItem> Query(LibraryQuery q)
    {
        var items = Repo.Query(q);
        var result = new List<LibraryItem>(items.Count);
        foreach (var it in items)
        {
            if (File.Exists(it.Path)) result.Add(it);
            else Repo.Remove(it.Id);
        }
        return result;
    }

    public string ThumbPath(LibraryItem item) => Path.Combine(AppPaths.ThumbsDir, $"{item.Id}_{item.SizeBytes}.png");

    public BitmapSource? GetThumbnail(LibraryItem item, int width = 360)
    {
        try
        {
            if (item.Kind != MediaKind.Video) return Img.LoadThumbnail(item.Path, width);
            var cover = Path.ChangeExtension(item.Path, null) + ".portada.png";
            if (File.Exists(cover)) return Img.LoadThumbnail(cover, width);
            var thumb = ThumbPath(item);
            if (!File.Exists(thumb) && FfmpegRunner.IsAvailable)
                FfmpegRunner.Run(FranCapture.Core.Video.Ffmpeg.ExtractFrameArgs(item.Path, 0.3, thumb), 15000);
            return File.Exists(thumb) ? Img.LoadThumbnail(thumb, width) : null;
        }
        catch { return null; }
    }

    public void Rename(LibraryItem item, string newName)
    {
        newName = FileNaming.Sanitize(Path.GetFileNameWithoutExtension(newName));
        var target = Path.Combine(Path.GetDirectoryName(item.Path)!, newName + Path.GetExtension(item.Path));
        if (string.Equals(target, item.Path, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(target)) throw new IOException("Ya existe un archivo con ese nombre.");
        File.Move(item.Path, target);
        Repo.UpdatePath(item.Id, target);
        item.Path = target;
        RaiseChanged();
    }

    /// <summary>Elimina el archivo (a la Papelera) y su registro. Solo se llama tras confirmación del usuario.</summary>
    public void Delete(LibraryItem item)
    {
        if (File.Exists(item.Path)) Shell.Recycle(item.Path);
        Repo.Remove(item.Id);
        RaiseChanged();
    }

    /// <summary>Olvida todo el historial sin tocar los archivos.</summary>
    public void ClearHistory()
    {
        Repo.Clear();
        RaiseChanged();
    }

    public void Dispose() => Repo.Dispose();
}

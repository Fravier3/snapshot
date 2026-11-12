using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SD = System.Drawing;
using SDI = System.Drawing.Imaging;

namespace FranCaptureStudio.Services;

/// <summary>Utilidades de imagen. En toda la aplicación las imágenes son BitmapSource congelados, Bgra32 a 96 ppp.</summary>
public static class Img
{
    public static BitmapSource Normalize(BitmapSource src)
    {
        if (src.Format == PixelFormats.Bgra32 && Math.Abs(src.DpiX - 96) < 0.5 && Math.Abs(src.DpiY - 96) < 0.5)
        {
            if (!src.IsFrozen && src.CanFreeze) src.Freeze();
            if (src.IsFrozen) return src;
        }
        return FromPixels(Pixels(src), src.PixelWidth, src.PixelHeight);
    }

    /// <summary>Píxeles BGRA (alfa sin premultiplicar), sin relleno de fila.</summary>
    public static byte[] Pixels(BitmapSource src)
    {
        BitmapSource s = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        var buf = new byte[s.PixelWidth * s.PixelHeight * 4];
        s.CopyPixels(buf, s.PixelWidth * 4, 0);
        return buf;
    }

    public static BitmapSource FromPixels(byte[] bgra, int width, int height)
    {
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        bmp.Freeze();
        return bmp;
    }

    public static BitmapSource FromGdi(SD.Bitmap bmp)
    {
        var rect = new SD.Rectangle(0, 0, bmp.Width, bmp.Height);
        bool alpha = SD.Image.IsAlphaPixelFormat(bmp.PixelFormat);
        var data = bmp.LockBits(rect, SDI.ImageLockMode.ReadOnly, alpha ? SDI.PixelFormat.Format32bppArgb : SDI.PixelFormat.Format32bppRgb);
        try
        {
            var buf = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, buf, y * bmp.Width * 4, bmp.Width * 4);
            if (!alpha) for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
            return FromPixels(buf, bmp.Width, bmp.Height);
        }
        finally { bmp.UnlockBits(data); }
    }

    public static SD.Bitmap ToGdi(BitmapSource src)
    {
        var px = Pixels(src);
        var bmp = new SD.Bitmap(src.PixelWidth, src.PixelHeight, SDI.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new SD.Rectangle(0, 0, bmp.Width, bmp.Height), SDI.ImageLockMode.WriteOnly, SDI.PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bmp.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(px, y * bmp.Width * 4, data.Scan0 + y * data.Stride, bmp.Width * 4);
        }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    /// <summary>Carga completa en memoria (no bloquea el archivo).</summary>
    public static BitmapSource Load(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Load(fs);
        }
        catch (Exception) when (Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase) && FfmpegRunner.IsAvailable)
        {
            // Sin extensión WebP de Windows: decodificar con FFmpeg.
            var tmp = Path.Combine(AppPaths.TempDir, Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var r = FfmpegRunner.Run(FranCapture.Core.Video.Ffmpeg.ConvertImageArgs(path, tmp, 100));
                if (r.ExitCode != 0) throw new InvalidDataException("No se pudo decodificar la imagen WebP.");
                using var fs = File.OpenRead(tmp);
                return Load(fs);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
    }

    public static BitmapSource Load(Stream stream)
    {
        var dec = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        return Normalize(dec.Frames[0]);
    }

    public static BitmapSource? LoadThumbnail(string path, int maxWidth)
    {
        try
        {
            var bi = new BitmapImage();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.DecodePixelWidth = maxWidth;
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    public static (int Width, int Height) ReadSize(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return (dec.Frames[0].PixelWidth, dec.Frames[0].PixelHeight);
        }
        catch { return (0, 0); }
    }

    public static readonly string[] WritableFormats = ["png", "jpg", "webp", "bmp", "gif", "tiff"];
    public static readonly string OpenFilter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|Todos los archivos|*.*";
    public static readonly string SaveFilter = "PNG|*.png|JPEG|*.jpg|WebP|*.webp|BMP|*.bmp|GIF|*.gif|TIFF|*.tiff";

    public static byte[] Encode(BitmapSource img, string format, int quality = 90, BitmapMetadata? metadata = null)
    {
        BitmapEncoder enc = format.ToLowerInvariant().TrimStart('.') switch
        {
            "jpg" or "jpeg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) },
            "bmp" => new BmpBitmapEncoder(),
            "gif" => new GifBitmapEncoder(),
            "tif" or "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            _ => new PngBitmapEncoder(),
        };
        BitmapSource src = img;
        if (enc is JpegBitmapEncoder or BmpBitmapEncoder)
            src = Flatten(img, Colors.White); // sin canal alfa: fondo blanco
        BitmapFrame frame;
        try { frame = metadata != null ? BitmapFrame.Create(src, null, metadata, null) : BitmapFrame.Create(src); }
        catch { frame = BitmapFrame.Create(src); }
        enc.Frames.Add(frame);
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Guarda la imagen según la extensión de la ruta. Se recodifica desde los píxeles, por lo que el archivo
    /// resultante no contiene metadatos (EXIF, autor, ubicación) salvo que se pasen expresamente.
    /// </summary>
    public static void Save(BitmapSource img, string path, int quality = 90, BitmapMetadata? metadata = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext == "webp")
        {
            if (!FfmpegRunner.IsAvailable) throw new InvalidOperationException("Para escribir WebP se necesita el componente FFmpeg incluido con la aplicación.");
            var tmp = Path.Combine(AppPaths.TempDir, Guid.NewGuid().ToString("N") + ".png");
            try
            {
                File.WriteAllBytes(tmp, Encode(img, "png"));
                var r = FfmpegRunner.Run(FranCapture.Core.Video.Ffmpeg.ConvertImageArgs(tmp, path, quality));
                if (r.ExitCode != 0 || !File.Exists(path)) throw new IOException("FFmpeg no pudo escribir el archivo WebP. " + r.Error);
            }
            finally { try { File.Delete(tmp); } catch { } }
            return;
        }
        var bytes = Encode(img, ext, quality, metadata);
        var tmpOut = path + ".tmp~";
        File.WriteAllBytes(tmpOut, bytes);
        File.Move(tmpOut, path, true);
    }

    public static BitmapSource Flatten(BitmapSource img, Color background)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
            dc.DrawImage(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
        }
        return Render(dv, img.PixelWidth, img.PixelHeight);
    }

    public static BitmapSource Render(Visual visual, int width, int height)
    {
        var rtb = new RenderTargetBitmap(Math.Max(1, width), Math.Max(1, height), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return Normalize(rtb);
    }

    public static BitmapSource Draw(int width, int height, Action<DrawingContext> draw)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) draw(dc);
        return Render(dv, width, height);
    }

    public static BitmapSource Crop(BitmapSource img, Int32Rect r)
    {
        int x = Math.Clamp(r.X, 0, img.PixelWidth - 1), y = Math.Clamp(r.Y, 0, img.PixelHeight - 1);
        int w = Math.Clamp(r.Width, 1, img.PixelWidth - x), h = Math.Clamp(r.Height, 1, img.PixelHeight - y);
        return Normalize(new CroppedBitmap(img, new Int32Rect(x, y, w, h)));
    }

    public static BitmapSource Resize(BitmapSource img, int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        return Draw(width, height, dc => dc.DrawImage(img, new Rect(0, 0, width, height)));
    }

    public static BitmapSource Rotate(BitmapSource img, int degrees) =>
        Normalize(new TransformedBitmap(img, new RotateTransform(degrees)));

    public static BitmapSource Flip(BitmapSource img, bool horizontal) =>
        Normalize(new TransformedBitmap(img, new ScaleTransform(horizontal ? -1 : 1, horizontal ? 1 : -1)));

    public static BitmapSource AddBorder(BitmapSource img, int thickness, Color color)
    {
        int w = img.PixelWidth + thickness * 2, h = img.PixelHeight + thickness * 2;
        return Draw(w, h, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, w, h));
            dc.DrawImage(img, new Rect(thickness, thickness, img.PixelWidth, img.PixelHeight));
        });
    }

    public static BitmapSource AddShadow(BitmapSource img, int size = 24)
    {
        int w = img.PixelWidth + size * 2, h = img.PixelHeight + size * 2;
        // sombra: rectángulo negro desenfocado bajo la imagen, sobre fondo transparente
        var px = new byte[w * h * 4];
        int off = size / 3;
        for (int y = size + off; y < size + off + img.PixelHeight && y < h; y++)
            for (int x = size; x < size + img.PixelWidth; x++) px[(y * w + x) * 4 + 3] = 150;
        FranCapture.Core.Imaging.PixelOps.BoxBlur(px, w, h, Math.Max(2, size / 3));
        var shadow = FromPixels(px, w, h);
        return Draw(w, h, dc =>
        {
            dc.DrawImage(shadow, new Rect(0, 0, w, h));
            dc.DrawImage(img, new Rect(size, size, img.PixelWidth, img.PixelHeight));
        });
    }

    public static BitmapSource AddWatermark(BitmapSource img, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return img;
        int w = img.PixelWidth, h = img.PixelHeight;
        return Draw(w, h, dc =>
        {
            dc.DrawImage(img, new Rect(0, 0, w, h));
            double size = Math.Clamp(Math.Min(w, h) / 28.0, 11, 48);
            var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size, Brushes.White, 1.0);
            double x = w - ft.Width - size * 0.8, y = h - ft.Height - size * 0.6;
            if (x < 2) x = 2;
            var geo = ft.BuildGeometry(new Point(x, y));
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Math.Max(2, size / 7)) { LineJoin = PenLineJoin.Round }, geo);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(215, 255, 255, 255)), null, geo);
        });
    }
}

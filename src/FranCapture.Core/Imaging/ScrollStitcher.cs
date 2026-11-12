namespace FranCapture.Core.Imaging;

public enum StitchStatus
{
    Appended,      // se detectó desplazamiento y se añadieron filas nuevas
    NoMovement,    // el fotograma es idéntico al anterior (fin del contenido)
    NoMatch,       // no se encontró solapamiento fiable (contenido incompatible)
}

/// <summary>
/// Ensambla una captura extendida a partir de fotogramas consecutivos del mismo tamaño tomados mientras
/// el contenido se desplaza verticalmente. Detecta cabeceras/pies fijos, calcula el desplazamiento
/// comparando huellas por fila y evita duplicar segmentos. Si no hay solapamiento fiable devuelve
/// NoMatch en vez de producir una imagen dañada.
/// </summary>
public sealed class ScrollStitcher
{
    public int Width { get; }
    public int FrameHeight { get; }
    readonly List<byte[]> _rows = [];      // filas del resultado (cada una Width*4 bytes)
    byte[]? _prev;
    ulong[]? _prevHash;
    int _header, _footer = -1;
    int _lastShift;
    public int FramesAccepted { get; private set; }
    public int TotalHeight => _rows.Count + (_footerRows?.Count ?? 0);
    List<byte[]>? _footerRows;

    /// <summary>Fracción mínima de filas solapadas que deben coincidir.</summary>
    public double MatchThreshold { get; set; } = 0.92;
    /// <summary>Altura máxima del resultado, para proteger la memoria.</summary>
    public int MaxHeight { get; set; } = 30000;

    public ScrollStitcher(int width, int frameHeight)
    {
        if (width < 8 || frameHeight < 16) throw new ArgumentException("Región demasiado pequeña para captura extendida.");
        Width = width; FrameHeight = frameHeight;
    }

    ulong[] HashRows(byte[] frame)
    {
        // Se ignora el 6 % derecho (barras de desplazamiento) y se muestrea cada píxel del resto.
        int x1 = Math.Max(4, (int)(Width * 0.94));
        var h = new ulong[FrameHeight];
        for (int y = 0; y < FrameHeight; y++)
        {
            ulong hash = 14695981039346656037UL;
            int off = y * Width * 4;
            for (int x = 0; x < x1; x++)
            {
                int i = off + x * 4;
                uint px = (uint)(frame[i] | frame[i + 1] << 8 | frame[i + 2] << 16);
                hash = (hash ^ px) * 1099511628211UL;
            }
            h[y] = hash;
        }
        return h;
    }

    byte[] Row(byte[] frame, int y)
    {
        var r = new byte[Width * 4];
        Buffer.BlockCopy(frame, y * Width * 4, r, 0, r.Length);
        return r;
    }

    public StitchStatus AddFrame(byte[] bgra)
    {
        if (bgra.Length != Width * FrameHeight * 4) throw new ArgumentException("Tamaño de fotograma inesperado.");
        var hash = HashRows(bgra);
        if (_prev == null)
        {
            _prev = bgra; _prevHash = hash;
            for (int y = 0; y < FrameHeight; y++) _rows.Add(Row(bgra, y));
            FramesAccepted = 1;
            return StitchStatus.Appended;
        }

        var ph = _prevHash!;
        bool identical = true;
        for (int y = 0; y < FrameHeight; y++) if (ph[y] != hash[y]) { identical = false; break; }
        if (identical) return StitchStatus.NoMovement;

        // Zonas fijas (cabecera/pie pegajosos): filas idénticas en la misma posición.
        int header = 0;
        while (header < FrameHeight && ph[header] == hash[header]) header++;
        int footer = 0;
        while (footer < FrameHeight - header && ph[FrameHeight - 1 - footer] == hash[FrameHeight - 1 - footer]) footer++;
        if (header > FrameHeight * 0.6) header = 0;   // demasiado "fijo": no fiarse
        if (footer > FrameHeight * 0.4) footer = 0;

        int bodyTop = header, bodyBottom = FrameHeight - footer; // [bodyTop, bodyBottom)
        int body = bodyBottom - bodyTop;
        if (body < 12) return StitchStatus.NoMatch;

        int best = 0; double bestScore = 0;
        int minOverlap = Math.Max(8, body / 10);
        for (int d = 1; d <= body - minOverlap; d++)
        {
            int overlap = body - d, match = 0, informative = 0;
            for (int k = 0; k < overlap; k++)
            {
                ulong hp = ph[bodyTop + d + k], hn = hash[bodyTop + k];
                if (hp == hn) match++;
                if (k > 0 && hn != hash[bodyTop + k - 1]) informative++;
            }
            if (informative < Math.Min(6, overlap / 4)) continue; // zona casi uniforme: coincidencia sin valor
            double score = (double)match / overlap;
            if (score < MatchThreshold) continue;
            bool better = score > bestScore + 1e-9
                || (Math.Abs(score - bestScore) <= 1e-9 && _lastShift > 0 && Math.Abs(d - _lastShift) < Math.Abs(best - _lastShift));
            if (best == 0 || better) { best = d; bestScore = score; }
        }
        if (best == 0) return StitchStatus.NoMatch;

        if (FramesAccepted == 1)
        {
            // Primer empalme: separar el pie fijo del primer fotograma para colocarlo al final.
            _header = header; _footer = footer;
            if (footer > 0)
            {
                _footerRows = _rows.GetRange(FrameHeight - footer, footer);
                _rows.RemoveRange(FrameHeight - footer, footer);
            }
        }
        else if (_footer >= 0 && footer < _footer)
        {
            // El pie detectado ahora es menor que el asumido: usar el menor para no perder contenido.
            footer = Math.Min(footer, _footer);
        }
        int usableBottom = FrameHeight - Math.Max(_footer, 0);
        int newFrom = Math.Max(usableBottom - best, bodyTop);
        for (int y = newFrom; y < usableBottom && _rows.Count < MaxHeight; y++) _rows.Add(Row(bgra, y));
        if (_footerRows != null && _footer > 0)
            for (int i = 0; i < _footer; i++) _footerRows[i] = Row(bgra, FrameHeight - _footer + i);

        _prev = bgra; _prevHash = hash; _lastShift = best; FramesAccepted++;
        return StitchStatus.Appended;
    }

    public bool ReachedMaxHeight => _rows.Count >= MaxHeight;

    /// <summary>Imagen final BGRA.</summary>
    public (byte[] Pixels, int Width, int Height) Build()
    {
        var all = new List<byte[]>(_rows);
        if (_footerRows != null) all.AddRange(_footerRows);
        var buf = new byte[all.Count * Width * 4];
        for (int y = 0; y < all.Count; y++) Buffer.BlockCopy(all[y], 0, buf, y * Width * 4, Width * 4);
        return (buf, Width, all.Count);
    }
}

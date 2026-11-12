namespace FranCapture.Core.Imaging;

/// <summary>Operaciones sobre búferes BGRA de 32 bits (4 bytes por píxel, sin relleno de fila).</summary>
public static class PixelOps
{
    /// <summary>Pixelado: cada bloque se sustituye por su color medio. La información original no se conserva.</summary>
    public static void Pixelate(byte[] bgra, int width, int height, int block)
    {
        block = Math.Max(2, block);
        for (int by = 0; by < height; by += block)
            for (int bx = 0; bx < width; bx += block)
            {
                int x1 = Math.Min(bx + block, width), y1 = Math.Min(by + block, height);
                long b = 0, g = 0, r = 0, a = 0; int n = 0;
                for (int y = by; y < y1; y++)
                    for (int x = bx; x < x1; x++)
                    {
                        int i = (y * width + x) * 4;
                        b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2]; a += bgra[i + 3]; n++;
                    }
                byte ab = (byte)(b / n), ag = (byte)(g / n), ar = (byte)(r / n), aa = (byte)(a / n);
                for (int y = by; y < y1; y++)
                    for (int x = bx; x < x1; x++)
                    {
                        int i = (y * width + x) * 4;
                        bgra[i] = ab; bgra[i + 1] = ag; bgra[i + 2] = ar; bgra[i + 3] = aa;
                    }
            }
    }

    /// <summary>Desenfoque de caja en tres pasadas (aproxima un gaussiano).</summary>
    public static void BoxBlur(byte[] bgra, int width, int height, int radius, int passes = 3)
    {
        if (radius < 1 || width < 2 || height < 2) return;
        var tmp = new byte[bgra.Length];
        for (int p = 0; p < passes; p++)
        {
            BlurPass(bgra, tmp, width, height, radius, true);
            BlurPass(tmp, bgra, width, height, radius, false);
        }
    }

    static void BlurPass(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
    {
        int outer = horizontal ? h : w, inner = horizontal ? w : h;
        int win = r * 2 + 1;
        for (int o = 0; o < outer; o++)
        {
            for (int ch = 0; ch < 4; ch++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++)
                {
                    int idx = Math.Clamp(k, 0, inner - 1);
                    sum += src[(horizontal ? o * w + idx : idx * w + o) * 4 + ch];
                }
                for (int i = 0; i < inner; i++)
                {
                    dst[(horizontal ? o * w + i : i * w + o) * 4 + ch] = (byte)(sum / win);
                    int add = Math.Min(i + r + 1, inner - 1), rem = Math.Max(i - r, 0);
                    sum += src[(horizontal ? o * w + add : add * w + o) * 4 + ch] - src[(horizontal ? o * w + rem : rem * w + o) * 4 + ch];
                }
            }
        }
    }

    /// <summary>
    /// Eliminación de fondo por color: rellena desde los bordes todo lo que se parezca al color del borde
    /// (tolerancia 0-255) y lo vuelve transparente. Funciona con fondos uniformes; no requiere modelos.
    /// Devuelve el número de píxeles vueltos transparentes.
    /// </summary>
    public static int RemoveBackgroundByColor(byte[] bgra, int width, int height, int tolerance)
    {
        if (width < 1 || height < 1) return 0;
        var visited = new bool[width * height];
        var stack = new Stack<int>();
        int tol2 = tolerance * tolerance * 3;
        int removed = 0;

        void Seed(int x, int y)
        {
            int idx = y * width + x;
            if (visited[idx]) return;
            int i = idx * 4;
            int sb = bgra[i], sg = bgra[i + 1], sr = bgra[i + 2];
            visited[idx] = true; stack.Push(idx);
            while (stack.Count > 0)
            {
                int cur = stack.Pop();
                int ci = cur * 4;
                if (bgra[ci + 3] != 0) removed++;
                bgra[ci] = 0; bgra[ci + 1] = 0; bgra[ci + 2] = 0; bgra[ci + 3] = 0;
                int cx = cur % width, cy = cur / width;
                Try(cx - 1, cy); Try(cx + 1, cy); Try(cx, cy - 1); Try(cx, cy + 1);
            }

            void Try(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) return;
                int n = ny * width + nx;
                if (visited[n]) return;
                int ni = n * 4;
                int db = bgra[ni] - sb, dg = bgra[ni + 1] - sg, dr = bgra[ni + 2] - sr;
                if (db * db + dg * dg + dr * dr > tol2) return;
                visited[n] = true; stack.Push(n);
            }
        }

        for (int x = 0; x < width; x++) { Seed(x, 0); Seed(x, height - 1); }
        for (int y = 0; y < height; y++) { Seed(0, y); Seed(width - 1, y); }
        return removed;
    }

    /// <summary>Aplica una máscara (0-255 por píxel) como canal alfa.</summary>
    public static void ApplyAlphaMask(byte[] bgra, byte[] mask)
    {
        for (int i = 0; i < mask.Length; i++)
        {
            int a = mask[i];
            int p = i * 4;
            bgra[p + 3] = (byte)(bgra[p + 3] * a / 255);
        }
    }
}

public sealed class DiffResult
{
    public required byte[] Highlight { get; init; }   // imagen B con las diferencias marcadas en magenta
    public long DifferentPixels { get; init; }
    public double Percent { get; init; }
    public bool SameSize { get; init; }
}

public static class ImageDiff
{
    /// <summary>Compara dos imágenes BGRA. Si los tamaños difieren se compara el área común y el resto cuenta como diferente.</summary>
    public static DiffResult Compare(byte[] a, int aw, int ah, byte[] b, int bw, int bh, int threshold = 24)
    {
        var o = (byte[])b.Clone();
        long diff = 0;
        for (int y = 0; y < bh; y++)
            for (int x = 0; x < bw; x++)
            {
                int bi = (y * bw + x) * 4;
                bool different;
                if (x >= aw || y >= ah) different = true;
                else
                {
                    int ai = (y * aw + x) * 4;
                    different = Math.Abs(a[ai] - b[bi]) > threshold || Math.Abs(a[ai + 1] - b[bi + 1]) > threshold || Math.Abs(a[ai + 2] - b[bi + 2]) > threshold;
                }
                if (different)
                {
                    diff++;
                    o[bi] = (byte)((o[bi] + 255 * 2) / 3); o[bi + 1] = (byte)(o[bi + 1] / 3); o[bi + 2] = (byte)((o[bi + 2] + 255 * 2) / 3); o[bi + 3] = 255;
                }
                else
                {
                    // atenuar lo que no cambió para que las diferencias destaquen
                    int gray = (o[bi] + o[bi + 1] + o[bi + 2]) / 3;
                    o[bi] = (byte)((o[bi] + gray) / 2); o[bi + 1] = (byte)((o[bi + 1] + gray) / 2); o[bi + 2] = (byte)((o[bi + 2] + gray) / 2);
                }
            }
        long total = (long)Math.Max(aw, bw) * Math.Max(ah, bh);
        long outside = total - (long)bw * bh;
        return new DiffResult { Highlight = o, DifferentPixels = diff + outside, Percent = total == 0 ? 0 : (diff + outside) * 100.0 / total, SameSize = aw == bw && ah == bh };
    }
}

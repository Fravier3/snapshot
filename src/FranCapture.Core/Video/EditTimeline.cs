namespace FranCapture.Core.Video;

public sealed class TimelineSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public bool Removed { get; set; }
    public double Length => End - Start;
}

/// <summary>
/// Modelo de la línea de tiempo del editor: el video se divide en segmentos contiguos;
/// cada uno puede conservarse o eliminarse. Recortar inicio/final equivale a dividir y eliminar.
/// </summary>
public sealed class EditTimeline
{
    public double Duration { get; }
    public List<TimelineSegment> Segments { get; private set; }
    const double MinLen = 0.05;

    public EditTimeline(double duration)
    {
        Duration = Math.Max(0, duration);
        Segments = [new TimelineSegment { Start = 0, End = Duration }];
    }

    public int IndexAt(double t)
    {
        for (int i = 0; i < Segments.Count; i++)
            if (t >= Segments[i].Start && (t < Segments[i].End || i == Segments.Count - 1)) return i;
        return 0;
    }

    /// <summary>Divide el segmento que contiene t. Devuelve false si el corte quedaría pegado a un borde.</summary>
    public bool Split(double t)
    {
        int i = IndexAt(t);
        var s = Segments[i];
        if (t - s.Start < MinLen || s.End - t < MinLen) return false;
        Segments.Insert(i + 1, new TimelineSegment { Start = t, End = s.End, Removed = s.Removed });
        s.End = t;
        return true;
    }

    public void SetRemoved(int index, bool removed)
    {
        if (index >= 0 && index < Segments.Count) Segments[index].Removed = removed;
    }

    /// <summary>Recorta el inicio: todo lo anterior a t se elimina.</summary>
    public void TrimStart(double t)
    {
        t = Math.Clamp(t, 0, Duration);
        Split(t);
        foreach (var s in Segments) if (s.End <= t + 1e-6) s.Removed = true;
    }

    /// <summary>Recorta el final: todo lo posterior a t se elimina.</summary>
    public void TrimEnd(double t)
    {
        t = Math.Clamp(t, 0, Duration);
        Split(t);
        foreach (var s in Segments) if (s.Start >= t - 1e-6) s.Removed = true;
    }

    public void Reset() => Segments = [new TimelineSegment { Start = 0, End = Duration }];

    /// <summary>Segmentos que se conservan, fusionando los contiguos.</summary>
    public List<(double Start, double End)> KeptRanges()
    {
        var list = new List<(double, double)>();
        foreach (var s in Segments)
        {
            if (s.Removed || s.Length < 1e-6) continue;
            if (list.Count > 0 && Math.Abs(list[^1].Item2 - s.Start) < 1e-6) list[^1] = (list[^1].Item1, s.End);
            else list.Add((s.Start, s.End));
        }
        return list;
    }

    public double KeptDuration => KeptRanges().Sum(r => r.End - r.Start);

    /// <summary>Convierte un instante del video original al instante equivalente tras eliminar los cortes.</summary>
    public double SourceToOutput(double t)
    {
        double acc = 0;
        foreach (var (s, e) in KeptRanges())
        {
            if (t <= s) return acc;
            if (t < e) return acc + (t - s);
            acc += e - s;
        }
        return acc;
    }

    /// <summary>Intervalo de una superposición en la línea de tiempo de salida; null si cae completamente en zonas eliminadas.</summary>
    public (double Start, double End)? MapInterval(double start, double end)
    {
        double a = SourceToOutput(start), b = SourceToOutput(end);
        return b - a < 0.02 ? null : (a, b);
    }

    public EditTimeline Clone()
    {
        var c = new EditTimeline(Duration);
        c.Segments = Segments.Select(s => new TimelineSegment { Start = s.Start, End = s.End, Removed = s.Removed }).ToList();
        return c;
    }
}

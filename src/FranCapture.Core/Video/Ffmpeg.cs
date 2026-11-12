using System.Globalization;
using System.Text.RegularExpressions;

namespace FranCapture.Core.Video;

public sealed record VideoEncoder(string Name, string FfmpegName, string PixelFormat, bool Hardware)
{
    // Orden de preferencia para H.264. libx264 (GPL) no se usa: la compilación de FFmpeg distribuida es LGPL.
    public static readonly VideoEncoder Nvenc = new("NVIDIA NVENC", "h264_nvenc", "yuv420p", true);
    public static readonly VideoEncoder Qsv = new("Intel Quick Sync", "h264_qsv", "nv12", true);
    public static readonly VideoEncoder Amf = new("AMD AMF", "h264_amf", "nv12", true);
    public static readonly VideoEncoder MediaFoundation = new("Windows Media Foundation", "h264_mf", "nv12", false);
    public static readonly VideoEncoder OpenH264 = new("OpenH264 (software)", "libopenh264", "yuv420p", false);
    public static readonly VideoEncoder Mpeg4 = new("MPEG-4 (software)", "mpeg4", "yuv420p", false);
    public static readonly VideoEncoder Vp9 = new("VP9 (software)", "libvpx-vp9", "yuv420p", false);
    public static readonly VideoEncoder Vp8 = new("VP8 (software)", "libvpx", "yuv420p", false);

    public static IReadOnlyList<VideoEncoder> H264Candidates(bool hardwareAllowed) => hardwareAllowed
        ? [Nvenc, Qsv, Amf, MediaFoundation, OpenH264, Mpeg4]
        : [OpenH264, MediaFoundation, Mpeg4];

    public static IReadOnlyList<VideoEncoder> WebmCandidates => [Vp9, Vp8];
}

public sealed class MediaInfo
{
    public double DurationSeconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public bool HasAudio { get; set; }
    public bool HasVideo { get; set; }
}

public sealed record OverlaySpec(string PngPath, double Start, double End);

public sealed class EditExportOptions
{
    public required string Input { get; init; }
    public required string Output { get; init; }
    public required IReadOnlyList<(double Start, double End)> Segments { get; init; }
    public double Speed { get; init; } = 1.0;
    public bool RemoveAudio { get; init; }
    public bool InputHasAudio { get; init; }
    public IReadOnlyList<OverlaySpec> Overlays { get; init; } = [];
    public VideoEncoder Encoder { get; init; } = VideoEncoder.OpenH264;
    public string Quality { get; init; } = "high";
    public int Width { get; init; }
    public int Height { get; init; }
    public double Fps { get; init; } = 30;
    public int? ScaleHeight { get; init; }
    public int GifMaxWidth { get; init; } = 800;
}

/// <summary>Construcción de líneas de comandos de FFmpeg. Sin estado y comprobable con pruebas unitarias.</summary>
public static class Ffmpeg
{
    static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    public static string Q(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

    public static int BitrateKbps(int width, int height, double fps, string quality)
    {
        double bpp = quality switch { "low" => 0.045, "medium" => 0.08, _ => 0.14 };
        return (int)Math.Clamp(width * (double)height * fps * bpp / 1000.0, 400, 60000);
    }

    public static string EncoderArgs(VideoEncoder enc, int width, int height, double fps, string quality)
    {
        int kbps = BitrateKbps(width, height, fps, quality);
        string rate = $"-b:v {kbps}k -maxrate {kbps * 2}k -bufsize {kbps * 2}k";
        int gop = (int)Math.Round(fps * 2);
        return enc.FfmpegName switch
        {
            "h264_nvenc" => $"-c:v h264_nvenc -preset p4 -rc vbr {rate} -g {gop} -pix_fmt yuv420p",
            "h264_qsv" => $"-c:v h264_qsv -preset faster {rate} -g {gop} -pix_fmt nv12",
            "h264_amf" => $"-c:v h264_amf -quality speed -rc vbr_peak {rate} -g {gop} -pix_fmt nv12",
            "h264_mf" => $"-c:v h264_mf -b:v {kbps}k -g {gop} -pix_fmt nv12",
            "libopenh264" => $"-c:v libopenh264 -b:v {kbps}k -g {gop} -pix_fmt yuv420p",
            "libvpx-vp9" => $"-c:v libvpx-vp9 -b:v {kbps}k -deadline realtime -cpu-used 8 -row-mt 1 -g {gop} -pix_fmt yuv420p",
            "libvpx" => $"-c:v libvpx -b:v {kbps}k -deadline realtime -cpu-used 8 -g {gop} -pix_fmt yuv420p",
            _ => $"-c:v mpeg4 -q:v {(quality == "low" ? 8 : quality == "medium" ? 5 : 3)} -g {gop} -pix_fmt yuv420p",
        };
    }

    /// <summary>Prueba rápida de disponibilidad de un codificador.</summary>
    public static string ProbeEncoderArgs(VideoEncoder enc) =>
        $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=320x240:r=30 -frames:v 6 {EncoderArgs(enc, 320, 240, 30, "medium")} -f null -";

    /// <summary>Grabación: fotogramas BGRA por stdin hacia un Matroska temporal (recuperable si se interrumpe).</summary>
    public static string RecordArgs(int width, int height, int fps, VideoEncoder enc, string quality, int? scaleHeight, string outputMkv)
    {
        string vf = "";
        int ow = width, oh = height;
        if (scaleHeight is { } sh && sh < height)
        {
            oh = sh & ~1; ow = (int)Math.Round(width * (double)oh / height) & ~1;
            vf = $"-vf scale={ow}:{oh}:flags=bicubic ";
        }
        return $"-hide_banner -loglevel error -y -f rawvideo -pix_fmt bgr0 -video_size {width}x{height} -framerate {fps} -i pipe:0 " +
               $"{vf}{EncoderArgs(enc, ow, oh, fps, quality)} -r {fps} -f matroska -flush_packets 1 {Q(outputMkv)}";
    }

    /// <summary>Finalización: combina el video temporal con las pistas de audio grabadas y produce el archivo final.</summary>
    public static string MuxArgs(string videoMkv, IReadOnlyList<string> audioWavs, string output)
    {
        bool webm = output.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
        string acodec = webm ? "-c:a libopus -b:a 128k" : "-c:a aac -b:a 160k";
        string tail = webm ? "" : " -movflags +faststart";
        var inputs = $"-i {Q(videoMkv)}" + string.Concat(audioWavs.Select(a => $" -i {Q(a)}"));
        if (audioWavs.Count == 0)
            return $"-hide_banner -loglevel error -y {inputs} -map 0:v -c:v copy{tail} {Q(output)}";
        if (audioWavs.Count == 1)
            return $"-hide_banner -loglevel error -y {inputs} -map 0:v -map 1:a -c:v copy {acodec} -shortest{tail} {Q(output)}";
        var mixIn = string.Concat(Enumerable.Range(1, audioWavs.Count).Select(i => $"[{i}:a]"));
        return $"-hide_banner -loglevel error -y {inputs} -filter_complex \"{mixIn}amix=inputs={audioWavs.Count}:duration=longest:normalize=0[a]\" " +
               $"-map 0:v -map \"[a]\" -c:v copy {acodec} -shortest{tail} {Q(output)}";
    }

    public static string GifFilter(int fps, int maxWidth) =>
        $"fps={fps},scale='min({maxWidth},iw)':-1:flags=lanczos,split[s0][s1];[s0]palettegen=stats_mode=diff[p];[s1][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle";

    /// <summary>Convierte un video (o un tramo) en GIF optimizado con paleta propia.</summary>
    public static string GifArgs(string input, string output, int fps, int maxWidth, double? start = null, double? duration = null)
    {
        string ss = start is { } s ? $"-ss {F(s)} " : "";
        string t = duration is { } d ? $"-t {F(d)} " : "";
        return $"-hide_banner -loglevel error -y {ss}{t}-i {Q(input)} -filter_complex \"[0:v]{GifFilter(fps, maxWidth)}\" -loop 0 {Q(output)}";
    }

    public static string ExtractFrameArgs(string input, double time, string outputPng) =>
        $"-hide_banner -loglevel error -y -ss {F(time)} -i {Q(input)} -frames:v 1 {Q(outputPng)}";

    public static string ConvertImageArgs(string input, string output, int quality) =>
        output.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            ? $"-hide_banner -loglevel error -y -i {Q(input)} -frames:v 1 -c:v libwebp -quality {quality} {Q(output)}"
            : $"-hide_banner -loglevel error -y -i {Q(input)} -frames:v 1 {Q(output)}";

    static string Atempo(double speed)
    {
        // atempo admite 0.5–2.0 por instancia: se encadenan las necesarias.
        var parts = new List<string>();
        double s = speed;
        while (s > 2.0 + 1e-9) { parts.Add("atempo=2.0"); s /= 2.0; }
        while (s < 0.5 - 1e-9) { parts.Add("atempo=0.5"); s /= 0.5; }
        parts.Add("atempo=" + F(s));
        return string.Join(",", parts);
    }

    /// <summary>Exportación del editor: conserva los segmentos indicados, aplica velocidad, superposiciones y audio.</summary>
    public static string EditExportArgs(EditExportOptions o, bool withProgress = true)
    {
        if (o.Segments.Count == 0) throw new InvalidOperationException("No queda ningún segmento que exportar.");
        bool gif = o.Output.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        bool webm = o.Output.EndsWith(".webm", StringComparison.OrdinalIgnoreCase);
        bool audio = o.InputHasAudio && !o.RemoveAudio && !gif;
        var fc = new List<string>();
        int n = o.Segments.Count;
        for (int i = 0; i < n; i++)
        {
            var (s, e) = o.Segments[i];
            fc.Add($"[0:v]trim=start={F(s)}:end={F(e)},setpts=PTS-STARTPTS[v{i}]");
            if (audio) fc.Add($"[0:a]atrim=start={F(s)}:end={F(e)},asetpts=PTS-STARTPTS[a{i}]");
        }
        string v = "v0", a = "a0";
        if (n > 1)
        {
            var ins = string.Concat(Enumerable.Range(0, n).Select(i => audio ? $"[v{i}][a{i}]" : $"[v{i}]"));
            fc.Add($"{ins}concat=n={n}:v=1:a={(audio ? 1 : 0)}[vc]{(audio ? "[ac]" : "")}");
            v = "vc"; a = "ac";
        }
        for (int i = 0; i < o.Overlays.Count; i++)
        {
            var ov = o.Overlays[i];
            fc.Add($"[{v}][{i + 1}:v]overlay=0:0:enable='between(t,{F(ov.Start)},{F(ov.End)})':shortest=1[vo{i}]");
            v = "vo" + i;
        }
        if (Math.Abs(o.Speed - 1.0) > 0.001)
        {
            fc.Add($"[{v}]setpts=PTS/{F(o.Speed)}[vs]"); v = "vs";
            if (audio) { fc.Add($"[{a}]{Atempo(o.Speed)}[as]"); a = "as"; }
        }
        int outH = o.Height, outW = o.Width;
        if (o.ScaleHeight is { } sh && sh < o.Height && o.Height > 0)
        {
            outH = sh & ~1; outW = (int)Math.Round(o.Width * (double)outH / o.Height) & ~1;
            fc.Add($"[{v}]scale={outW}:{outH}[vz]"); v = "vz";
        }
        else if (!gif && ((o.Width & 1) == 1 || (o.Height & 1) == 1))
        {
            fc.Add($"[{v}]scale=trunc(iw/2)*2:trunc(ih/2)*2[vz]"); v = "vz";
        }
        var inputs = $"-i {Q(o.Input)}" + string.Concat(o.Overlays.Select(ov => $" -loop 1 -i {Q(ov.PngPath)}"));
        string progress = withProgress ? "-progress pipe:1 -nostats " : "";
        string head = $"-hide_banner -loglevel error -y {progress}{inputs} -filter_complex ";
        if (gif)
        {
            // el filtro GIF se encadena sobre la etiqueta de video final
            fc.Add($"[{v}]{GifFilter((int)Math.Round(o.Fps), o.GifMaxWidth)}");
            return head + $"\"{string.Join(";", fc)}\" -loop 0 {Q(o.Output)}";
        }
        string enc = EncoderArgs(o.Encoder, Math.Max(outW, 16), Math.Max(outH, 16), o.Fps, o.Quality);
        string aArgs = audio ? $" -map \"[{a}]\" {(webm ? "-c:a libopus -b:a 128k" : "-c:a aac -b:a 160k")}" : " -an";
        return head + $"\"{string.Join(";", fc)}\" -map \"[{v}]\"{aArgs} {enc}{(webm ? "" : " -movflags +faststart")} {Q(o.Output)}";
    }

    static readonly Regex DurationRx = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    static readonly Regex VideoRx = new(@"Stream #\d+:\d+.*?: Video: .*?(\d{2,5})x(\d{2,5})", RegexOptions.Compiled);
    static readonly Regex FpsRx = new(@"(\d+(?:\.\d+)?) fps", RegexOptions.Compiled);
    static readonly Regex AudioRx = new(@"Stream #\d+:\d+.*?: Audio:", RegexOptions.Compiled);

    /// <summary>Interpreta la salida de «ffmpeg -i archivo».</summary>
    public static MediaInfo ParseMediaInfo(string stderr)
    {
        var info = new MediaInfo();
        var d = DurationRx.Match(stderr);
        if (d.Success)
            info.DurationSeconds = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
        foreach (var line in stderr.Split('\n'))
        {
            var vm = VideoRx.Match(line);
            if (vm.Success && !info.HasVideo)
            {
                info.HasVideo = true;
                info.Width = int.Parse(vm.Groups[1].Value); info.Height = int.Parse(vm.Groups[2].Value);
                var f = FpsRx.Match(line);
                if (f.Success) info.Fps = double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            if (AudioRx.IsMatch(line)) info.HasAudio = true;
        }
        return info;
    }

    static readonly Regex DshowRx = new(@"\]\s+""(?<name>[^""]+)""\s+\((?<kind>video|audio|none)\)", RegexOptions.Compiled);

    public const string ListDshowArgs = "-hide_banner -list_devices true -f dshow -i dummy";

    /// <summary>Nombres de cámaras a partir de la salida de «-list_devices true -f dshow».</summary>
    public static List<string> ParseDshowVideoDevices(string stderr)
    {
        var list = new List<string>();
        foreach (Match m in DshowRx.Matches(stderr))
            if (m.Groups["kind"].Value == "video" && !list.Contains(m.Groups["name"].Value)) list.Add(m.Groups["name"].Value);
        return list;
    }

    public static string WebcamArgs(string device, int size) =>
        $"-hide_banner -loglevel error -f dshow -i video={Q(device)} -vf \"scale={size}:{size}:force_original_aspect_ratio=increase,crop={size}:{size},hflip\" -r 20 -pix_fmt bgra -f rawvideo pipe:1";

    /// <summary>Lee «out_time_us=…» o «out_time_ms=…» de -progress y devuelve los segundos procesados.</summary>
    public static double? ParseProgressSeconds(string line)
    {
        int eq = line.IndexOf('=');
        if (eq < 0) return null;
        var key = line[..eq].Trim();
        if (key is "out_time_us" or "out_time_ms" && long.TryParse(line[(eq + 1)..].Trim(), out var us)) return us / 1_000_000.0;
        return null;
    }
}

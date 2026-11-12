using System.IO.Compression;
using FranCapture.Core.Automation;
using FranCapture.Core.Hotkeys;
using FranCapture.Core.Imaging;
using FranCapture.Core.Library;
using FranCapture.Core.Localization;
using FranCapture.Core.Qa;
using FranCapture.Core.Settings;
using FranCapture.Core.Storage;
using FranCapture.Core.Text;
using FranCapture.Core.Video;
using Xunit;

namespace FranCapture.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("F9", HotkeyModifiers.None, 0x78)]
    [InlineData("Ctrl+Shift+F10", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x79)]
    [InlineData("alt+a", HotkeyModifiers.Alt, 'A')]
    [InlineData("ImprPant", HotkeyModifiers.None, 0x2C)]
    [InlineData("PrintScreen", HotkeyModifiers.None, 0x2C)]
    [InlineData("Ctrl++", HotkeyModifiers.Ctrl, 0xBB)]
    public void Parse_roundtrip(string text, HotkeyModifiers mods, int vk)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(mods, g.Modifiers);
        Assert.Equal(vk, g.VirtualKey);
        Assert.True(HotkeyGesture.TryParse(g.ToString(), out var g2));
        Assert.Equal(g, g2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Nada")]
    [InlineData("A+B")]
    public void Parse_rejects_invalid(string text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Theory]
    [InlineData("F9", HotkeyProblem.None)]
    [InlineData("F8", HotkeyProblem.None)]
    [InlineData("F10", HotkeyProblem.None)]
    [InlineData("F11", HotkeyProblem.None)]
    [InlineData("ImprPant", HotkeyProblem.None)]
    [InlineData("F12", HotkeyProblem.Reserved)]
    [InlineData("A", HotkeyProblem.NeedsModifier)]
    [InlineData("Shift+A", HotkeyProblem.NeedsModifier)]
    [InlineData("Ctrl+C", HotkeyProblem.Reserved)]
    [InlineData("Alt+F4", HotkeyProblem.Reserved)]
    [InlineData("Win+F9", HotkeyProblem.WinNotAllowed)]
    [InlineData("Ctrl+Alt+K", HotkeyProblem.None)]
    public void Validate(string text, HotkeyProblem expected)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g));
        Assert.Equal(expected, g.Validate());
    }

    [Fact]
    public void Default_capture_hotkey_is_F9_and_defaults_have_no_conflicts()
    {
        var map = HotkeyDefaults.Create();
        Assert.Equal("F9", map[nameof(HotkeyAction.CaptureRegion)]);
        var used = map.Values.Where(v => v != "").ToList();
        Assert.Equal(used.Count, used.Distinct().Count());
        foreach (var a in Enum.GetNames<HotkeyAction>()) Assert.True(map.ContainsKey(a));
    }

    [Fact]
    public void Conflict_detected_and_alternatives_offered()
    {
        var map = HotkeyDefaults.Create();
        HotkeyGesture.TryParse("F9", out var f9);
        Assert.Equal(HotkeyAction.CaptureRegion, HotkeyMap.FindConflict(map, HotkeyAction.OcrRegion, f9));
        Assert.Null(HotkeyMap.FindConflict(map, HotkeyAction.CaptureRegion, f9));
        var alts = HotkeyMap.Suggest(map, f9);
        Assert.NotEmpty(alts);
        Assert.All(alts, a => { Assert.NotEqual(f9, a); Assert.Equal(HotkeyProblem.None, a.Validate()); Assert.DoesNotContain(a.ToString(), map.Values); });
        var none = HotkeyMap.Suggest(map, f9, _ => false);
        Assert.Empty(none);
    }
}

public class SettingsTests
{
    [Fact]
    public void Roundtrip_and_persistence()
    {
        var dir = Directory.CreateTempSubdirectory("fcs").FullName;
        var store = new SettingsStore(Path.Combine(dir, "sub", "settings.json"));
        var s = store.Load();
        Assert.Equal("es", s.Language);
        s.Language = "en"; s.Theme = "light"; s.RecordFps = 60; s.Hotkeys[nameof(HotkeyAction.CaptureRegion)] = "F8";
        s.LastRegion = new RegionRect { X = 5, Y = 6, Width = 70, Height = 80 };
        store.Save(s);
        var r = new SettingsStore(store.Path).Load();
        Assert.Equal("en", r.Language); Assert.Equal("light", r.Theme); Assert.Equal(60, r.RecordFps);
        Assert.Equal("F8", r.Hotkeys[nameof(HotkeyAction.CaptureRegion)]);
        Assert.Equal(70, r.LastRegion!.Width);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        var dir = Directory.CreateTempSubdirectory("fcs").FullName;
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ esto no es json");
        var s = new SettingsStore(path).Load();
        Assert.Equal("dark", s.Theme);
        Assert.True(File.Exists(path + ".dañado"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Normalize_fixes_out_of_range_values()
    {
        var s = new AppSettings { Theme = "rosa", Language = "fr", JpegQuality = 900, RecordFps = 144, Countdown = 7, ImageFormat = "JPEG", GifFps = 0 };
        s.Hotkeys["CaptureRegion"] = "A";                 // inseguro
        s.Hotkeys["CaptureFullScreen"] = "Ctrl+Shift+F9";
        s.Hotkeys["OcrRegion"] = "Ctrl+Shift+F9";         // duplicado
        s.Hotkeys["Inventada"] = "F7";
        SettingsValidator.Normalize(s);
        Assert.Equal("dark", s.Theme); Assert.Equal("es", s.Language); Assert.Equal(100, s.JpegQuality);
        Assert.Equal(60, s.RecordFps); Assert.Equal(3, s.Countdown); Assert.Equal("jpg", s.ImageFormat); Assert.Equal(2, s.GifFps);
        Assert.Equal("", s.Hotkeys["CaptureRegion"]);
        Assert.Equal("Ctrl+Shift+F9", s.Hotkeys["CaptureFullScreen"]);
        Assert.Equal("", s.Hotkeys["OcrRegion"]);
        Assert.False(s.Hotkeys.ContainsKey("Inventada"));
    }

    [Fact]
    public void Import_validation()
    {
        Assert.NotNull(SettingsValidator.ValidateImport("", out _));
        Assert.NotNull(SettingsValidator.ValidateImport("[1,2]", out _));
        Assert.NotNull(SettingsValidator.ValidateImport("{\"foo\":1}", out _));
        Assert.NotNull(SettingsValidator.ValidateImport("{\"SchemaVersion\":99}", out _));
        Assert.NotNull(SettingsValidator.ValidateImport("{no json", out _));
        var good = SettingsSerializer.Serialize(new AppSettings { Theme = "light" });
        Assert.Null(SettingsValidator.ValidateImport(good, out var imported));
        Assert.Equal("light", imported!.Theme);
    }
}

public class AutomationTests
{
    [Fact]
    public void Plan_reflects_enabled_rules_only()
    {
        var rules = AutomationRule.Defaults();
        var p = AutomationPlan.For(AutomationTrigger.Capture, rules);
        Assert.True(p.AutoSave); Assert.True(p.CopyToClipboard); Assert.True(p.OpenEditor);
        Assert.False(p.ConvertToJpg); Assert.False(p.AddWatermark); Assert.False(p.OpenVideoEditor);
        AutomationPlan.SetEnabled(rules, AutomationTrigger.Capture, AutomationAction.ConvertToJpg, true);
        AutomationPlan.SetEnabled(rules, AutomationTrigger.Capture, AutomationAction.OpenEditor, false);
        p = AutomationPlan.For(AutomationTrigger.Capture, rules);
        Assert.True(p.ConvertToJpg); Assert.False(p.OpenEditor);
        Assert.True(AutomationPlan.For(AutomationTrigger.Recording, rules).OpenVideoEditor);
        Assert.False(AutomationPlan.For(AutomationTrigger.Ocr, rules).CopyText);
    }

    [Fact]
    public void Incompatible_rules_are_ignored()
    {
        var rules = new List<AutomationRule> { new() { Trigger = AutomationTrigger.Ocr, Action = AutomationAction.OpenEditor, Enabled = true } };
        Assert.False(AutomationPlan.For(AutomationTrigger.Ocr, rules).OpenEditor);
        var s = new AppSettings { Automations = rules };
        SettingsValidator.Normalize(s);
        Assert.Empty(s.Automations);
    }
}

public class NamingTests
{
    [Fact]
    public void Template_expansion()
    {
        var d = new DateTime(2026, 3, 9, 14, 5, 7);
        Assert.Equal("Captura_2026-03-09_14-05-07", FileNaming.Expand("Captura_{fecha}_{hora}", d));
        Assert.Equal("img_007", FileNaming.Expand("img_{n}", d, 7));
        Assert.Equal("img_2026-03-09", FileNaming.Expand("img", d, forceDate: true));
        Assert.Equal("a_b_c", FileNaming.Expand("a:b?c", d));
        Assert.Equal("_CON", FileNaming.Sanitize("CON"));
    }

    [Fact]
    public void Unique_path_avoids_overwrite()
    {
        var existing = new HashSet<string> { Path.Combine("d", "a.png"), Path.Combine("d", "a (2).png") };
        Assert.Equal(Path.Combine("d", "a (3).png"), FileNaming.UniquePath("d", "a", "png", existing.Contains));
        Assert.Equal(Path.Combine("d", "b.png"), FileNaming.UniquePath("d", "b", ".png", existing.Contains));
    }

    [Fact]
    public void Size_format() { Assert.Equal("512 B", FileNaming.FormatSize(512)); Assert.Equal("1.5 MB", FileNaming.FormatSize(1572864)); }
}

public class LibraryTests
{
    [Fact]
    public void Crud_filters_and_prune()
    {
        using var repo = new LibraryRepository(":memory:");
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a = repo.Upsert(new LibraryItem { Path = @"C:\caps\login error.png", Kind = MediaKind.Image, CreatedUtc = t0, Width = 800, Height = 600, SizeBytes = 1000 });
        var b = repo.Upsert(new LibraryItem { Path = @"C:\caps\demo.mp4", Kind = MediaKind.Video, CreatedUtc = t0.AddMinutes(1), SizeBytes = 5000 });
        var c = repo.Upsert(new LibraryItem { Path = @"C:\caps\anim.gif", Kind = MediaKind.Gif, CreatedUtc = t0.AddMinutes(2), SizeBytes = 300 });
        Assert.Equal(3, repo.Count());
        Assert.Equal(6300, repo.TotalSize());

        // Upsert por ruta no duplica y conserva favoritos/etiquetas
        repo.SetFavorite(a.Id, true); repo.SetTags(a.Id, "Bug; Login ,bug"); repo.SetCollection(a.Id, "Proyecto X");
        var a2 = repo.Upsert(new LibraryItem { Path = @"c:\CAPS\login error.png", Kind = MediaKind.Image, CreatedUtc = t0, Width = 801, Height = 600, SizeBytes = 1100 });
        Assert.Equal(a.Id, a2.Id); Assert.True(a2.Favorite); Assert.Equal("bug,login", a2.Tags); Assert.Equal(801, a2.Width);
        Assert.Equal(3, repo.Count());

        Assert.Equal(["anim.gif", "demo.mp4", "login error.png"], repo.Query(new LibraryQuery()).Select(i => i.Name).ToArray());
        Assert.Equal("login error.png", repo.Query(new LibraryQuery { NewestFirst = false })[0].Name);
        Assert.Single(repo.Query(new LibraryQuery { Kind = MediaKind.Video }));
        Assert.Single(repo.Query(new LibraryQuery { Kind = MediaKind.Gif }));
        Assert.Single(repo.Query(new LibraryQuery { FavoritesOnly = true }));
        Assert.Single(repo.Query(new LibraryQuery { Search = "LOGIN" }));
        Assert.Empty(repo.Query(new LibraryQuery { Search = "caps" })); // no coincide con carpetas
        Assert.Single(repo.Query(new LibraryQuery { Tag = "bug" }));
        Assert.Empty(repo.Query(new LibraryQuery { Tag = "bu" }));
        Assert.Single(repo.Query(new LibraryQuery { Collection = "Proyecto X" }));
        Assert.Equal(["Proyecto X"], repo.Collections());
        Assert.Equal(["bug", "login"], repo.AllTags());

        repo.UpdatePath(b.Id, @"C:\caps\demo2.mp4");
        Assert.NotNull(repo.GetByPath(@"C:\caps\demo2.mp4"));

        int removed = repo.PruneMissing(p => !p.EndsWith(".gif"));
        Assert.Equal(1, removed);
        Assert.Null(repo.GetById(c.Id));
        repo.Remove(a.Id);
        Assert.Equal(1, repo.Count());
    }

    [Fact]
    public void Database_persists_on_disk()
    {
        var dir = Directory.CreateTempSubdirectory("fcs").FullName;
        var db = Path.Combine(dir, "lib.db");
        using (var r = new LibraryRepository(db)) r.Upsert(new LibraryItem { Path = "x.png", CreatedUtc = DateTime.UtcNow });
        using (var r = new LibraryRepository(db)) Assert.Equal(1, r.Count());
    }

    [Theory]
    [InlineData("a.PNG", MediaKind.Image)]
    [InlineData("a.mp4", MediaKind.Video)]
    [InlineData("a.webm", MediaKind.Video)]
    [InlineData("a.gif", MediaKind.Gif)]
    public void Kind_from_path(string p, MediaKind k) => Assert.Equal(k, LibraryItem.KindFromPath(p));
}

public class ImagingTests
{
    static byte[] Solid(int w, int h, byte b, byte g, byte r)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = 255; }
        return px;
    }

    [Fact]
    public void Pixelate_destroys_detail_inside_blocks()
    {
        int w = 16, h = 16;
        var px = new byte[w * h * 4];
        var rnd = new Random(1);
        rnd.NextBytes(px);
        PixelOps.Pixelate(px, w, h, 8);
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++)
            for (int ch = 0; ch < 4; ch++) Assert.Equal(px[ch], px[(y * w + x) * 4 + ch]);
    }

    [Fact]
    public void Blur_keeps_solid_color_and_smooths_edges()
    {
        var solid = Solid(20, 20, 10, 20, 30);
        PixelOps.BoxBlur(solid, 20, 20, 3);
        Assert.Equal(10, solid[0]); Assert.Equal(30, solid[(10 * 20 + 10) * 4 + 2]);

        var edge = Solid(20, 20, 0, 0, 0);
        for (int y = 0; y < 20; y++) for (int x = 10; x < 20; x++) edge[(y * 20 + x) * 4] = 255;
        PixelOps.BoxBlur(edge, 20, 20, 2);
        int mid = edge[(10 * 20 + 10) * 4];
        Assert.InRange(mid, 40, 215);
    }

    [Fact]
    public void Background_removal_clears_uniform_border_but_keeps_subject()
    {
        int w = 30, h = 30;
        var px = Solid(w, h, 255, 255, 255);
        for (int y = 10; y < 20; y++) for (int x = 10; x < 20; x++) { int i = (y * w + x) * 4; px[i] = 0; px[i + 1] = 0; px[i + 2] = 200; }
        int removed = PixelOps.RemoveBackgroundByColor(px, w, h, 20);
        Assert.Equal(w * h - 100, removed);
        Assert.Equal(0, px[3]);
        Assert.Equal(255, px[(15 * w + 15) * 4 + 3]);
        Assert.Equal(200, px[(15 * w + 15) * 4 + 2]);
    }

    [Fact]
    public void Diff_counts_changed_pixels()
    {
        var a = Solid(10, 10, 0, 0, 0); var b = Solid(10, 10, 0, 0, 0);
        Assert.Equal(0, ImageDiff.Compare(a, 10, 10, b, 10, 10).DifferentPixels);
        for (int i = 0; i < 5; i++) b[i * 4 + 2] = 255;
        var d = ImageDiff.Compare(a, 10, 10, b, 10, 10);
        Assert.Equal(5, d.DifferentPixels); Assert.Equal(5.0, d.Percent, 3); Assert.True(d.SameSize);
        var c = Solid(12, 10, 0, 0, 0);
        var d2 = ImageDiff.Compare(a, 10, 10, c, 12, 10);
        Assert.False(d2.SameSize); Assert.Equal(20, d2.DifferentPixels);
    }

    // Documento sintético: cada fila tiene un patrón único determinado por su índice.
    static byte[] Frame(int w, int h, int scrollTop, int header = 0, int footer = 0, int docHeight = 100000)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int docRow = y < header ? -1000 - y : y >= h - footer ? -5000 - y : Math.Min(scrollTop + y, docHeight - 1 - (h - 1 - y) + 0);
            if (y >= header && y < h - footer) docRow = scrollTop + y;
            var rnd = new Random(docRow * 7919 + 13);
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)rnd.Next(256); px[i + 1] = (byte)rnd.Next(256); px[i + 2] = (byte)rnd.Next(256); px[i + 3] = 255;
            }
        }
        return px;
    }

    [Fact]
    public void Stitch_assembles_scrolled_frames_without_duplicates()
    {
        int w = 64, h = 100;
        var st = new ScrollStitcher(w, h);
        int[] tops = [0, 37, 80, 80, 131];
        var statuses = tops.Select(t => st.AddFrame(Frame(w, h, t))).ToArray();
        Assert.Equal([StitchStatus.Appended, StitchStatus.Appended, StitchStatus.Appended, StitchStatus.NoMovement, StitchStatus.Appended], statuses);
        var (pixels, rw, rh) = st.Build();
        Assert.Equal(w, rw); Assert.Equal(231, rh);
        var expected = Frame(w, 231, 0);
        Assert.Equal(expected, pixels);
    }

    [Fact]
    public void Stitch_handles_sticky_header_and_footer()
    {
        int w = 64, h = 120, header = 20, footer = 10;
        var st = new ScrollStitcher(w, h);
        Assert.Equal(StitchStatus.Appended, st.AddFrame(Frame(w, h, 0, header, footer)));
        Assert.Equal(StitchStatus.Appended, st.AddFrame(Frame(w, h, 50, header, footer)));
        Assert.Equal(StitchStatus.Appended, st.AddFrame(Frame(w, h, 95, header, footer)));
        var (pixels, _, rh) = st.Build();
        Assert.Equal(h + 95, rh);
        // cabecera una sola vez arriba, pie una sola vez abajo, cuerpo continuo
        var first = Frame(w, h, 0, header, footer);
        Assert.Equal(first.AsSpan(0, header * w * 4).ToArray(), pixels.AsSpan(0, header * w * 4).ToArray());
        Assert.Equal(first.AsSpan((h - footer) * w * 4).ToArray(), pixels.AsSpan((rh - footer) * w * 4).ToArray());
        var body = Frame(w, 300, 0);
        Assert.Equal(body.AsSpan(header * w * 4, (rh - header - footer) * w * 4).ToArray(), pixels.AsSpan(header * w * 4, (rh - header - footer) * w * 4).ToArray());
    }

    [Fact]
    public void Stitch_refuses_unrelated_content_instead_of_corrupting()
    {
        int w = 64, h = 100;
        var st = new ScrollStitcher(w, h);
        st.AddFrame(Frame(w, h, 0));
        Assert.Equal(StitchStatus.NoMatch, st.AddFrame(Frame(w, h, 50000)));
        Assert.Equal(h, st.Build().Height);
        Assert.Equal(1, st.FramesAccepted);
    }
}

public class VideoTests
{
    [Fact]
    public void Record_args_pipe_raw_frames_into_recoverable_container()
    {
        var a = Ffmpeg.RecordArgs(1920, 1080, 30, VideoEncoder.OpenH264, "high", null, @"C:\t\v.mkv");
        Assert.Contains("-f rawvideo -pix_fmt bgra -video_size 1920x1080 -framerate 30 -i pipe:0", a);
        Assert.Contains("-c:v libopenh264", a); Assert.Contains("-f matroska", a); Assert.Contains("\"C:\\t\\v.mkv\"", a);
        Assert.DoesNotContain("scale=", a);
        var s = Ffmpeg.RecordArgs(2560, 1440, 60, VideoEncoder.Nvenc, "low", 720, "o.mkv");
        Assert.Contains("-vf scale=1280:720", s); Assert.Contains("h264_nvenc", s);
    }

    [Fact]
    public void Mux_args_cover_zero_one_and_two_audio_tracks()
    {
        Assert.Contains("-c:v copy -movflags +faststart", Ffmpeg.MuxArgs("v.mkv", [], "o.mp4"));
        var one = Ffmpeg.MuxArgs("v.mkv", ["m.wav"], "o.mp4");
        Assert.Contains("-map 1:a", one); Assert.Contains("-c:a aac", one);
        var two = Ffmpeg.MuxArgs("v.mkv", ["m.wav", "s.wav"], "o.webm");
        Assert.Contains("amix=inputs=2", two); Assert.Contains("libopus", two); Assert.DoesNotContain("faststart", two);
    }

    [Fact]
    public void Hardware_is_preferred_only_when_allowed()
    {
        Assert.True(VideoEncoder.H264Candidates(true)[0].Hardware);
        Assert.DoesNotContain(VideoEncoder.H264Candidates(false), e => e.Hardware);
    }

    [Fact]
    public void Timeline_split_trim_and_ranges()
    {
        var t = new EditTimeline(10);
        Assert.True(t.Split(4)); Assert.True(t.Split(6));
        Assert.False(t.Split(6.01));
        t.SetRemoved(1, true);
        Assert.Equal([(0d, 4d), (6d, 10d)], t.KeptRanges());
        Assert.Equal(8, t.KeptDuration, 6);
        t.TrimStart(1); t.TrimEnd(9);
        Assert.Equal([(1d, 4d), (6d, 9d)], t.KeptRanges());
        Assert.Equal(0, t.SourceToOutput(0.5), 6);
        Assert.Equal(2, t.SourceToOutput(3), 6);
        Assert.Equal(3, t.SourceToOutput(5), 6);
        Assert.Equal(4, t.SourceToOutput(7), 6);
        Assert.Null(t.MapInterval(4.2, 5.8));
        Assert.Equal((2d, 4d), t.MapInterval(3, 7));
        t.SetRemoved(1, false); // restaurar segmento
        Assert.Contains((1d, 4d), t.KeptRanges());
    }

    [Fact]
    public void Edit_export_builds_filter_graph()
    {
        var o = new EditExportOptions
        {
            Input = "in.mp4", Output = "out.mp4", Segments = [(1, 4), (6, 9)], Speed = 2, InputHasAudio = true,
            Overlays = [new OverlaySpec("ov.png", 0.5, 2)], Width = 1280, Height = 720, Fps = 30,
        };
        var a = Ffmpeg.EditExportArgs(o);
        Assert.Contains("[0:v]trim=start=1:end=4,setpts=PTS-STARTPTS[v0]", a);
        Assert.Contains("[0:a]atrim=start=6:end=9,asetpts=PTS-STARTPTS[a1]", a);
        Assert.Contains("concat=n=2:v=1:a=1[vc][ac]", a);
        Assert.Contains("overlay=0:0:enable='between(t,0.5,2)'", a);
        Assert.Contains("setpts=PTS/2", a); Assert.Contains("atempo=2", a);
        Assert.Contains("-loop 1 -i \"ov.png\"", a); Assert.Contains("-progress pipe:1", a);

        var mute = Ffmpeg.EditExportArgs(new EditExportOptions { Input = "in.mp4", Output = "o.mp4", Segments = [(0, 5)], InputHasAudio = true, RemoveAudio = true, Width = 100, Height = 100 });
        Assert.Contains(" -an", mute); Assert.DoesNotContain("atrim", mute);

        var gif = Ffmpeg.EditExportArgs(new EditExportOptions { Input = "in.mp4", Output = "o.gif", Segments = [(0, 5)], InputHasAudio = true, Width = 100, Height = 100, Fps = 12, GifMaxWidth = 480 });
        Assert.Contains("palettegen", gif); Assert.Contains("fps=12", gif); Assert.Contains("min(480,iw)", gif); Assert.DoesNotContain("atrim", gif);

        var slow = Ffmpeg.EditExportArgs(new EditExportOptions { Input = "i", Output = "o.mp4", Segments = [(0, 5)], InputHasAudio = true, Speed = 0.25, Width = 100, Height = 100 });
        Assert.Contains("atempo=0.5,atempo=0.5", slow);
        Assert.Throws<InvalidOperationException>(() => Ffmpeg.EditExportArgs(new EditExportOptions { Input = "i", Output = "o.mp4", Segments = [] }));
    }

    [Fact]
    public void Media_info_and_progress_parsing()
    {
        const string err = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'x.mp4':
              Duration: 00:01:05.50, start: 0.000000, bitrate: 1200 kb/s
              Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(progressive), 1920x1080, 1100 kb/s, 30 fps, 30 tbr, 15360 tbn (default)
              Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 160 kb/s (default)
            """;
        var i = Ffmpeg.ParseMediaInfo(err);
        Assert.Equal(65.5, i.DurationSeconds, 3); Assert.Equal(1920, i.Width); Assert.Equal(1080, i.Height); Assert.Equal(30, i.Fps); Assert.True(i.HasAudio); Assert.True(i.HasVideo);
        Assert.False(Ffmpeg.ParseMediaInfo("Stream #0:0: Video: h264, yuv420p, 640x480, 25 fps").HasAudio);
        Assert.Equal(2.5, Ffmpeg.ParseProgressSeconds("out_time_us=2500000"));
        Assert.Null(Ffmpeg.ParseProgressSeconds("frame=10"));
    }

    [Fact]
    public void Bitrate_scales_with_quality()
    {
        Assert.True(Ffmpeg.BitrateKbps(1920, 1080, 30, "low") < Ffmpeg.BitrateKbps(1920, 1080, 30, "medium"));
        Assert.True(Ffmpeg.BitrateKbps(1920, 1080, 30, "medium") < Ffmpeg.BitrateKbps(1920, 1080, 30, "high"));
    }
}

public class TextTests
{
    [Fact]
    public void Links_detected_and_normalized()
    {
        var links = LinkDetector.Find("Visita https://example.com/a?b=1, o www.github.com. Escribe a ana@correo.es y mira docs.example.org/guia");
        Assert.Equal(["https://example.com/a?b=1", "https://www.github.com", "https://docs.example.org/guia"], links);
        Assert.Empty(LinkDetector.Find("sin enlaces aquí"));
        Assert.Equal("https://www.bing.com/search?q=hola%20mundo", LinkDetector.SearchUrl("bing", " hola mundo "));
    }

    [Theory]
    [InlineData("ana.perez@correo.es", SensitiveKind.Email)]
    [InlineData("4111 1111 1111 1111", SensitiveKind.CardNumber)]
    [InlineData("192.168.1.20", SensitiveKind.IpAddress)]
    [InlineData("+34 612 345 678", SensitiveKind.Phone)]
    [InlineData("ghp_abcdefghijklmnop1234", SensitiveKind.Secret)]
    public void Sensitive_classification(string token, SensitiveKind kind) => Assert.Equal(kind, SensitiveData.Classify(token));

    [Theory]
    [InlineData("hola")]
    [InlineData("1234 5678 9012 3456")] // no pasa Luhn
    [InlineData("999.999.1.1")]
    [InlineData("Captura_2026")]
    public void Non_sensitive(string token) => Assert.Null(SensitiveData.Classify(token));

    [Fact]
    public void Sensitive_word_groups()
    {
        var hits = SensitiveData.FindInWords(["Tarjeta:", "4111", "1111", "1111", "1111", "correo", "a.b@c.com"]);
        Assert.Equal([(1, 4, SensitiveKind.CardNumber), (6, 6, SensitiveKind.Email)], hits);
    }

    [Fact]
    public void Scrub_removes_private_data()
    {
        var s = SensitiveData.ScrubText(@"Error en C:\Users\Francisco\Pictures\a.png de fran@x.com desde 10.0.0.5", "Francisco");
        Assert.DoesNotContain("Francisco", s); Assert.DoesNotContain("fran@x.com", s); Assert.DoesNotContain("10.0.0.5", s);
        Assert.Contains(@"C:\Users\<usuario>\Pictures", s);
    }

    [Fact]
    public void Docx_is_valid_package()
    {
        using var ms = new MemoryStream();
        DocxWriter.Write(ms, "Título", "Línea 1 <&>\nLínea 2");
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        using var r = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
        var xml = r.ReadToEnd();
        Assert.Contains("Línea 1 &lt;&amp;&gt;", xml);
        System.Xml.Linq.XDocument.Parse(xml);
    }
}

public class QaTests
{
    [Fact]
    public void Markdown_is_structured()
    {
        var r = new BugReport
        {
            Title = "El botón Guardar no responde", Description = "No pasa nada al pulsar.", Expected = "Debe guardar el archivo.",
            Steps = BugReport.ParseSteps("1. Abrir la app\n- Pulsar Guardar\n\n3) Observar"), Attachments = [@"C:\x\cap1.png"],
            IncludeTechnicalInfo = true, TechnicalInfo = "Windows 11", Created = new DateTime(2026, 5, 1, 10, 0, 0),
        };
        var md = r.ToMarkdown();
        Assert.Contains("## El botón Guardar no responde", md);
        Assert.Contains("### Qué ocurrió", md); Assert.Contains("### Qué se esperaba", md); Assert.Contains("### Pasos para reproducir", md);
        Assert.Contains("1. Abrir la app", md); Assert.Contains("2. Pulsar Guardar", md); Assert.Contains("3. Observar", md);
        Assert.Contains("- cap1.png", md); Assert.Contains("Windows 11", md);
        r.IncludeTechnicalInfo = false;
        Assert.DoesNotContain("Windows 11", r.ToMarkdown());
    }

    [Fact]
    public void Html_escapes_and_embeds_images()
    {
        var r = new BugReport { Title = "<script>x</script>", Description = "a & b", Attachments = ["c.png"] };
        var html = r.ToHtml(_ => [1, 2, 3]);
        Assert.DoesNotContain("<script>x", html); Assert.Contains("a &amp; b", html);
        Assert.Contains("data:image/png;base64,AQID", html);
    }
}

public class LocTests
{
    [Fact]
    public void Falls_back_to_spanish_and_notifies()
    {
        var loc = new Loc();
        Assert.Equal("Cadena sin traducir", loc["Cadena sin traducir"]);
        bool notified = false;
        loc.PropertyChanged += (_, e) => notified |= e.PropertyName == "Item[]";
        loc.Language = "en";
        Assert.True(notified);
        Assert.Equal("Cadena sin traducir", loc["Cadena sin traducir"]);
        foreach (var kv in English.Map) Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key);
    }
}

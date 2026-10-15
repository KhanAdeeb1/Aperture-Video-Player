using Aperture.Core.Playback;

namespace Aperture.Playback.Mpv.Tests;

/// <summary>Milestone 9 against real libmpv: seek previews, A–B loops, sound options and found subtitles.</summary>
public class AdvancedFeatureTests
{
    private static readonly Action<string, string> Log = static (level, text) =>
    {
        if (level is "error" or "fatal" or "warn")
        {
            Console.WriteLine($"[{level}] {text}");
        }
    };

    [MediaFact]
    public async Task Seek_previews_show_the_picture_at_the_time_asked()
    {
        using var previews = await MpvSeekPreviews.OpenAsync(TestMedia.PathOf("colours.mp4"), 160, 90, Log);
        Assert.NotNull(previews);
        Assert.Equal((160, 90), previews.Size);

        var red = await previews.GetAsync(TimeSpan.FromSeconds(4));
        var blue = await previews.GetAsync(TimeSpan.FromSeconds(15));
        var redAgain = await previews.GetAsync(TimeSpan.FromSeconds(6));
        Assert.NotNull(red);
        Assert.NotNull(blue);
        Assert.NotNull(redAgain);
        var (r1, _, b1) = Average(red);
        var (r2, _, b2) = Average(blue);
        var (r3, _, b3) = Average(redAgain);
        Console.WriteLine($"4 s: R{r1:0} B{b1:0}; 15 s: R{r2:0} B{b2:0}; 6 s: R{r3:0} B{b3:0}");
        Assert.True(r1 > 150 && b1 < 80, "4 s should be red");
        Assert.True(b2 > 150 && r2 < 80, "15 s should be blue");
        Assert.True(r3 > 150 && b3 < 80, "6 s should be red again");
        Assert.All(Enumerable.Range(0, red.Pixels.Length / 4), i => Assert.Equal(255, red.Pixels[(i * 4) + 3]));

        // A picture already made comes back without decoding again.
        var cached = await previews.GetAsync(TimeSpan.FromSeconds(15));
        Assert.Same(blue, cached);
        TestMedia.Record("colours.mp4 (seek previews)", "Previews", "red at 4 s and 6 s, blue at 15 s, 160×90, cached on repeat");
    }

    [MediaFact]
    public async Task Seek_previews_keep_the_picture_shape()
    {
        // Stored 720×576, shown 16:9.
        using var previews = await MpvSeekPreviews.OpenAsync(TestMedia.PathOf("mpeg2-anamorphic.ts"), 160, 160, Log);
        Assert.NotNull(previews);
        var (width, height) = previews.Size;
        Assert.InRange((double)width / height, 1.7, 1.85);
        Assert.NotNull(await previews.GetAsync(TimeSpan.FromSeconds(3)));
    }

    [MediaFact]
    public async Task Seek_previews_are_not_offered_without_a_picture()
    {
        Assert.Null(await MpvSeekPreviews.OpenAsync(TestMedia.PathOf("audio-only.mp4"), 160, 90, Log));
        Assert.Null(await MpvSeekPreviews.OpenAsync(TestMedia.PathOf("damaged.mp4"), 160, 90, Log));
    }

    [MediaFact]
    public async Task A_newer_preview_request_replaces_a_waiting_one()
    {
        using var previews = await MpvSeekPreviews.OpenAsync(TestMedia.PathOf("colours.mp4"), 96, 54, Log);
        Assert.NotNull(previews);
        var requests = Enumerable.Range(0, 20).Select(i => previews.GetAsync(TimeSpan.FromSeconds(i % 19))).ToList();
        var results = await Task.WhenAll(requests);
        Assert.NotNull(results[^1]);
        Assert.Contains(results, r => r is null);
    }

    [MediaFact]
    public async Task A_B_loop_repeats_the_section()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("h264-aac-timedtext.mp4"), TimeSpan.Zero);
        engine.SetLoop(new LoopPoints(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3.5)));
        Assert.True(await TestMedia.WaitAsync(() => engine.ReadProperty("ab-loop-b") is "3.500000"));
        var seen = new List<double>();
        var end = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < end)
        {
            seen.Add(engine.Position.TotalSeconds);
            await Task.Delay(100);
        }

        Assert.Equal(PlaybackStatus.Playing, engine.Status);
        Assert.All(seen.Skip(3), p => Assert.InRange(p, 1.9, 3.7));
        Assert.Contains(seen, p => p > 3.0);
        engine.SetLoop(LoopPoints.None);
        Assert.True(await TestMedia.WaitAsync(() => engine.ReadProperty("ab-loop-a") == "no"));
        Assert.True(await TestMedia.WaitAsync(() => engine.Position > TimeSpan.FromSeconds(4)));
        TestMedia.Record("h264-aac-timedtext.mp4 (A–B loop)", "Loops", "stayed between 2 s and 3.5 s for 4 s, then played on when cleared");
    }

    [MediaFact]
    public async Task Clearer_dialogue_and_even_volume_play_through_FFmpeg_filters()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("hevc10-ac3-51.mkv"), TimeSpan.Zero);
        Assert.Null(engine.SetSound(new SoundOptions(ClearDialogue: true, EvenVolume: true)));
        Assert.Contains("dialoguenhance", engine.ReadProperty("af") ?? string.Empty, StringComparison.Ordinal);
        var start = engine.Position;
        Assert.True(await TestMedia.WaitAsync(() => engine.Position > start + TimeSpan.FromSeconds(1.5)), "playback should carry on with the filters");
        Assert.Equal(PlaybackStatus.Playing, engine.Status);
        Assert.True(await TestMedia.WaitAsync(() => engine.ReadProperty("audio-out-params/channel-count") == "2"), $"mixed to stereo, got {engine.ReadProperty("audio-out-params/channel-count")}");

        Assert.Null(engine.SetSound(SoundOptions.Off));
        Assert.Equal(string.Empty, engine.ReadProperty("af"));
        TestMedia.Record("hevc10-ac3-51.mkv (sound options)", "Processes", "dialoguenhance and dynaudnorm applied to 5.1 AC-3 (mixed to stereo), then removed");
    }

    [MediaFact]
    public async Task Finds_subtitles_in_the_release_Subs_folder()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf(Path.Combine("release", "Lighthouse.2019.1080p.WEB.mp4")), TimeSpan.Zero);
        Assert.True(await TestMedia.WaitAsync(() => engine.Tracks.Count(t => t.Kind == TrackKind.Subtitle) == 2));
        var english = engine.Tracks.Single(t => t.Kind == TrackKind.Subtitle && t.Language == "eng");
        var french = engine.Tracks.Single(t => t.Kind == TrackKind.Subtitle && t.Language == "fre");
        Assert.True(english.IsExternal);
        Assert.False(english.IsForced);
        Assert.True(french.IsForced);
        Assert.Equal("English", TrackLabels.Name(english));
        TestMedia.Record("release/Subs/<video>/ (subtitles)", "Found", "2_English.srt and 3_French.forced loaded and labelled English, French (forced)");
    }

    private static (double R, double G, double B) Average(PreviewFrame frame)
    {
        double r = 0, g = 0, b = 0;
        var count = frame.Pixels.Length / 4;
        for (var i = 0; i < frame.Pixels.Length; i += 4)
        {
            b += frame.Pixels[i];
            g += frame.Pixels[i + 1];
            r += frame.Pixels[i + 2];
        }

        return (r / count, g / count, b / count);
    }
}

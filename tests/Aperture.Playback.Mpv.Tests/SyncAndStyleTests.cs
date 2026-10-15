using System.Globalization;
using Aperture.Core.Playback;

namespace Aperture.Playback.Mpv.Tests;

/// <summary>Milestone 3 controls against real libmpv: timing offsets, pitch, subtitle style, frame steps.</summary>
[Collection("libmpv")]
public class SyncAndStyleTests
{
    private static double Number(MpvPlaybackEngine engine, string property) =>
        double.Parse(engine.ReadProperty(property) ?? "nan", CultureInfo.InvariantCulture);

    [MediaFact]
    public async Task Timing_offsets_reach_the_engine_and_reset_for_the_next_file()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("multi-track.mkv"), TimeSpan.Zero);

        engine.SubtitleDelay = TimeSpan.FromMilliseconds(300);
        engine.AudioDelay = TimeSpan.FromMilliseconds(-200);
        Assert.True(await TestMedia.WaitAsync(() => Math.Abs(Number(engine, "sub-delay") - 0.3) < 0.001 && Math.Abs(Number(engine, "audio-delay") + 0.2) < 0.001));
        Assert.Contains("Subtitle offset", engine.GetDiagnostics().ToText(), StringComparison.Ordinal);

        await engine.OpenAsync(TestMedia.PathOf("vp9-opus.webm"), TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, engine.SubtitleDelay);
        Assert.True(await TestMedia.WaitAsync(() => Number(engine, "sub-delay") == 0 && Number(engine, "audio-delay") == 0));
        TestMedia.Record("multi-track.mkv (sync)", "Adjusts", "subtitle +0.3 s and sound -0.2 s offsets applied, then reset for the next file");
    }

    [MediaFact]
    public async Task Pitch_correction_can_be_turned_off()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("h264-aac-timedtext.mp4"), TimeSpan.Zero);
        Assert.Equal("yes", engine.ReadProperty("audio-pitch-correction"));
        engine.PreservePitch = false;
        engine.Rate = 1.75;
        Assert.True(await TestMedia.WaitAsync(() => engine.ReadProperty("audio-pitch-correction") == "no" && Math.Abs(Number(engine, "speed") - 1.75) < 0.001));
    }

    [MediaFact]
    public async Task Subtitle_style_sets_size_colour_and_background()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("multi-track.mkv"), TimeSpan.Zero);
        engine.ApplySubtitleStyle(new SubtitleStyle(SubtitleSize.ExtraLarge, SubtitleColor.Yellow, SubtitleBackground.Box));
        Assert.True(await TestMedia.WaitAsync(() => Math.Abs(Number(engine, "sub-scale") - 1.5) < 0.001 && engine.ReadProperty("sub-color") == "#FFF5DC5A"));
        if (engine.SupportsSubtitleBox)
        {
            Assert.True(await TestMedia.WaitAsync(() => engine.ReadProperty("sub-border-style") is "background-box" or "opaque-box"));
        }

        TestMedia.Record(
            "multi-track.mkv (subtitle style)",
            "Styles",
            $"size, colour applied; dark box {(engine.SupportsSubtitleBox ? "applied" : "not available in this libmpv (" + engine.ReadProperty("mpv-version") + ")")}");
    }

    [MediaFact]
    public async Task Frame_steps_pause_and_move_one_frame()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("h264-aac-timedtext.mp4"), TimeSpan.FromSeconds(2));
        engine.Pause();
        Assert.True(await TestMedia.WaitAsync(() => engine.Status == PlaybackStatus.Paused));
        await Task.Delay(300);
        var before = Number(engine, "time-pos");
        engine.StepFrame(forward: true);
        Assert.True(await TestMedia.WaitAsync(() => Number(engine, "time-pos") > before + 0.001), $"before {before}, after {Number(engine, "time-pos")}");
        var stepped = Number(engine, "time-pos") - before;
        Assert.InRange(stepped, 0.001, 0.2);
        Assert.Equal(PlaybackStatus.Paused, engine.Status);
    }

    [MediaFact]
    public async Task A_file_can_open_paused_at_its_resume_point()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("h264-aac-timedtext.mp4"), TimeSpan.FromSeconds(3), startPaused: true);

        Assert.Equal(PlaybackStatus.Paused, engine.Status);
        Assert.InRange(engine.Position.TotalSeconds, 2.5, 3.5);
        engine.Play();
        Assert.True(await TestMedia.WaitAsync(() => engine.Status == PlaybackStatus.Playing));

        await engine.OpenAsync(TestMedia.PathOf("vp9-opus.webm"), TimeSpan.Zero);
        Assert.Equal(PlaybackStatus.Playing, engine.Status);
    }
}

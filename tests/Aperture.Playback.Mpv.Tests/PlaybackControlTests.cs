using System.Globalization;
using Aperture.Core.Playback;

namespace Aperture.Playback.Mpv.Tests;

/// <summary>Volume, speed, aspect, resume, tracks and external subtitles against real libmpv.</summary>
[Collection("libmpv")]
public class PlaybackControlTests
{
    private static double Number(MpvPlaybackEngine engine, string property) =>
        double.Parse(engine.ReadProperty(property) ?? "nan", CultureInfo.InvariantCulture);

    [MediaFact]
    public async Task Starts_at_a_saved_position()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("vp9-opus.webm"), TimeSpan.FromSeconds(5));
        Assert.Equal(PlaybackStatus.Playing, engine.Status);
        Assert.True(await TestMedia.WaitAsync(() => engine.Position.TotalSeconds >= 4.9, 5), $"started at {engine.Position.TotalSeconds:0.00}");

        // The start position belongs to that file only.
        await engine.OpenAsync(TestMedia.PathOf("vp9-opus.webm"), TimeSpan.Zero);
        Assert.True(engine.Position.TotalSeconds < 2, $"second open started at {engine.Position.TotalSeconds:0.00}");
    }

    [MediaFact]
    public async Task Volume_mute_and_speed_reach_the_engine()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("h264-aac-timedtext.mp4"), TimeSpan.Zero);

        engine.Volume = 0.4;
        engine.IsMuted = true;
        engine.Rate = 1.5;
        Assert.True(await TestMedia.WaitAsync(() => Math.Abs(Number(engine, "volume") - 40) < 0.01 && engine.ReadProperty("mute") == "yes" && Math.Abs(Number(engine, "speed") - 1.5) < 0.001));

        var before = engine.Position;
        await Task.Delay(1000);
        var advanced = (engine.Position - before).TotalSeconds;
        Assert.InRange(advanced, 1.1, 2.0);
    }

    [MediaFact]
    public async Task Aspect_modes_set_the_matching_options()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("h264-aac-timedtext.mp4"), TimeSpan.Zero);

        engine.Aspect = AspectMode.Fill;
        Assert.True(await TestMedia.WaitAsync(() => Number(engine, "panscan") == 1));
        engine.Aspect = AspectMode.Standard;
        Assert.True(await TestMedia.WaitAsync(() => Math.Abs(Number(engine, "video-aspect-override") - (4.0 / 3)) < 0.01 && Number(engine, "panscan") == 0));
        engine.Aspect = AspectMode.Stretch;
        Assert.True(await TestMedia.WaitAsync(() => engine.ReadProperty("keepaspect") == "no"));
        engine.Aspect = AspectMode.Original;
        Assert.True(await TestMedia.WaitAsync(() => Number(engine, "video-aspect-override") == -1 && engine.ReadProperty("keepaspect") == "yes"));
    }

    [MediaFact]
    public async Task Lists_and_switches_audio_and_subtitle_tracks()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("multi-track.mkv"), TimeSpan.Zero);
        // The track list can arrive before mpv has selected the default tracks in it.
        Assert.True(
            await TestMedia.WaitAsync(() => engine.Tracks.Count == 5 && engine.Tracks.Any(t => t.Kind == TrackKind.Audio && t.IsSelected)),
            $"tracks: {engine.Tracks.Count}");

        var audio = engine.Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
        Assert.Equal(["eng", "jpn"], audio.Select(t => t.Language));
        Assert.True(audio[0].IsSelected);
        Assert.Equal("English · Original", TrackLabels.Name(audio[0]));
        Assert.Equal("Dolby Digital · 5.1", TrackLabels.Detail(audio[1]));

        engine.SelectAudioTrack(audio[1].Id);
        Assert.True(await TestMedia.WaitAsync(() => engine.Tracks.Single(t => t.Kind == TrackKind.Audio && t.IsSelected).Language == "jpn"));
        Assert.True(await TestMedia.WaitAsync(() => engine.ReadProperty("audio-codec-name") == "ac3"));

        var subtitles = engine.Tracks.Where(t => t.Kind == TrackKind.Subtitle).ToList();
        Assert.Equal(["eng", "fre"], subtitles.Select(t => t.Language));
        Assert.Equal("French", TrackLabels.Name(subtitles[1]));
        engine.SelectSubtitleTrack(subtitles[1].Id);
        Assert.True(await TestMedia.WaitAsync(() => engine.ReadProperty("current-tracks/sub/lang") == "fre"));
        engine.SelectSubtitleTrack(null);
        Assert.True(await TestMedia.WaitAsync(() => engine.ReadProperty("sid") == "no"));
        TestMedia.Record("multi-track.mkv (tracks)", "Switches", "2 audio and 2 subtitle tracks listed with languages; audio and subtitle switching and subtitles off verified");
    }

    [MediaFact]
    public async Task Loads_an_external_subtitle_file()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("multi-track.mkv"), TimeSpan.Zero);

        var error = await engine.AddSubtitleFileAsync(TestMedia.PathOf("extra-subtitles.ass"));
        Assert.Null(error);
        Assert.True(await TestMedia.WaitAsync(() => engine.Tracks.Any(t => t.Kind == TrackKind.Subtitle && t.IsExternal && t.IsSelected)));
        TestMedia.Record("extra-subtitles.ass", "Loads", "added to multi-track.mkv as a third, selected subtitle track");
    }

    [MediaFact]
    public async Task Reports_an_unreadable_subtitle_file()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("multi-track.mkv"), TimeSpan.Zero);
        var error = await engine.AddSubtitleFileAsync(TestMedia.PathOf("broken-subtitles.srt"));
        TestMedia.Record("broken-subtitles.srt", error is null ? "Accepted" : "Rejected", error ?? "libmpv accepted it");
        Assert.NotNull(error);
        Assert.Equal(PlaybackStatus.Playing, engine.Status);
    }

    [MediaFact]
    public async Task Loads_subtitles_named_like_the_video_automatically()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("h264-aac-timedtext.mp4"), TimeSpan.Zero);
        Assert.True(await TestMedia.WaitAsync(() => engine.Tracks.Count(t => t.Kind == TrackKind.Subtitle) == 2), "expected the embedded and the matching external subtitles");
        Assert.Contains(engine.Tracks, t => t.Kind == TrackKind.Subtitle && t.IsExternal);
        Assert.Contains(engine.Tracks, t => t.Kind == TrackKind.Subtitle && t.Codec == "mov_text");
    }

    [MediaFact]
    public async Task Diagnostics_describe_the_playing_file()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        engine.RendererDescription = () => "Test renderer";
        await engine.OpenAsync(TestMedia.PathOf("hevc10-ac3-51.mkv"), TimeSpan.Zero);
        Assert.True(await TestMedia.WaitAsync(() => engine.GetDiagnostics().Sections.Any(s => s.Title == "Audio" && s.Items.Any(i => i.Label == "Channels"))));
        var text = engine.GetDiagnostics().ToText();
        Assert.Contains("HEVC", text, StringComparison.Ordinal);
        Assert.Contains("1920×1080", text, StringComparison.Ordinal);
        Assert.Contains("5.1", text, StringComparison.Ordinal);
        Assert.Contains("Test renderer", text, StringComparison.Ordinal);
        Console.WriteLine(text);
    }

    [MediaFact]
    public async Task Close_releases_the_file()
    {
        using var engine = await TestMedia.CreateEngineAsync();
        await engine.OpenAsync(TestMedia.PathOf("vp9-opus.webm"), TimeSpan.Zero);
        engine.Close();
        Assert.Equal(PlaybackStatus.Idle, engine.Status);
        Assert.Empty(engine.Tracks);
        Assert.True(await TestMedia.WaitAsync(() => engine.ReadProperty("idle-active") == "yes"));
    }
}

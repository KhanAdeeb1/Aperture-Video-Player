using System.Net;
using Aperture.Core.Casting;
using Aperture.Core.Server;
using Aperture.Core.Settings;
using Aperture.Data;
using Aperture.Server.Casting;

namespace Aperture.Server.Tests;

/// <summary>
/// Casting through the real server to a renderer that behaves like a TV: what it is sent, what
/// is saved, and what happens when it fails, goes away, or is taken over. Requests go through
/// the same control client the desktop app uses.
/// </summary>
public sealed class CastingTests : IAsyncLifetime
{
    private static readonly CastTimings Quick = new(
        Poll: TimeSpan.FromMilliseconds(100),
        StartGrace: TimeSpan.FromSeconds(1),
        StartTimeout: TimeSpan.FromSeconds(8),
        GiveUp: TimeSpan.FromSeconds(1.5),
        SaveEvery: TimeSpan.FromMilliseconds(300));

    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private TestServer _server = null!;
    private FakeRenderer _tv = null!;
    private ServerControlClient _control = null!;
    private ServerStateFile _state = null!;
    private string _movie = null!;

    public async Task InitializeAsync() => await StartAsync(useFfmpeg: false);

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _tv.DisposeAsync();
    }

    [Fact]
    public async Task Finds_the_renderer_and_lists_its_name()
    {
        var renderers = await _control.GetRenderersAsync(_state, search: true);

        var tv = Assert.Single(renderers!);
        Assert.Equal(FakeRenderer.Udn, tv.Id);
        Assert.Equal("Living Room TV", tv.Name);
        Assert.Equal("Test Fake TV", tv.Model);
        Assert.Equal("127.0.0.1", tv.Address);
    }

    [Fact]
    public async Task Casts_from_the_desktop_position_and_saves_where_the_TV_is()
    {
        await StartCastAsync(startSeconds: 30);

        var playing = await WaitForAsync(s => s.State == CastState.Playing && s.PositionSeconds >= 30.5);
        Assert.Equal("Living Room TV", playing.RendererName);
        Assert.Contains("Seek", _tv.Actions);
        Assert.True(_tv.Position >= 30, "the TV should have been asked to start at the desktop's position");

        // The TV fetched the original file, with DLNA headers, from an address made for it.
        var fetch = Assert.Single(_tv.Fetches);
        Assert.Equal(HttpStatusCode.OK, fetch.Status);
        Assert.Equal("video/mp4", fetch.Type);
        Assert.StartsWith("DLNA.ORG_OP=01", fetch.Features, StringComparison.Ordinal);
        Assert.Contains("<dc:title>Night Train</dc:title>", _tv.Metadata, StringComparison.Ordinal);

        var saved = await WaitForHistoryAsync(e => e.Position.TotalSeconds >= 30.5);
        Assert.Equal(600, saved.Duration.TotalSeconds);
    }

    [Fact]
    public async Task A_TV_reporting_zero_while_loading_never_wipes_the_resume_point()
    {
        await _server.History.SaveAsync(new Core.Library.HistoryEntry(_movie, "Night Train", TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(600), DateTimeOffset.UtcNow));
        _tv.RefuseSeek = true; // starts from 0:00 and can't be moved

        await StartCastAsync(startSeconds: 300);
        await WaitForAsync(s => s.State == CastState.Playing);
        await Task.Delay(1500);

        // The TV is playing from 0:00, which isn't trusted until it has played for a minute from there.
        var entry = await _server.History.FindAsync(_movie);
        Assert.Equal(300, entry!.Position.TotalSeconds);
        Assert.Contains("doesn't accept a starting point", (await StatusAsync()).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pause_play_and_seek_reach_the_TV_and_stop_ends_cleanly()
    {
        await StartCastAsync(startSeconds: 0);
        await WaitForAsync(s => s.State == CastState.Playing && s.PositionSeconds > 0.3);

        Assert.True(await _control.CastCommandAsync(_state, CastCommand.Pause));
        await WaitForAsync(s => s.State == CastState.Paused);
        Assert.Equal("PAUSED_PLAYBACK", _tv.State);

        Assert.True(await _control.CastSeekAsync(_state, 120));
        Assert.True(await _control.CastCommandAsync(_state, CastCommand.Play));
        await WaitForAsync(s => s.State == CastState.Playing && s.PositionSeconds >= 120);

        var media = new Uri(_tv.Uri!);
        Assert.True(await _control.CastCommandAsync(_state, CastCommand.Stop));
        var stopped = await StatusAsync();
        Assert.Equal(CastState.Stopped, stopped.State);
        Assert.False(stopped.IsActive);
        Assert.Equal("STOPPED", _tv.State);
        Assert.True((await _server.History.FindAsync(_movie))!.Position.TotalSeconds >= 120);

        // The address the TV used stops working with the cast.
        using var after = await Http.GetAsync(media);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }

    [Fact]
    public async Task Media_is_served_only_to_the_TV_it_was_made_for()
    {
        await StartCastAsync(startSeconds: 0);
        await WaitForAsync(s => s.State == CastState.Playing);
        var media = new Uri(_tv.Uri!);

        _server.Context.RemoteAddress = _ => IPAddress.Parse("192.168.1.77"); // another device on the network
        using (var other = await Http.GetAsync(media))
        {
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        }

        _server.Context.RemoteAddress = c => c.Connection.RemoteIpAddress;
        using (var made = await Http.GetAsync(new Uri(media, "/cast/" + new string('a', 48) + "/media.mp4")))
        {
            Assert.Equal(HttpStatusCode.NotFound, made.StatusCode);
        }

        using var range = new HttpRequestMessage(HttpMethod.Get, media);
        range.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 19);
        using var partial = await Http.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal((await File.ReadAllBytesAsync(_movie))[10..20], await partial.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_TV_that_stops_answering_keeps_the_place_and_ends_as_disconnected()
    {
        await StartCastAsync(startSeconds: 0);
        await WaitForAsync(s => s.State == CastState.Playing && s.PositionSeconds > 0.5);
        await WaitForHistoryAsync(e => e.Position.TotalSeconds > 0.5);

        _tv.Unplugged = true;
        var reconnecting = await WaitForAsync(s => s.State == CastState.Reconnecting);
        Assert.True(reconnecting.IsActive);
        Assert.Contains("stopped answering", reconnecting.Message, StringComparison.Ordinal);
        var kept = (await _server.History.FindAsync(_movie))!.Position;

        var ended = await WaitForAsync(s => s.State == CastState.Disconnected, TimeSpan.FromSeconds(15));
        Assert.False(ended.IsActive);
        Assert.Equal(kept, (await _server.History.FindAsync(_movie))!.Position);
    }

    [Fact]
    public async Task A_TV_that_comes_back_carries_on()
    {
        await StartCastAsync(startSeconds: 0);
        await WaitForAsync(s => s.State == CastState.Playing);

        _tv.Unplugged = true;
        await WaitForAsync(s => s.State == CastState.Reconnecting);
        _tv.Unplugged = false;

        await WaitForAsync(s => s.State == CastState.Playing);
    }

    [Fact]
    public async Task Something_else_playing_on_the_TV_ends_the_cast_without_stopping_it()
    {
        await StartCastAsync(startSeconds: 0);
        await WaitForAsync(s => s.State == CastState.Playing && s.PositionSeconds > 0.3);

        _tv.TakeOver("http://192.168.1.9:9000/someone-elses-video.mp4");
        var ended = await WaitForAsync(s => !s.IsActive);

        Assert.Equal(CastState.Stopped, ended.State);
        Assert.Contains("something else", ended.Message, StringComparison.Ordinal);
        Assert.Equal("PLAYING", _tv.State); // left alone
        Assert.DoesNotContain("Stop", _tv.Actions);
    }

    [Fact]
    public async Task Playing_to_the_end_marks_it_finished()
    {
        _tv.Duration = 600;
        await StartCastAsync(startSeconds: 590);
        await WaitForAsync(s => s.State == CastState.Playing && s.PositionSeconds >= 590);

        _tv.PlayToEnd();
        var ended = await WaitForAsync(s => !s.IsActive);

        Assert.Equal(CastState.Ended, ended.State);
        var entry = await _server.History.FindAsync(_movie);
        Assert.True(Core.Library.WatchProgress.IsFinished(entry!));
    }

    [Fact]
    public async Task Nothing_is_saved_when_history_is_turned_off()
    {
        await new SqliteSettingsStore(_server.Database).SaveAsync(AppSettings.Default with { RememberHistory = false });
        await StartCastAsync(startSeconds: 0);
        await WaitForAsync(s => s.State == CastState.Playing && s.PositionSeconds > 0.5);
        await Task.Delay(500);

        await _control.CastCommandAsync(_state, CastCommand.Stop);
        Assert.Null(await _server.History.FindAsync(_movie));
    }

    [Fact]
    public async Task Without_FFmpeg_a_file_the_TV_doesnt_accept_fails_with_a_reason()
    {
        var mkv = Path.Combine(_server.MediaFolder, "Other.mkv");
        await File.WriteAllBytesAsync(mkv, TestServer.RandomBytes(4096));
        await _control.GetRenderersAsync(_state, search: true);

        var (status, problem) = await _control.StartCastAsync(_state, new CastStartRequest(FakeRenderer.Udn, mkv, "Other", 0, 600));
        Assert.Null(problem);
        Assert.NotNull(status);

        var failed = await WaitForAsync(s => !s.IsActive);
        Assert.Equal(CastState.Failed, failed.State);
        Assert.Contains("needs FFmpeg", failed.Message, StringComparison.Ordinal);
        Assert.Null(_tv.Uri); // nothing was sent
    }

    [Fact]
    public async Task Refuses_files_that_dont_exist_and_unknown_TVs()
    {
        await _control.GetRenderersAsync(_state, search: true);

        var (_, missing) = await _control.StartCastAsync(_state, new CastStartRequest(FakeRenderer.Udn, Path.Combine(_server.MediaFolder, "nope.mp4"), "Nope", 0, 0));
        var (_, relative) = await _control.StartCastAsync(_state, new CastStartRequest(FakeRenderer.Udn, Path.Combine(_server.MediaFolder, "..", "media", "Night Train.mp4"), "x", 0, 0));
        var (_, unknown) = await _control.StartCastAsync(_state, new CastStartRequest("uuid:someone-else", _movie, "x", 0, 0));

        Assert.Equal("That file can't be found.", missing);
        Assert.Equal("That file can't be found.", relative);
        Assert.Contains("wasn't found", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cast_control_needs_the_control_token()
    {
        using var client = _server.SetupClient();
        using var response = await client.GetAsync("control/cast/status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var wrong = _state with { ControlToken = new string('0', 64) };
        Assert.Null(await _control.GetCastStatusAsync(wrong));
    }

    [FfmpegFact]
    public async Task A_TV_that_cant_play_the_original_gets_a_converted_stream()
    {
        await _server.DisposeAsync();
        await _tv.DisposeAsync();
        await StartAsync(useFfmpeg: true);
        var clip = Path.Combine(_server.MediaFolder, "Clip.mp4");
        TestServer.MakeClip(clip, "libx264", "aac", seconds: 20);
        _tv.FailOriginals = true;
        await _control.GetRenderersAsync(_state, search: true);

        await _control.StartCastAsync(_state, new CastStartRequest(FakeRenderer.Udn, clip, "Clip", 5, 20));
        var playing = await WaitForAsync(s => s.State == CastState.Playing, TimeSpan.FromSeconds(30));

        Assert.Contains("couldn't play the original", playing.Delivery, StringComparison.Ordinal);
        Assert.Equal(2, _tv.Fetches.Count);
        var converted = _tv.Fetches[1];
        Assert.Equal("video/mpeg", converted.Type);
        Assert.Equal(0x47, converted.Start[0]); // MPEG-TS sync byte
        Assert.StartsWith("DLNA.ORG_OP=00", converted.Features, StringComparison.Ordinal);
        Assert.EndsWith("/stream.ts", _tv.Uri, StringComparison.Ordinal);
        Assert.False(_tv.Actions.Contains("Seek"), "a converted stream starts at the right place itself");
    }

    [FfmpegFact]
    public async Task An_HEVC_file_is_converted_and_seeking_starts_a_new_stream()
    {
        await _server.DisposeAsync();
        await _tv.DisposeAsync();
        await StartAsync(useFfmpeg: true);
        var clip = Path.Combine(_server.MediaFolder, "Show S01E01.mkv");
        TestServer.MakeClip(clip, "libx265", "aac", seconds: 20);
        await _control.GetRenderersAsync(_state, search: true);

        await _control.StartCastAsync(_state, new CastStartRequest(FakeRenderer.Udn, clip, "Show", 0, 20));
        var playing = await WaitForAsync(s => s.State == CastState.Playing, TimeSpan.FromSeconds(30));
        Assert.Contains("converted to H.264", playing.Delivery, StringComparison.Ordinal);
        var first = _tv.Uri;

        Assert.True(await _control.CastSeekAsync(_state, 10));
        await WaitForAsync(s => s.State == CastState.Playing && s.PositionSeconds >= 10, TimeSpan.FromSeconds(30));
        Assert.NotEqual(first, _tv.Uri);
        Assert.All(_tv.Fetches, f => Assert.Equal(0x47, f.Start[0]));
    }

    private async Task StartAsync(bool useFfmpeg)
    {
        _tv = await FakeRenderer.StartAsync();
        _server = await TestServer.StartAsync(useFfmpeg: useFfmpeg);
        _server.Context.Casting.Timings = Quick;
        _server.Context.Casting.Search = _ => Task.FromResult<IReadOnlyList<Uri>>([_tv.Location]);
        _control = new ServerControlClient(Http);
        _state = new ServerStateFile(Environment.ProcessId, _server.Server.Port, _server.Server.SetupPort, _server.Context.ControlToken, DateTimeOffset.UtcNow);
        _movie = Path.Combine(_server.MediaFolder, "Night Train.mp4");
        await File.WriteAllBytesAsync(_movie, TestServer.RandomBytes(256 * 1024));
    }

    private async Task StartCastAsync(double startSeconds)
    {
        await _control.GetRenderersAsync(_state, search: true);
        var (status, problem) = await _control.StartCastAsync(_state, new CastStartRequest(FakeRenderer.Udn, _movie, "Night Train", startSeconds, 600));
        Assert.Null(problem);
        Assert.True(status!.IsActive);
    }

    private async Task<CastStatus> StatusAsync() =>
        (await _control.GetCastStatusAsync(_state))!;

    private async Task<CastStatus> WaitForAsync(Func<CastStatus, bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        CastStatus status;
        do
        {
            status = await StatusAsync();
            if (condition(status))
            {
                return status;
            }

            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"The cast didn't get there; it is {status.State} at {status.PositionSeconds:0.0}s ({status.Message}).");
    }

    private async Task<Core.Library.HistoryEntry> WaitForHistoryAsync(Func<Core.Library.HistoryEntry, bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await _server.History.FindAsync(_movie) is { } entry && condition(entry))
            {
                return entry;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The position wasn't saved.");
    }
}

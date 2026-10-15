using System.Net;
using System.Net.Http.Json;
using Aperture.Core.Server;
using Aperture.Server.Media;

namespace Aperture.Server.Tests;

/// <summary>Remuxing and converting with the real FFmpeg. Skipped, not passed, where FFmpeg isn't installed.</summary>
public class ConversionTests
{
    [FfmpegFact]
    public async Task An_h264_mkv_is_repackaged_into_hls_for_a_phone()
    {
        long id = 0;
        string path = string.Empty;
        await using var test = await TestServer.StartAsync(
            async t =>
            {
                var films = await t.AddFolderAsync("Films", shared: true);
                path = Path.Combine(films.Path, "Night Market.mkv");
                TestServer.MakeClip(path, "libx264", "aac");
                id = (await t.AddItemAsync(films, "Night Market.mkv", "Night Market")).Id;
            },
            useFfmpeg: true);
        var original = await File.ReadAllBytesAsync(path);
        using var client = await test.SignedInClientAsync();

        using var play = await client.PostAsJsonAsync($"api/items/{id}/play", TestServer.PlayRequest(hls: true));
        var answer = await TestServer.ReadJsonAsync(play);
        Assert.Equal("remux", answer.GetProperty("mode").GetString());
        Assert.Contains("H.264 copied", answer.GetProperty("stream").GetString(), StringComparison.Ordinal);
        var url = answer.GetProperty("url").GetString()!.TrimStart('/');
        Assert.EndsWith("/index.m3u8", url, StringComparison.Ordinal);

        var playlist = await client.GetStringAsync(url);
        Assert.StartsWith("#EXTM3U", playlist, StringComparison.Ordinal);
        Assert.Contains("seg00000.ts", playlist, StringComparison.Ordinal);

        var segment = await client.GetByteArrayAsync(url.Replace("index.m3u8", "seg00000.ts", StringComparison.Ordinal));
        Assert.True(segment.Length > 1000);
        Assert.Equal(0x47, segment[0]); // MPEG-TS sync byte

        // The original is only read.
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(["Night Market.mkv"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [FfmpegFact]
    public async Task A_file_the_browser_cannot_play_is_converted_to_a_continuous_mp4()
    {
        long id = 0;
        await using var test = await TestServer.StartAsync(
            async t =>
            {
                var films = await t.AddFolderAsync("Films", shared: true);
                TestServer.MakeClip(Path.Combine(films.Path, "Archive.avi"), "mpeg4", "mp3", seconds: 4);
                id = (await t.AddItemAsync(films, "Archive.avi", "Archive")).Id;
            },
            useFfmpeg: true);
        using var client = await test.SignedInClientAsync();

        using var play = await client.PostAsJsonAsync($"api/items/{id}/play", TestServer.PlayRequest(hls: false));
        var answer = await TestServer.ReadJsonAsync(play);
        Assert.Equal("convert", answer.GetProperty("mode").GetString());
        Assert.Contains("MPEG-4 Part 2", answer.GetProperty("source").GetString(), StringComparison.Ordinal);
        var url = answer.GetProperty("url").GetString()!.TrimStart('/');
        Assert.EndsWith("/stream.mp4", url, StringComparison.Ordinal);

        var mp4 = await client.GetByteArrayAsync(url);
        Assert.True(mp4.Length > 10_000);
        Assert.Equal("ftyp"u8.ToArray(), mp4[4..8]);
    }

    [FfmpegFact]
    public async Task Segment_names_cannot_leave_the_conversion_folder()
    {
        long id = 0;
        await using var test = await TestServer.StartAsync(
            async t =>
            {
                var films = await t.AddFolderAsync("Films", shared: true);
                TestServer.MakeClip(Path.Combine(films.Path, "Night Market.mkv"), "libx264", "aac", seconds: 4);
                id = (await t.AddItemAsync(films, "Night Market.mkv", "Night Market")).Id;
            },
            useFfmpeg: true);
        using var client = await test.SignedInClientAsync();
        using var play = await client.PostAsJsonAsync($"api/items/{id}/play", TestServer.PlayRequest(hls: true));
        var session = (await TestServer.ReadJsonAsync(play)).GetProperty("streamId").GetString();
        _ = await client.GetStringAsync($"media/{session}/index.m3u8");

        foreach (var name in new[] { "..%2f..%2faperture.db", "index.m3u8.tmp", "seg0.ts", "seg00000.ts.tmp", "%2e%2e" })
        {
            using var response = await client.GetAsync($"media/{session}/{name}");
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{name} answered {response.StatusCode}");
        }
    }

    [Fact]
    public void Ffmpeg_gets_separate_arguments_and_reads_files_only_as_files()
    {
        var plan = new StreamPlan(StreamMode.Convert, "test", TargetHeight: 720, VideoBitrateKbps: 4000);
        var path = "/media/Films/x; rm -rf ~ $(whoami).mkv";

        var hls = ConversionManager.BuildArguments(path, "/data/Server/Conversions/abc", plan, 61.5, null);
        var progressive = ConversionManager.BuildArguments(path, null, plan, 0, new H264Encoder("h264_nvenc", "NVIDIA NVENC", true));

        Assert.Contains("file:" + path, hls);
        Assert.Equal("61.5", hls[hls.ToList().IndexOf("-ss") + 1]);
        Assert.Contains("libx264", hls);
        Assert.Contains("hls", hls);
        Assert.Equal(Path.Combine("/data/Server/Conversions/abc", "index.m3u8"), hls[^1]);
        Assert.DoesNotContain("-ss", progressive);
        Assert.Contains("h264_nvenc", progressive);
        Assert.Equal("pipe:1", progressive[^1]);
        Assert.Contains("frag_keyframe+empty_moov+default_base_moof", progressive);
    }

    [Fact]
    public void Remuxing_copies_the_video()
    {
        var args = ConversionManager.BuildArguments("/m/a.mkv", "/c/x", new StreamPlan(StreamMode.Remux, "test", CopyVideo: true, CopyAudio: true), 0, null);

        Assert.Equal("copy", args[args.ToList().IndexOf("-c:v") + 1]);
        Assert.Equal("copy", args[args.ToList().IndexOf("-c:a") + 1]);
        Assert.DoesNotContain("-vf", args);
    }
}

using System.Security.Cryptography;

namespace Aperture.Playback.Mpv.Tests;

/// <summary>The library's file inspection, against real files.</summary>
[Collection("libmpv")]
public class MediaProbeTests
{
    private static Task<MpvMediaProbe> CreateAsync() => Task.Run(() => MpvMediaProbe.Create(static (_, _) => { }));

    [MediaTheory]
    [InlineData("h264-aac-timedtext.mp4", 1280, 720, "h264")]
    [InlineData("hevc10-ac3-51.mkv", 1920, 1080, "hevc")]
    [InlineData("mpeg2-anamorphic.ts", 720, 576, "mpeg2video")]
    [InlineData("portrait.mp4", 540, 960, "h264")]
    public async Task Reads_length_and_picture_size(string file, int width, int height, string codec)
    {
        using var probe = await CreateAsync();
        var result = await probe.ProbeAsync(TestMedia.PathOf(file));
        Assert.NotNull(result);
        Assert.Equal((width, height, codec), (result.Width!.Value, result.Height!.Value, result.VideoCodec));
        Assert.InRange(result.Duration!.Value.TotalSeconds, 7.5, 8.5);
        TestMedia.Record($"{file} (library probe)", "Inspected", $"{result.Width}x{result.Height} {result.VideoCodec}, {result.Duration.Value.TotalSeconds:0.0} s");
    }

    [MediaFact]
    public async Task Audio_only_files_have_a_length_and_no_picture()
    {
        using var probe = await CreateAsync();
        var result = await probe.ProbeAsync(TestMedia.PathOf("audio-only.mp4"));
        Assert.NotNull(result);
        Assert.Null(result.Width);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [MediaTheory]
    [InlineData("damaged.mp4")]
    [InlineData("empty.mp4")]
    [InlineData("missing.mp4")]
    public async Task Unreadable_files_return_nothing_and_the_probe_keeps_working(string file)
    {
        using var probe = await CreateAsync();
        Assert.Null(await probe.ProbeAsync(TestMedia.PathOf(file)));
        Assert.NotNull(await probe.ProbeAsync(TestMedia.PathOf("vp9-opus.webm")));
    }

    [MediaFact]
    public async Task Inspecting_never_changes_the_file()
    {
        var path = TestMedia.PathOf("multi-track.mkv");
        var before = (File.GetLastWriteTimeUtc(path), new FileInfo(path).Length, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        using (var probe = await CreateAsync())
        {
            for (var i = 0; i < 3; i++)
            {
                Assert.NotNull(await probe.ProbeAsync(path));
            }
        }

        var after = (File.GetLastWriteTimeUtc(path), new FileInfo(path).Length, SHA256.HashData(await File.ReadAllBytesAsync(path)));
        Assert.Equal(before.Item1, after.Item1);
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before.Item3, after.Item3);
    }

    [MediaFact]
    public async Task Probing_many_files_in_a_row_is_quick()
    {
        using var probe = await CreateAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            Assert.NotNull(await probe.ProbeAsync(TestMedia.PathOf(i % 2 == 0 ? "vp9-opus.webm" : "h264-aac-timedtext.mp4")));
        }

        Console.WriteLine($"20 probes took {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
    }
}

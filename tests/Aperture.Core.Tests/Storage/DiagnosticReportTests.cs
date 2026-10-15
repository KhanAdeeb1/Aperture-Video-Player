using Aperture.Core.Diagnostics;

namespace Aperture.Core.Tests.Storage;

public class DiagnosticReportTests
{
    [Theory]
    [InlineData(@"Could not open D:\Films\Secret Holiday 2024.mkv", "Could not open <path>.mkv")]
    [InlineData(@"read \\nas\video\Home Movies\clip.mp4 | IOException", "read <path>.mp4 | IOException")]
    [InlineData("libmpv [ffmpeg] /home/sam/Videos/x.webm: error", "libmpv [ffmpeg] <path>.webm: error")]
    [InlineData(@"folder C:\Users\sam\Videos", "folder <path>")]
    [InlineData("Loaded mkv: video h264 1920x1080, audio aac, hwdec d3d11va", "Loaded mkv: video h264 1920x1080, audio aac, hwdec d3d11va")]
    [InlineData("speed 1.5/2.0 at 0:12", "speed 1.5/2.0 at 0:12")]
    public void Paths_are_removed(string line, string expected) =>
        Assert.Equal(expected, DiagnosticReport.Redact(line, userName: null));

    [Fact]
    public void Report_has_facts_problem_and_the_end_of_the_log_without_names()
    {
        var log = Enumerable.Range(1, 80).Select(i => $"line {i}").Append(@"WARN Sam could not open C:\Users\Sam\Clip.mov").ToList();
        var report = DiagnosticReport.Build(
            [("Aperture", "0.9.0"), ("Windows", "10.0.26100"), ("Renderer", null)],
            "This video stopped unexpectedly (Failed): demuxer error",
            log,
            "Sam");
        Assert.Contains("Aperture: 0.9.0", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Renderer", report, StringComparison.Ordinal);
        Assert.Contains("demuxer error", report, StringComparison.Ordinal);
        Assert.DoesNotContain("line 21\n", report.Replace("\r", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("line 80", report, StringComparison.Ordinal);
        Assert.Contains("WARN <user> could not open <path>.mov", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Sam", report, StringComparison.Ordinal);
    }
}

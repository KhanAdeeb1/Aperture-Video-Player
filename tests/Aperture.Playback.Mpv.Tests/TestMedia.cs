using Aperture.Core.Playback;
using Aperture.Playback.Mpv.Interop;

namespace Aperture.Playback.Mpv.Tests;

/// <summary>Where the generated test files are, and whether libmpv can run here.</summary>
internal static class TestMedia
{
    private static readonly Lazy<string?> Unavailable = new(() =>
    {
        var folder = Environment.GetEnvironmentVariable("APERTURE_TEST_MEDIA");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return "APERTURE_TEST_MEDIA is not set to a folder made by tools/test-media/make-engine-test-media.sh";
        }

        try
        {
            MpvLibrary.EnsureLoaded();
            return null;
        }
        catch (MpvUnavailableException error)
        {
            return error.Message;
        }
    });

    private static readonly object ResultsGate = new();

    /// <summary>Why integration tests cannot run here, or null when they can.</summary>
    public static string? SkipReason => Unavailable.Value;

    public static string PathOf(string name) =>
        Path.Combine(Environment.GetEnvironmentVariable("APERTURE_TEST_MEDIA")!, name);

    /// <summary>An engine that decodes without showing a picture or playing sound.</summary>
    public static Task<MpvPlaybackEngine> CreateEngineAsync() =>
        // Created off any test synchronisation context, so events arrive in order on libmpv's thread.
        Task.Run(() => MpvPlaybackEngine.Create(new MpvEngineOptions
        {
            VideoOutput = "null",
            AudioOutput = "null",
            HardwareDecoding = "no",
            Log = static (level, text) =>
            {
                if (level is "error" or "fatal" or "warn")
                {
                    Console.WriteLine($"[{level}] {text}");
                }
            },
        }));

    /// <summary>Waits until <paramref name="condition"/> holds; returns false after <paramref name="seconds"/>.</summary>
    public static async Task<bool> WaitAsync(Func<bool> condition, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    /// <summary>Appends one row to the results table named by APERTURE_TEST_RESULTS, when set.</summary>
    public static void Record(string file, string outcome, string details)
    {
        var target = Environment.GetEnvironmentVariable("APERTURE_TEST_RESULTS");
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        lock (ResultsGate)
        {
            File.AppendAllText(target, $"| {file} | {outcome} | {details.Replace("|", "/", StringComparison.Ordinal)} |{Environment.NewLine}");
        }
    }

    public static string Describe(MpvPlaybackEngine engine)
    {
        var tracks = engine.Tracks;
        string Count(TrackKind kind) => tracks.Count(t => t.Kind == kind).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var video = engine.ReadProperty("video-format") is { } format
            ? $"video {format} {engine.ReadProperty("video-params/w")}x{engine.ReadProperty("video-params/h")} "
              + $"(shown {engine.ReadProperty("video-params/dw")}x{engine.ReadProperty("video-params/dh")})"
            : "no video";
        return $"{engine.ReadProperty("file-format")}; {video}; "
            + $"audio {engine.ReadProperty("audio-codec-name") ?? "none"} {engine.ReadProperty("audio-params/hr-channels")}; "
            + $"tracks v{Count(TrackKind.Video)} a{Count(TrackKind.Audio)} s{Count(TrackKind.Subtitle)}; "
            + $"duration {engine.Duration.TotalSeconds:0.0}s";
    }
}

/// <summary>A fact that needs real media and libmpv; skipped (not passed) without them.</summary>
public sealed class MediaFactAttribute : FactAttribute
{
    public MediaFactAttribute()
    {
        if (TestMedia.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>A theory that needs real media and libmpv; skipped (not passed) without them.</summary>
public sealed class MediaTheoryAttribute : TheoryAttribute
{
    public MediaTheoryAttribute()
    {
        if (TestMedia.SkipReason is { } reason)
        {
            Skip = reason;
        }
    }
}

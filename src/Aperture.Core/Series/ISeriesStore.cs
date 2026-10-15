namespace Aperture.Core.Series;

/// <summary>How the last analysis of a file ended.</summary>
public enum AnalysisOutcome
{
    /// <summary>The file was compared with the other episodes (finding something or not).</summary>
    Done,

    /// <summary>The file has no sound to compare.</summary>
    NoAudio,

    /// <summary>The file could not be read.</summary>
    Failed,
}

/// <summary>When and how a file was last analysed.</summary>
/// <param name="Fingerprint">The file's content fingerprint.</param>
/// <param name="DetectorVersion">The detection method's version; a newer one analyses again.</param>
/// <param name="Signature">Which other episodes it was compared with; analysis repeats when that set changes.</param>
/// <param name="AnalyzedUtc">When.</param>
/// <param name="Outcome">How it ended.</param>
public sealed record AnalysisRecord(string Fingerprint, int DetectorVersion, string Signature, DateTimeOffset AnalyzedUtc, AnalysisOutcome Outcome);

/// <summary>
/// Stores segments, the user's corrections and per-show preferences in Aperture's database.
/// Segments and corrections belong to a file's content fingerprint, not its path: they follow
/// the file when it is renamed or moved and are shared by identical copies, while a different
/// release of the same episode (another cut or length) has its own.
/// </summary>
public interface ISeriesStore
{
    Task<EpisodeSegments> GetSegmentsAsync(string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>Replaces what detection found for a file and records the analysis. Corrections are kept.</summary>
    Task SaveDetectedAsync(AnalysisRecord record, IReadOnlyList<MediaSegment> segments, CancellationToken cancellationToken = default);

    /// <summary>Saves the user's times for a segment, or "none" when both are null.</summary>
    Task SaveCorrectionAsync(string fingerprint, SegmentCorrection correction, CancellationToken cancellationToken = default);

    /// <summary>Forgets the user's correction, so detection applies again.</summary>
    Task ClearCorrectionAsync(string fingerprint, SegmentKind kind, CancellationToken cancellationToken = default);

    /// <summary>Every analysis record, by fingerprint.</summary>
    Task<IReadOnlyDictionary<string, AnalysisRecord>> GetAnalysesAsync(CancellationToken cancellationToken = default);

    Task<SeriesPreferences> GetPreferencesAsync(string seriesKey, CancellationToken cancellationToken = default);

    Task SavePreferencesAsync(string seriesKey, SeriesPreferences preferences, CancellationToken cancellationToken = default);

    /// <summary>The languages, speed and sound last picked while watching the show.</summary>
    Task<ShowChoices> GetShowChoicesAsync(string seriesKey, CancellationToken cancellationToken = default);

    /// <summary>Replaces what is carried between the show's episodes; <see cref="ShowChoices.None"/> forgets it.</summary>
    Task SaveShowChoicesAsync(string seriesKey, ShowChoices choices, CancellationToken cancellationToken = default);
}

/// <summary>A stretch of a file.</summary>
public readonly record struct TimeRange(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Length => End - Start;
}

/// <summary>A chapter mark in a file.</summary>
/// <param name="Title">The chapter's name, if it has one.</param>
/// <param name="Start">Where it starts.</param>
public sealed record ChapterMark(string? Title, TimeSpan Start);

/// <summary>Mono 16-bit sound decoded from part of a file.</summary>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Start">Where in the file the first sample is.</param>
/// <param name="Samples">The samples.</param>
public sealed record AudioSamples(int SampleRate, TimeSpan Start, short[] Samples)
{
    public TimeSpan Length => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}

/// <summary>
/// Reads what intro and credits detection needs from a media file: its sound, the stretches
/// where the picture is almost entirely dark, and its chapters. Files are only read.
/// </summary>
public interface IMediaSignals
{
    /// <summary>Decodes mono sound from <paramref name="start"/> for <paramref name="length"/> (to the end when null), or null when the file has none.</summary>
    Task<AudioSamples?> ReadAudioAsync(string path, TimeSpan start, TimeSpan? length, int sampleRate, CancellationToken cancellationToken = default);

    /// <summary>Stretches between <paramref name="start"/> and the end where the picture is nearly all dark, or null when there is no picture.</summary>
    Task<IReadOnlyList<TimeRange>?> FindDarkStretchesAsync(string path, TimeSpan start, CancellationToken cancellationToken = default);

    /// <summary>The file's chapters (empty when it has none).</summary>
    Task<IReadOnlyList<ChapterMark>> ReadChaptersAsync(string path, CancellationToken cancellationToken = default);
}

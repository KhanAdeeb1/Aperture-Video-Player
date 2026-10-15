using System.Globalization;
using System.Text.RegularExpressions;
using Aperture.Core.Series;

namespace Aperture.Media.Analysis;

/// <summary>What was measured in one episode file.</summary>
/// <param name="Id">The file's content fingerprint.</param>
/// <param name="Duration">Its length.</param>
/// <param name="Audio">Its sound fingerprint, or null when it has no sound.</param>
/// <param name="DarkFrom">Where the dark-picture measurement started (the tail of the file).</param>
/// <param name="Dark">Stretches after <paramref name="DarkFrom"/> where the picture is nearly all dark, or null when not measured.</param>
/// <param name="Chapters">The file's chapters.</param>
public sealed record EpisodeSignals(
    string Id,
    TimeSpan Duration,
    AudioPrint? Audio,
    TimeSpan DarkFrom,
    IReadOnlyList<TimeRange>? Dark,
    IReadOnlyList<ChapterMark> Chapters);

/// <summary>
/// Finds an episode's recap, intro and credits by measurement, never by assuming they sit
/// where another episode's did:
/// <list type="bullet">
/// <item>Intro: the stretch near the start whose sound recurs in other episodes of the show,
/// wherever it sits in each (cold opens differ). Several episodes must agree for high confidence.</item>
/// <item>Credits: the stretch near the end whose sound recurs in other episodes' endings, and/or
/// a long nearly-dark picture (text on black). Agreement of both is the strongest evidence. A
/// scene after the credits is detected, so it is never skipped.</item>
/// <item>Recap: short excerpts at the very start whose sound appears in the previous episodes.</item>
/// <item>Chapters named for any of these ("Opening", "Previously", "End Credits") are used when present.</item>
/// </list>
/// Confidence reflects how much evidence agrees; <see cref="SegmentPolicy"/> decides what
/// each level allows, and nothing uncertain is ever skipped automatically.
/// </summary>
public static partial class SegmentDetector
{
    /// <summary>Raised when the method changes, so files are analysed again.</summary>
    public const int Version = 1;

    /// <summary>Episodes compared with each file to find its intro and credits.</summary>
    public const int PeerCount = 4;

    public static readonly TimeSpan IntroMinimum = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan IntroMaximum = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan CreditsMinimum = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan DarkCreditsMinimum = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan RecapMinimum = TimeSpan.FromSeconds(10);

    /// <summary>Where intros are looked for: the first 45 % of an episode, at most 12 minutes.</summary>
    public static TimeSpan HeadEnd(TimeSpan duration) => Min(duration * 0.45, TimeSpan.FromMinutes(12));

    /// <summary>Where credits are looked for: the last 45 % of an episode, at most 10 minutes.</summary>
    public static TimeSpan TailStart(TimeSpan duration) => Max(duration * 0.55, duration - TimeSpan.FromMinutes(10));

    /// <summary>
    /// Finds the segments of <paramref name="episode"/>, comparing it with <paramref name="peers"/>
    /// (other episodes of the show, nearest first) and <paramref name="previous"/> (the episodes
    /// just before it, for recaps).
    /// </summary>
    public static IReadOnlyList<MediaSegment> Detect(
        EpisodeSignals episode,
        IReadOnlyList<EpisodeSignals> peers,
        IReadOnlyList<EpisodeSignals> previous,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(peers);
        ArgumentNullException.ThrowIfNull(previous);

        var segments = new List<MediaSegment>();
        var intro = FindIntro(episode, peers, cancellationToken);
        if (intro is not null)
        {
            segments.Add(intro);
        }

        var credits = FindCredits(episode, peers, cancellationToken);
        if (credits is not null)
        {
            segments.Add(credits);
        }

        var recap = FindRecap(episode, intro, previous, cancellationToken);
        if (recap is not null)
        {
            segments.Add(recap);
        }

        return segments;
    }

    // ===== Intro =====

    internal static MediaSegment? FindIntro(EpisodeSignals episode, IReadOnlyList<EpisodeSignals> peers, CancellationToken cancellationToken)
    {
        var chapter = ChapterSegment(episode, SegmentKind.Intro, IntroChapterPattern());
        MediaSegment? measured = null;
        if (episode.Audio is { } audio)
        {
            var candidates = new List<Candidate>();
            var compared = 0;
            foreach (var peer in peers.Take(PeerCount))
            {
                if (peer.Audio is not { } other || peer.Id == episode.Id)
                {
                    continue;
                }

                compared++;
                var headA = audio.FrameAt(HeadEnd(episode.Duration));
                var headB = other.FrameAt(HeadEnd(peer.Duration));
                var matches = PrintMatcher.FindMatches(audio, 0, headA, other, 0, headB, MatchOptions.Sequence(IntroMinimum), cancellationToken);
                if (matches.Any(m => Seconds(m.Length) > IntroMaximum.TotalSeconds || m.Length >= 0.9 * Math.Min(headA, headB)))
                {
                    // The two openings are the same for minutes: a re-release or a duplicate,
                    // which says nothing about where the intro is.
                    compared--;
                    continue;
                }

                if (PrintMatcher.Longest(matches) is { } best)
                {
                    candidates.Add(new Candidate(audio.TimeAt(best.StartA), audio.TimeAt(best.EndA - 1), best.ErrorRate));
                }
            }

            measured = Agree(SegmentKind.Intro, candidates, compared, "other episodes share this opening");
        }

        return Combine(chapter, measured);
    }

    // ===== Credits =====

    internal static MediaSegment? FindCredits(EpisodeSignals episode, IReadOnlyList<EpisodeSignals> peers, CancellationToken cancellationToken)
    {
        var chapter = ChapterSegment(episode, SegmentKind.Credits, CreditsChapterPattern());

        MediaSegment? sound = null;
        if (episode.Audio is { } audio)
        {
            var candidates = new List<Candidate>();
            var compared = 0;
            foreach (var peer in peers.Take(PeerCount))
            {
                if (peer.Audio is not { } other || peer.Id == episode.Id)
                {
                    continue;
                }

                compared++;
                var matches = PrintMatcher.FindMatches(
                    audio, audio.FrameAt(TailStart(episode.Duration)), audio.Count,
                    other, other.FrameAt(TailStart(peer.Duration)), other.Count,
                    MatchOptions.Sequence(CreditsMinimum),
                    cancellationToken);
                if (PrintMatcher.Longest(matches) is { } best)
                {
                    candidates.Add(new Candidate(audio.TimeAt(best.StartA), audio.TimeAt(best.EndA - 1), best.ErrorRate));
                }
            }

            sound = Agree(SegmentKind.Credits, candidates, compared, "other episodes end with the same music");
        }

        var picture = DarkCredits(episode);
        MediaSegment? measured = (sound, picture) switch
        {
            ({ } s, { } p) when Overlap(s.Start, s.End, p.Start, p.End) > TimeSpan.Zero =>
                // Both agree. Start at the later of the two, so the last scene is never cut short.
                new MediaSegment(SegmentKind.Credits, Max(s.Start, p.Start), Max(s.End, p.End), Math.Max(0.95, s.Confidence), SegmentSource.Detected, "The ending music and a dark picture agree"),
            ({ } s, _) => s,
            (_, { } p) => p,
            _ => null,
        };

        return Combine(chapter, measured);
    }

    /// <summary>A long stretch of nearly dark picture near the end, typical of credits on black.</summary>
    private static MediaSegment? DarkCredits(EpisodeSignals episode)
    {
        if (episode.Dark is not { Count: > 0 } dark)
        {
            return null;
        }

        // Credits often cut briefly to a lighter card; bridge short gaps.
        var merged = new List<TimeRange>();
        foreach (var range in dark.Where(r => r.Start >= episode.DarkFrom - TimeSpan.FromSeconds(1)).OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && range.Start - merged[^1].End <= TimeSpan.FromSeconds(3))
            {
                merged[^1] = new TimeRange(merged[^1].Start, Max(merged[^1].End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }

        var longest = merged.Where(r => r.Length >= DarkCreditsMinimum).OrderByDescending(r => r.Length).FirstOrDefault();
        if (longest.Length < DarkCreditsMinimum)
        {
            return null;
        }

        var toEnd = longest.End >= episode.Duration - EpisodeCueTracker.PostCreditsTolerance;
        if (!toEnd && longest.Length < TimeSpan.FromSeconds(45))
        {
            // A dark stretch with more of the episode after it may be a night scene.
            return null;
        }

        return new MediaSegment(SegmentKind.Credits, longest.Start, toEnd ? episode.Duration : longest.End, toEnd ? 0.75 : 0.65, SegmentSource.Detected, "The picture is dark, as for credits");
    }

    // ===== Recap =====

    internal static MediaSegment? FindRecap(EpisodeSignals episode, MediaSegment? intro, IReadOnlyList<EpisodeSignals> previous, CancellationToken cancellationToken)
    {
        var chapter = ChapterSegment(episode, SegmentKind.Recap, RecapChapterPattern());
        if (chapter is not null)
        {
            return chapter;
        }

        if (episode.Audio is not { } audio || previous.Count == 0)
        {
            return null;
        }

        // Recaps open the episode, before the intro when there is one.
        // The intro's own theme also plays in the earlier episodes, so it is never part of a recap.
        var regionEnd = Min(HeadEnd(episode.Duration), TimeSpan.FromMinutes(5));
        if (intro is not null)
        {
            regionEnd = Min(regionEnd, intro.Start);
        }

        if (regionEnd < RecapMinimum)
        {
            return null;
        }

        var to = audio.FrameAt(regionEnd);
        var covered = new bool[to];
        foreach (var earlier in previous.Take(2))
        {
            if (earlier.Audio is not { } other || earlier.Id == episode.Id)
            {
                continue;
            }

            foreach (var match in PrintMatcher.FindMatches(audio, 0, to, other, 0, other.Count, MatchOptions.Clips(TimeSpan.FromSeconds(2)), cancellationToken))
            {
                // A whole minute of the same sound is a repeated scene or a shared theme, not a montage.
                if (Seconds(match.Length) > 60)
                {
                    continue;
                }

                for (var i = match.StartA; i < match.EndA; i++)
                {
                    covered[i] = true;
                }
            }
        }

        var clips = new List<(int Start, int End)>();
        for (var i = 0; i < to; i++)
        {
            if (!covered[i])
            {
                continue;
            }

            var start = i;
            while (i < to && covered[i])
            {
                i++;
            }

            clips.Add((start, i));
        }

        if (clips.Count == 0)
        {
            return null;
        }

        // The recap must begin the episode (allowing a logo or "Previously on…").
        var firstStart = audio.TimeAt(clips[0].Start);
        if (firstStart > TimeSpan.FromSeconds(30))
        {
            return null;
        }

        // Clips separated by more than 20 seconds of new material are not one recap.
        var last = 0;
        for (var c = 1; c < clips.Count; c++)
        {
            if (Seconds(clips[c].Start - clips[last].End) > 20)
            {
                break;
            }

            last = c;
        }

        var start0 = firstStart <= TimeSpan.FromSeconds(10) ? TimeSpan.Zero : firstStart;
        var end = audio.TimeAt(clips[last].End - 1);
        var coveredFrames = clips.Take(last + 1).Sum(c => c.End - c.Start);
        var spanFrames = Math.Max(1, AudioPrint.Frames(end - start0));
        var coverage = (double)coveredFrames / spanFrames;
        var clipCount = last + 1;
        // A montage cuts between scenes; one long stretch is a repeated scene or a shared theme.
        if (end - start0 < RecapMinimum || coverage < 0.5 || clipCount < 2)
        {
            return null;
        }

        var confidence = clipCount >= 3 && coverage >= 0.7 ? 0.85 : 0.7;
        return new MediaSegment(SegmentKind.Recap, start0, end, confidence, SegmentSource.Detected, $"{clipCount} scenes from earlier episodes");
    }

    // ===== Agreement =====

    private readonly record struct Candidate(TimeSpan Start, TimeSpan End, double ErrorRate);

    /// <summary>
    /// Combines what each compared episode suggested. The suggestions that agree with the most
    /// others (overlapping by at least half) win; their median start and end become the
    /// segment, and the confidence grows with how many agree, how cleanly they matched and how
    /// closely their boundaries line up.
    /// </summary>
    private static MediaSegment? Agree(SegmentKind kind, List<Candidate> candidates, int compared, string evidence)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        List<Candidate> best = [];
        foreach (var candidate in candidates)
        {
            var agreeing = candidates.Where(other => Overlap(candidate.Start, candidate.End, other.Start, other.End) >= Min(candidate.End - candidate.Start, other.End - other.Start) * 0.5).ToList();
            if (agreeing.Count > best.Count || (agreeing.Count == best.Count && agreeing.Sum(a => (a.End - a.Start).TotalSeconds) > best.Sum(b => (b.End - b.Start).TotalSeconds)))
            {
                best = agreeing;
            }
        }

        var start = Median(best.Select(c => c.Start));
        var end = Median(best.Select(c => c.End));
        var support = best.Count;
        var confidence = support switch
        {
            1 => 0.65,
            2 => 0.8,
            _ => 0.9,
        };

        // Most compared episodes disagree: perhaps only a shared scene, not a recurring sequence.
        if (compared >= 3 && support * 2 < compared)
        {
            confidence -= 0.15;
        }

        var errorRate = best.Average(c => c.ErrorRate);
        if (errorRate > 0.25)
        {
            confidence -= 0.1;
        }
        else if (errorRate < 0.12 && support >= 3)
        {
            confidence += 0.05;
        }

        // Boundaries that scatter mean the episodes agree there is something here, not exactly where.
        var precise = best.Count(c => (c.Start - start).Duration() <= TimeSpan.FromSeconds(2) && (c.End - end).Duration() <= TimeSpan.FromSeconds(2));
        if (precise * 2 < support)
        {
            confidence -= 0.1;
        }

        var text = support == 1 ? $"1 other episode {Singular(evidence)}" : $"{support.ToString(CultureInfo.InvariantCulture)} {evidence}";
        return end - start >= TimeSpan.FromSeconds(3)
            ? new MediaSegment(kind, start, end, Math.Clamp(confidence, 0, 0.97), SegmentSource.Detected, Capitalize(text))
            : null;
    }

    // ===== Chapters =====

    private static MediaSegment? ChapterSegment(EpisodeSignals episode, SegmentKind kind, Regex pattern)
    {
        var chapters = episode.Chapters.OrderBy(c => c.Start).ToList();
        for (var i = 0; i < chapters.Count; i++)
        {
            if (chapters[i].Title is not { } title || !pattern.IsMatch(title.Trim()))
            {
                continue;
            }

            var start = chapters[i].Start;
            var end = i + 1 < chapters.Count ? chapters[i + 1].Start : episode.Duration;
            var length = end - start;
            var plausible = kind switch
            {
                SegmentKind.Intro => length >= TimeSpan.FromSeconds(5) && length <= IntroMaximum && start <= HeadEnd(episode.Duration),
                SegmentKind.Recap => length >= TimeSpan.FromSeconds(5) && length <= TimeSpan.FromMinutes(5) && start <= TimeSpan.FromMinutes(1),
                _ => length >= TimeSpan.FromSeconds(5) && start >= episode.Duration * 0.5,
            };
            if (plausible)
            {
                return new MediaSegment(kind, start, end, 0.9, SegmentSource.Chapter, $"Chapter “{title.Trim()}”");
            }
        }

        return null;
    }

    /// <summary>A named chapter is authored and precise; measurement that agrees with it confirms it.</summary>
    private static MediaSegment? Combine(MediaSegment? chapter, MediaSegment? measured)
    {
        if (chapter is null)
        {
            return measured;
        }

        if (measured is not null && Overlap(chapter.Start, chapter.End, measured.Start, measured.End) >= (chapter.End - chapter.Start) * 0.5)
        {
            return chapter with { Confidence = 0.95, Evidence = chapter.Evidence + ", confirmed by other episodes" };
        }

        return chapter;
    }

    // Opening, Intro, OP, Main Titles, Title Sequence, Theme.
    [GeneratedRegex(@"^(?:intro|introduction|opening(?: (?:titles|credits|theme))?|op\d?|main titles?|title sequence|theme(?: song)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex IntroChapterPattern();

    // Credits, End Credits, Ending, ED, Outro, Closing Credits.
    [GeneratedRegex(@"^(?:(?:end(?:ing)?|closing) ?credits?|credits|ending|ed\d?|outro|end titles?)$", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsChapterPattern();

    // Recap, Previously, Previously on…, Story so far.
    [GeneratedRegex(@"^(?:recap|previously(?: on\b.*)?|(?:the )?story so far)$", RegexOptions.IgnoreCase)]
    private static partial Regex RecapChapterPattern();

    // ===== Helpers =====

    private static double Seconds(int frames) => frames * AudioPrint.FrameSeconds;

    private static TimeSpan Overlap(TimeSpan aStart, TimeSpan aEnd, TimeSpan bStart, TimeSpan bEnd) =>
        Max(TimeSpan.Zero, Min(aEnd, bEnd) - Max(aStart, bStart));

    private static TimeSpan Median(IEnumerable<TimeSpan> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static string Singular(string evidence) => evidence
        .Replace("other episodes share", "shares", StringComparison.Ordinal)
        .Replace("other episodes end", "ends", StringComparison.Ordinal);

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

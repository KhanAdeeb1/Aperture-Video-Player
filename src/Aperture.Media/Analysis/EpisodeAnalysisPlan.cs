using Aperture.Core.Library;
using Aperture.Core.Series;

namespace Aperture.Media.Analysis;

/// <summary>One file to analyse, and the episodes it is compared with.</summary>
/// <param name="Target">The file.</param>
/// <param name="Peers">Other episodes of the show, nearest first, for intros and credits.</param>
/// <param name="Previous">The one or two episodes before it, for recaps.</param>
/// <param name="Signature">Identifies this comparison; the file is analysed again only when it changes.</param>
public sealed record AnalysisJob(LibraryItem Target, IReadOnlyList<LibraryItem> Peers, IReadOnlyList<LibraryItem> Previous, string Signature)
{
    public string Fingerprint => Target.Fingerprint!;
}

/// <summary>
/// Decides which episode files need analysing. Every copy of an episode is analysed on its
/// own, because a different release can have a different cut. Each is compared with the
/// nearest other episodes of the same show (same season first), using the copy from the
/// same folder where there is one, so a release is compared with its own release group. A
/// file is skipped when it was already analysed against the same episodes with the same
/// detection method.
/// </summary>
public static class EpisodeAnalysisPlan
{
    /// <summary>Shorter files are clips or extras, not episodes with intros.</summary>
    public static readonly TimeSpan ShortestEpisode = TimeSpan.FromMinutes(1);

    /// <summary>Longer files are not decoded in the background.</summary>
    public static readonly TimeSpan LongestEpisode = TimeSpan.FromHours(3);

    /// <summary>The jobs still to do, in watching order.</summary>
    public static IReadOnlyList<AnalysisJob> Build(SeriesCatalog catalog, IReadOnlyDictionary<string, AnalysisRecord> done)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(done);
        var jobs = new List<AnalysisJob>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var series in catalog.All)
        {
            foreach (var episode in series.Episodes)
            {
                foreach (var copy in episode.Group.All)
                {
                    if (!Analysable(copy) || !seen.Add(copy.Fingerprint!))
                    {
                        continue;
                    }

                    var job = For(catalog, series, episode, copy);
                    if (!done.TryGetValue(job.Fingerprint, out var record) || record.DetectorVersion != SegmentDetector.Version || record.Signature != job.Signature)
                    {
                        jobs.Add(job);
                    }
                }
            }
        }

        return jobs;
    }

    /// <summary>The job for one file, whether or not it is needed, or null when the file is not an analysable episode.</summary>
    public static AnalysisJob? For(SeriesCatalog catalog, string path)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SeriesOf(path) is not { } series || catalog.EpisodeOf(path) is not { } episode)
        {
            return null;
        }

        var copy = episode.Group.All.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
        return copy is not null && Analysable(copy) ? For(catalog, series, episode, copy) : null;
    }

    /// <summary>True for a present, measured file of episode length.</summary>
    public static bool Analysable(LibraryItem item) =>
        !item.IsMissing
        && !string.IsNullOrEmpty(item.Fingerprint)
        && item.Duration is { } duration
        && duration >= ShortestEpisode
        && duration <= LongestEpisode;

    private static AnalysisJob For(SeriesCatalog catalog, SeriesInfo series, SeriesEpisode episode, LibraryItem copy)
    {
        var index = IndexOf(series, episode);
        var peers = series.Episodes
            .Select((other, i) => (Other: other, Distance: Math.Abs(i - index)))
            .Where(p => !ReferenceEquals(p.Other, episode))
            .OrderBy(p => p.Other.Season == episode.Season && p.Other.IsSpecial == episode.IsSpecial ? 0 : 1)
            .ThenBy(p => p.Distance)
            .Select(p => p.Other.PreferredCopy(copy.Path))
            .Where(c => Analysable(c) && c.Fingerprint != copy.Fingerprint)
            .DistinctBy(c => c.Fingerprint)
            .Take(SegmentDetector.PeerCount)
            .ToList();

        var previous = new List<LibraryItem>();
        var at = copy.Path;
        for (var i = 0; i < 2 && catalog.Previous(at) is { } before; i++)
        {
            var chosen = before.PreferredCopy(copy.Path);
            if (Analysable(chosen) && chosen.Fingerprint != copy.Fingerprint)
            {
                previous.Add(chosen);
            }

            at = chosen.Path;
        }

        var signature = $"{SegmentDetector.Version}:{string.Join(',', peers.Select(p => p.Fingerprint))}|{string.Join(',', previous.Select(p => p.Fingerprint))}";
        return new AnalysisJob(copy, peers, previous, signature);
    }

    private static int IndexOf(SeriesInfo series, SeriesEpisode episode)
    {
        for (var i = 0; i < series.Episodes.Count; i++)
        {
            if (ReferenceEquals(series.Episodes[i], episode))
            {
                return i;
            }
        }

        return 0;
    }
}

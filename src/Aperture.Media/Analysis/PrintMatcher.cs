using System.Numerics;

namespace Aperture.Media.Analysis;

/// <summary>A stretch where two prints carry the same sound, at a fixed offset.</summary>
/// <param name="StartA">First matching frame in the first print.</param>
/// <param name="StartB">First matching frame in the second print.</param>
/// <param name="Length">Frames.</param>
/// <param name="ErrorRate">Fraction of differing bits across the stretch (0 identical, about 0.5 unrelated).</param>
public readonly record struct PrintMatch(int StartA, int StartB, int Length, double ErrorRate)
{
    public int EndA => StartA + Length;

    public int EndB => StartB + Length;
}

/// <summary>How strict a search is.</summary>
/// <param name="MinFrames">Shortest stretch reported.</param>
/// <param name="WindowFrames">Frames averaged when judging whether a frame matches.</param>
/// <param name="MaxErrorRate">Highest averaged bit error rate that still counts as the same sound.</param>
/// <param name="MaxGapFrames">Longest run of non-matching frames bridged inside a stretch.</param>
public sealed record MatchOptions(int MinFrames, int WindowFrames, double MaxErrorRate, int MaxGapFrames)
{
    /// <summary>Settings for long shared sequences (intros, credit themes).</summary>
    public static MatchOptions Sequence(TimeSpan minimum) =>
        new(AudioPrint.Frames(minimum), AudioPrint.Frames(TimeSpan.FromSeconds(1.5)), 0.30, AudioPrint.Frames(TimeSpan.FromSeconds(2)));

    /// <summary>Settings for short clips (a recap's excerpts of earlier episodes).</summary>
    public static MatchOptions Clips(TimeSpan minimum) =>
        new(AudioPrint.Frames(minimum), AudioPrint.Frames(TimeSpan.FromSeconds(0.75)), 0.28, AudioPrint.Frames(TimeSpan.FromSeconds(0.3)));
}

/// <summary>
/// Finds where two fingerprints contain the same sound. Every relative offset is tried, so
/// a sequence is found wherever it sits in each file. A frame counts as matching when the bit
/// error rate around it is low; unrelated sound differs in about half its bits, so the
/// thresholds used here are many standard deviations from chance. Quiet frames carry no
/// information and never count as matching, so two silences are never mistaken for the same
/// sound.
/// </summary>
public static class PrintMatcher
{
    private const int Bits = 32;

    /// <summary>A quiet frame counts as this many differing bits: no evidence either way.</summary>
    private const int UnknownErrors = Bits / 2;

    /// <summary>Frames at the edge of a stretch with more differing bits than this are trimmed off.</summary>
    private const int EdgeErrors = 11;

    /// <summary>
    /// Every stretch, of at least <see cref="MatchOptions.MinFrames"/>, where frames
    /// [<paramref name="aFrom"/>, <paramref name="aTo"/>) of <paramref name="a"/> match frames
    /// [<paramref name="bFrom"/>, <paramref name="bTo"/>) of <paramref name="b"/>.
    /// </summary>
    public static List<PrintMatch> FindMatches(AudioPrint a, int aFrom, int aTo, AudioPrint b, int bFrom, int bTo, MatchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        ArgumentNullException.ThrowIfNull(options);
        aFrom = Math.Clamp(aFrom, 0, a.Count);
        aTo = Math.Clamp(aTo, aFrom, a.Count);
        bFrom = Math.Clamp(bFrom, 0, b.Count);
        bTo = Math.Clamp(bTo, bFrom, b.Count);

        var results = new List<PrintMatch>();
        var longest = Math.Min(aTo - aFrom, bTo - bFrom);
        if (longest < options.MinFrames)
        {
            return results;
        }

        var errors = new byte[aTo - aFrom];
        var prefix = new int[errors.Length + 1];
        var good = new bool[errors.Length];
        var threshold = options.MaxErrorRate * Bits;
        var half = Math.Max(1, options.WindowFrames / 2);
        var hashesA = a.Hashes;
        var hashesB = b.Hashes;
        var quietA = a.Quiet;
        var quietB = b.Quiet;

        // Offset = position in B minus position in A.
        for (var offset = bFrom - (aTo - 1); offset <= (bTo - 1) - aFrom; offset++)
        {
            if (((offset - bFrom) & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var lo = Math.Max(aFrom, bFrom - offset);
            var hi = Math.Min(aTo, bTo - offset);
            var length = hi - lo;
            if (length < options.MinFrames)
            {
                continue;
            }

            // A cheap first look: unless enough sampled frames are close, nothing here can match.
            if (!WorthAScan(hashesA, quietA, hashesB, quietB, lo, hi, offset, options.MinFrames))
            {
                continue;
            }

            prefix[0] = 0;
            for (var i = 0; i < length; i++)
            {
                var ia = lo + i;
                var ib = ia + offset;
                var e = quietA[ia] || quietB[ib] ? UnknownErrors : BitOperations.PopCount(hashesA[ia] ^ hashesB[ib]);
                errors[i] = (byte)e;
                prefix[i + 1] = prefix[i] + e;
            }

            for (var i = 0; i < length; i++)
            {
                var from = Math.Max(0, i - half);
                var to = Math.Min(length, i + half + 1);
                good[i] = (prefix[to] - prefix[from]) <= threshold * (to - from);
            }

            CollectRuns(errors, prefix, good, length, lo, offset, options, results);
        }

        return results;
    }

    /// <summary>The longest stretch in <paramref name="matches"/>, or null.</summary>
    public static PrintMatch? Longest(IEnumerable<PrintMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        PrintMatch? best = null;
        foreach (var match in matches)
        {
            if (best is not { } current || match.Length > current.Length || (match.Length == current.Length && match.ErrorRate < current.ErrorRate))
            {
                best = match;
            }
        }

        return best;
    }

    private static bool WorthAScan(uint[] hashesA, bool[] quietA, uint[] hashesB, bool[] quietB, int lo, int hi, int offset, int minFrames)
    {
        // Sample every fourth frame; a real match has most samples within 10 bits, chance almost none.
        var step = 4;
        var needed = Math.Max(2, minFrames / step / 3);
        var close = 0;
        var run = 0;
        for (var ia = lo; ia < hi; ia += step)
        {
            var ib = ia + offset;
            if (!quietA[ia] && !quietB[ib] && BitOperations.PopCount(hashesA[ia] ^ hashesB[ib]) <= 10)
            {
                close++;
                run = 0;
                if (close >= needed)
                {
                    return true;
                }
            }
            else if (++run * step > minFrames)
            {
                // A long gap resets the count: the samples must cluster, as a real stretch does.
                close = 0;
                run = 0;
            }
        }

        return false;
    }

    private static void CollectRuns(byte[] errors, int[] prefix, bool[] good, int length, int lo, int offset, MatchOptions options, List<PrintMatch> results)
    {
        var i = 0;
        while (i < length)
        {
            if (!good[i])
            {
                i++;
                continue;
            }

            var start = i;
            var end = i + 1;
            var gap = 0;
            for (var j = i + 1; j < length; j++)
            {
                if (good[j])
                {
                    end = j + 1;
                    gap = 0;
                }
                else if (++gap > options.MaxGapFrames)
                {
                    break;
                }
            }

            i = end + 1;

            // The averaging window widens a stretch a little at each end; trim frames that clearly differ.
            while (start < end && errors[start] > EdgeErrors)
            {
                start++;
            }

            while (end > start && errors[end - 1] > EdgeErrors)
            {
                end--;
            }

            if (end - start >= options.MinFrames)
            {
                var rate = (prefix[end] - prefix[start]) / (double)(Bits * (end - start));
                results.Add(new PrintMatch(lo + start, lo + start + offset, end - start, rate));
            }
        }
    }
}

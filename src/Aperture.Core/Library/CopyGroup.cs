namespace Aperture.Core.Library;

/// <summary>
/// One film, episode or video as the library shows it: the best copy, plus any other copies
/// of the same thing (another quality, or an identical duplicate elsewhere).
/// </summary>
/// <param name="Best">The copy Aperture plays by default.</param>
/// <param name="Others">Other copies, best first.</param>
public sealed record CopyGroup(LibraryItem Best, IReadOnlyList<LibraryItem> Others)
{
    /// <summary>Every copy, best first.</summary>
    public IEnumerable<LibraryItem> All => Others.Prepend(Best);

    /// <summary>True when this item exists more than once.</summary>
    public bool HasCopies => Others.Count > 0;

    /// <summary>True when <paramref name="copy"/> has exactly the same contents as <see cref="Best"/>.</summary>
    public bool IsIdenticalToBest(LibraryItem copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        return copy.Fingerprint is not null && copy.Fingerprint == Best.Fingerprint && copy.SizeBytes == Best.SizeBytes;
    }

    /// <summary>"Same file" for identical copies; otherwise the copy's quality ("720p · 1.2 GB").</summary>
    public string Describe(LibraryItem copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        if (!ReferenceEquals(copy, Best) && IsIdenticalToBest(copy))
        {
            return "Identical copy · " + FileSize.Label(copy.SizeBytes);
        }

        return copy.Quality is { } quality ? $"{quality} · {FileSize.Label(copy.SizeBytes)}" : FileSize.Label(copy.SizeBytes);
    }

    /// <summary>
    /// Groups items that are the same film or episode (by title, year, season, episode) or the
    /// same file contents (by fingerprint). Within a group the best copy has the largest
    /// picture, then the higher bitrate, then the larger file.
    /// </summary>
    public static IReadOnlyList<CopyGroup> Build(IEnumerable<LibraryItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();

        // Union-find over two keys, so a film copied under two different names still
        // groups with itself when its contents are identical.
        var parent = Enumerable.Range(0, list.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        void Join(Dictionary<string, int> seen, string? key, int index)
        {
            if (key is null)
            {
                return;
            }

            if (seen.TryGetValue(key, out var other))
            {
                parent[Find(index)] = Find(other);
            }
            else
            {
                seen[key] = index;
            }
        }

        var byContent = new Dictionary<string, int>(StringComparer.Ordinal);
        var byFingerprint = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
        {
            Join(byContent, list[i].ContentKey, i);
            Join(byFingerprint, list[i].Fingerprint is { } f ? $"{f}|{list[i].SizeBytes}" : null, i);
        }

        return Enumerable.Range(0, list.Count)
            .GroupBy(Find)
            .Select(g =>
            {
                var ordered = g.Select(i => list[i]).OrderByDescending(Rank).ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
                return new CopyGroup(ordered[0], ordered.Skip(1).ToList());
            })
            .ToList();
    }

    private static (int Quality, double Bitrate, long Size) Rank(LibraryItem item) =>
        (MediaQuality.Rank(item.Width, item.Height), Math.Round(item.Bitrate / 100_000), item.SizeBytes);
}

/// <summary>File sizes as people read them.</summary>
public static class FileSize
{
    /// <summary>"820 KB", "1.4 GB".</summary>
    public static string Label(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0" : "0.#";
        return value.ToString(format, System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit];
    }
}

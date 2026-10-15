using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Aperture.Core.Library;

namespace Aperture.Media;

/// <summary>Finds online details for a library item.</summary>
public interface IMetadataProvider
{
    /// <summary>The service's name as shown to people.</summary>
    string Name { get; }

    /// <summary>The best match for the item, or null when the service found nothing.</summary>
    Task<OnlineMatch?> FindAsync(LibraryItem item, CancellationToken cancellationToken);
}

/// <summary>The service refused the key the user entered.</summary>
public sealed class MetadataKeyRejectedException(string message) : Exception(message);

/// <summary>
/// The Movie Database (TMDB) API, v3: https://developer.themoviedb.org/docs. Used only when
/// the user turns on online details and enters their own API key (Aperture ships none).
/// Only a title, and a year, season and episode number when known, are sent. Results are
/// scored against the file name; weak matches come back marked uncertain so the library
/// can offer them instead of applying them.
/// </summary>
public sealed class TmdbMetadataProvider(HttpClient http, string apiKey) : IMetadataProvider
{
    /// <summary>The documented API root.</summary>
    public static readonly Uri ApiRoot = new("https://api.themoviedb.org/3/");

    /// <summary>The documented image root; w780 is the smallest backdrop size that stays sharp on a 320 px card at 200 % scale.</summary>
    public const string ImageRoot = "https://image.tmdb.org/t/p/w780";

    public string Name => "TMDB";

    public async Task<OnlineMatch?> FindAsync(LibraryItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Category switch
        {
            MediaCategory.Movie => await FindMovieAsync(item, cancellationToken).ConfigureAwait(false),
            MediaCategory.Episode => await FindEpisodeAsync(item, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    private async Task<OnlineMatch?> FindMovieAsync(LibraryItem item, CancellationToken cancellationToken)
    {
        var query = $"search/movie?query={Uri.EscapeDataString(item.Title)}&include_adult=false"
            + (item.Year is { } year ? $"&year={year.ToString(CultureInfo.InvariantCulture)}" : string.Empty);
        using var document = await GetAsync(query, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var candidates = Results(document, "title", "release_date");
        if (candidates.Count == 0 && item.Year is not null)
        {
            // Files are often a year off (festival release, regional date); try without it.
            using var retry = await GetAsync($"search/movie?query={Uri.EscapeDataString(item.Title)}&include_adult=false", cancellationToken).ConfigureAwait(false);
            candidates = retry is null ? [] : Results(retry, "title", "release_date");
        }

        var best = Pick(item, candidates);
        return best is null
            ? null
            : new OnlineMatch(Name, $"movie/{best.Id}", best.Title, best.Year, null, best.Overview, Image(best.Backdrop), best.Confidence);
    }

    private async Task<OnlineMatch?> FindEpisodeAsync(LibraryItem item, CancellationToken cancellationToken)
    {
        var query = $"search/tv?query={Uri.EscapeDataString(item.Title)}&include_adult=false"
            + (item.Year is { } year ? $"&first_air_date_year={year.ToString(CultureInfo.InvariantCulture)}" : string.Empty);
        using var document = await GetAsync(query, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var show = Pick(item, Results(document, "name", "first_air_date"));
        if (show is null)
        {
            return null;
        }

        string? episodeTitle = null;
        string? overview = show.Overview;
        string? still = null;
        if (item.Season is { } season && item.Episode is { } episode)
        {
            using var details = await GetAsync(
                string.Create(CultureInfo.InvariantCulture, $"tv/{show.Id}/season/{season}/episode/{episode}"),
                cancellationToken).ConfigureAwait(false);
            if (details is not null)
            {
                var root = details.RootElement;
                episodeTitle = Text(root, "name");
                overview = Text(root, "overview") ?? overview;
                still = Text(root, "still_path");
            }
        }

        return new OnlineMatch(Name, $"tv/{show.Id}", show.Title, show.Year, episodeTitle, overview, Image(still ?? show.Backdrop), show.Confidence);
    }

    // ===== Scoring =====

    internal sealed record Candidate(long Id, string Title, int? Year, string? Overview, string? Backdrop, double Popularity)
    {
        public MatchConfidence Confidence { get; init; }
    }

    /// <summary>
    /// Certain: same title and same year. Likely: same title with no year to check against
    /// and no rival of the same title, or a very close title in the same year. Anything else
    /// comes back uncertain.
    /// </summary>
    internal static Candidate? Pick(LibraryItem item, IReadOnlyList<Candidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        var wanted = LibraryItem.Normalize(item.Title);
        var scored = candidates
            .Select(c =>
            {
                var exact = LibraryItem.Normalize(c.Title) == wanted;
                var similarity = exact ? 1.0 : Similarity(wanted, LibraryItem.Normalize(c.Title));
                var sameYear = item.Year is { } y && c.Year == y;
                var nearYear = item.Year is { } y2 && c.Year is { } cy && Math.Abs(cy - y2) == 1;
                var score = similarity + (sameYear ? 0.5 : nearYear ? 0.2 : 0) + Math.Min(c.Popularity, 100) / 1000;
                return (Candidate: c, Exact: exact, Similarity: similarity, SameYear: sameYear, NearYear: nearYear, Score: score);
            })
            .OrderByDescending(s => s.Score)
            .ToList();

        var best = scored[0];
        var exactRivals = scored.Count(s => s.Exact) - (best.Exact ? 1 : 0);
        var confidence = best switch
        {
            { Exact: true, SameYear: true } => MatchConfidence.Certain,
            { Exact: true, NearYear: true } => MatchConfidence.Likely,
            { Exact: true } when item.Year is null && exactRivals == 0 => MatchConfidence.Likely,
            { SameYear: true, Similarity: >= 0.85 } => MatchConfidence.Likely,
            _ => MatchConfidence.Uncertain,
        };
        return best.Candidate with { Confidence = confidence };
    }

    /// <summary>Similarity of two normalised titles, 0 to 1 (Sørensen–Dice over letter pairs).</summary>
    internal static double Similarity(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2)
        {
            return a == b ? 1 : 0;
        }

        var pairs = new Dictionary<int, int>();
        for (var i = 0; i < a.Length - 1; i++)
        {
            var key = (a[i] << 16) | a[i + 1];
            pairs[key] = pairs.GetValueOrDefault(key) + 1;
        }

        var shared = 0;
        for (var i = 0; i < b.Length - 1; i++)
        {
            var key = (b[i] << 16) | b[i + 1];
            if (pairs.TryGetValue(key, out var count) && count > 0)
            {
                pairs[key] = count - 1;
                shared++;
            }
        }

        return 2.0 * shared / (a.Length + b.Length - 2);
    }

    // ===== HTTP and JSON =====

    private async Task<JsonDocument?> GetAsync(string relative, CancellationToken cancellationToken)
    {
        // A v4 read access token is a long JWT; anything else is a v3 API key.
        var bearer = apiKey.Length > 60 && apiKey.Contains('.', StringComparison.Ordinal);
        var separator = relative.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var uri = new Uri(ApiRoot, bearer ? relative : $"{relative}{separator}api_key={Uri.EscapeDataString(apiKey)}");
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (bearer)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized:
                    throw new MetadataKeyRejectedException("TMDB refused the API key. Check it in Settings.");
                case HttpStatusCode.NotFound:
                    return null;
                case HttpStatusCode.TooManyRequests when attempt < 3:
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 << attempt);
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    continue;
            }

            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static List<Candidate> Results(JsonDocument document, string titleField, string dateField)
    {
        var list = new List<Candidate>();
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var result in results.EnumerateArray().Take(10))
        {
            if (!result.TryGetProperty("id", out var id) || !id.TryGetInt64(out var value) || Text(result, titleField) is not { } title)
            {
                continue;
            }

            var date = Text(result, dateField);
            int? year = date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
            var popularity = result.TryGetProperty("popularity", out var p) && p.TryGetDouble(out var pv) ? pv : 0;
            list.Add(new Candidate(value, title, year, Text(result, "overview"), Text(result, "backdrop_path"), popularity));
        }

        return list;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    private static string? Image(string? path) => path is null ? null : ImageRoot + path;
}

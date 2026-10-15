using Aperture.Core.Library;

namespace Aperture.Media;

/// <summary>How an online lookup pass ended.</summary>
/// <param name="Applied">Items whose details and artwork were filled in.</param>
/// <param name="Suggested">Items with an uncertain match kept for the user to confirm.</param>
/// <param name="NotFound">Items the service had nothing for.</param>
/// <param name="Offline">True when the pass stopped because the service could not be reached.</param>
/// <param name="KeyRejected">True when the service refused the user's key.</param>
public sealed record LookupSummary(int Applied, int Suggested, int NotFound, bool Offline, bool KeyRejected);

/// <summary>
/// Looks up details and artwork for items that have none yet. Only runs when the user
/// turned online details on and gave a key. Confident matches are applied; uncertain ones
/// are stored as suggestions; nothing the user corrected by hand is ever changed. Without
/// a network the pass simply stops and the library carries on with file-name details.
/// </summary>
public sealed class LibraryLookup(ILibraryStore store, IMetadataProvider provider, ArtworkCache? artwork, Action<string, string> log)
{
    /// <summary>Items looked up per pass.</summary>
    public const int PassSize = 200;

    /// <summary>Pause between requests, well inside the service's published rate limit.</summary>
    public TimeSpan Spacing { get; init; } = TimeSpan.FromMilliseconds(250);

    public async Task<LookupSummary> RunAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var items = await store.GetUnmatchedAsync(PassSize, cancellationToken).ConfigureAwait(false);
        int applied = 0, suggested = 0, notFound = 0;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            progress?.Report(new ScanProgress(item.FolderId, ScanPhase.LookingUp, i, items.Count, 0));
            OnlineMatch? match;
            try
            {
                match = await provider.FindAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch (MetadataKeyRejectedException)
            {
                log("warn", $"{provider.Name} refused the API key; online details paused.");
                return new LookupSummary(applied, suggested, notFound, Offline: false, KeyRejected: true);
            }
            catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException
                or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                log("info", $"Online details stopped: {provider.Name} could not be reached.");
                return new LookupSummary(applied, suggested, notFound, Offline: true, KeyRejected: false);
            }

            if (match is null)
            {
                notFound++;
                await store.SaveOnlineMatchAsync(item.Id, null, apply: false, null, cancellationToken).ConfigureAwait(false);
            }
            else if (match.Confidence >= MatchConfidence.Likely)
            {
                string? image = null;
                if (artwork is not null && match.ArtworkUrl is { } url)
                {
                    image = await artwork.GetAsync(url, cancellationToken).ConfigureAwait(false);
                }

                await store.SaveOnlineMatchAsync(item.Id, match, apply: true, image, cancellationToken).ConfigureAwait(false);
                applied++;
            }
            else
            {
                await store.SaveOnlineMatchAsync(item.Id, match, apply: false, null, cancellationToken).ConfigureAwait(false);
                suggested++;
            }

            if (Spacing > TimeSpan.Zero && i < items.Count - 1)
            {
                await Task.Delay(Spacing, cancellationToken).ConfigureAwait(false);
            }
        }

        if (items.Count > 0)
        {
            progress?.Report(new ScanProgress(items[^1].FolderId, ScanPhase.LookingUp, items.Count, items.Count, 0));
            log("info", $"Online details: {applied} filled in, {suggested} to confirm, {notFound} not found");
        }

        return new LookupSummary(applied, suggested, notFound, Offline: false, KeyRejected: false);
    }

    /// <summary>Fetches the artwork of a suggestion the user accepted, then applies it.</summary>
    public async Task AcceptSuggestionAsync(LibraryItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.SuggestedMatch is not { } match)
        {
            return;
        }

        string? image = null;
        if (artwork is not null && match.ArtworkUrl is { } url)
        {
            image = await artwork.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }

        await store.SaveOnlineMatchAsync(item.Id, match with { Confidence = MatchConfidence.Certain }, apply: true, image, cancellationToken).ConfigureAwait(false);
    }
}

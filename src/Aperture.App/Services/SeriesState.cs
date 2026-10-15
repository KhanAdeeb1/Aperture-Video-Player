using Aperture.Core.Series;
using Aperture.Media;

namespace Aperture.App.Services;

/// <summary>
/// The library's shows as last read, shared by the player (next and previous episodes) and
/// background analysis. Rebuilt from the library whenever it changes, so episode order always
/// reflects the latest scan.
/// </summary>
internal sealed class SeriesState(LibraryCoordinator library)
{
    private SeriesCatalog _catalog = SeriesCatalog.Empty;

    public SeriesCatalog Catalog => Volatile.Read(ref _catalog);

    /// <summary>True once the library has been read at least once.</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Reads the library again and rebuilds the shows.</summary>
    public async Task<SeriesCatalog> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await library.LoadAsync(cancellationToken).ConfigureAwait(false);
        var catalog = SeriesCatalog.Build(snapshot.Groups);
        Volatile.Write(ref _catalog, catalog);
        IsLoaded = true;
        return catalog;
    }
}

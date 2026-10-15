using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Aperture.App.Services;

/// <summary>
/// Artwork for cards: a library item's downloaded artwork when there is one, otherwise the
/// Windows thumbnail (a frame Windows extracts from the video). Nothing is written next to
/// the user's media. Files without either return null and the card shows its designed
/// empty artwork instead, so missing artwork never changes the layout.
/// </summary>
internal sealed class ThumbnailCache
{
    private const uint RequestedSize = 640;
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ImageSource?> GetAsync(string path, string? artworkPath = null)
    {
        var key = artworkPath ?? path;
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        ImageSource? image = null;
        try
        {
            // Files are read off the interface thread. Windows.Storage calls made on it wait in a
            // COM modal loop that lets XAML's timers run re-entrantly, which WinUI ends the
            // process for (a fail-fast inside Microsoft.UI.Xaml.dll, seen in the visual review).
            if (artworkPath is not null && File.Exists(artworkPath))
            {
                var bytes = await Task.Run(() => File.ReadAllBytesAsync(artworkPath));
                using var stream = new MemoryStream(bytes).AsRandomAccessStream();
                var bitmap = new BitmapImage { DecodePixelWidth = (int)RequestedSize };
                await bitmap.SetSourceAsync(stream);
                image = bitmap;
            }
            else if (File.Exists(path))
            {
                using var thumbnail = await Task.Run(async () =>
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    return await file.GetThumbnailAsync(ThumbnailMode.VideosView, RequestedSize, ThumbnailOptions.ResizeThumbnail);
                });
                if (thumbnail is not null && thumbnail.Type == ThumbnailType.Image)
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(thumbnail);
                    image = bitmap;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warning("Could not load a thumbnail", error);
        }

        _cache[key] = image;
        return image;
    }

    /// <summary>Drops a cached thumbnail so the next request asks Windows again.</summary>
    public void Forget(string path) => _cache.Remove(path);

    /// <summary>Drops every cached thumbnail.</summary>
    public void Clear() => _cache.Clear();
}

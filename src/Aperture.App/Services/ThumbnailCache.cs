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
            if (artworkPath is not null && File.Exists(artworkPath))
            {
                var file = await StorageFile.GetFileFromPathAsync(artworkPath);
                using var stream = await file.OpenReadAsync();
                var bitmap = new BitmapImage { DecodePixelWidth = (int)RequestedSize };
                await bitmap.SetSourceAsync(stream);
                image = bitmap;
            }
            else if (File.Exists(path))
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, RequestedSize, ThumbnailOptions.ResizeThumbnail);
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

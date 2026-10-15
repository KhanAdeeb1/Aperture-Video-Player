using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Aperture.App.Services;

/// <summary>
/// Artwork for played files, taken from the Windows thumbnail cache (a frame Windows
/// extracts from the video). Nothing is written next to the user's media. Files without a
/// real thumbnail return null and the card shows its designed empty artwork instead.
/// </summary>
internal sealed class ThumbnailCache
{
    private const uint RequestedSize = 640;
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ImageSource?> GetAsync(string path)
    {
        if (_cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        ImageSource? image = null;
        try
        {
            if (File.Exists(path))
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

        _cache[path] = image;
        return image;
    }

    /// <summary>Drops a cached thumbnail so the next request asks Windows again.</summary>
    public void Forget(string path) => _cache.Remove(path);

    /// <summary>Drops every cached thumbnail.</summary>
    public void Clear() => _cache.Clear();
}

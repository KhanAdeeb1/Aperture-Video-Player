using System.Globalization;
using Aperture.Core.Library;
using Microsoft.Extensions.Logging;

namespace Aperture.Server.Media;

/// <summary>
/// Pictures for cards: the artwork Aperture already downloaded for the item (when online details
/// are on), otherwise one frame taken with FFmpeg and kept in the server's Thumbnails folder.
/// </summary>
public sealed class Thumbnails(string directory, string artworkDirectory, FfmpegTools ffmpeg, ILogger<Thumbnails> log) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(2, 2);

    public void Dispose() => _gate.Dispose();

    /// <summary>A JPEG or PNG for the item, or null when there is none and none can be made.</summary>
    public async Task<string?> GetAsync(LibraryItem item, string? ffmpegSetting, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.ArtworkPath is { } art && IsInside(art, artworkDirectory) && File.Exists(art))
        {
            return art;
        }

        var name = string.Create(CultureInfo.InvariantCulture, $"{item.Id}-{item.SizeBytes}-{item.ModifiedUtc.ToUnixTimeSeconds()}.jpg");
        var path = Path.Combine(directory, name);
        if (File.Exists(path))
        {
            return path;
        }

        var failed = path + ".none";
        if (File.Exists(failed))
        {
            return null;
        }

        var (install, _) = await ffmpeg.FindAsync(ffmpegSetting, cancellationToken).ConfigureAwait(false);
        if (install is null)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(path))
            {
                return path;
            }

            Directory.CreateDirectory(directory);
            var at = item.Duration is { } d && d > TimeSpan.Zero ? Math.Min(d.TotalSeconds * 0.1, 300) : 10;
            var temp = path + ".tmp.jpg";
            var result = await FfmpegTools.RunAsync(
                install.FfmpegPath,
                [
                    "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                    "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", "file:" + item.Path,
                    "-frames:v", "1", "-vf", "scale=480:-2:flags=bicubic", "-q:v", "4", temp,
                ],
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode == 0 && File.Exists(temp))
            {
                File.Move(temp, path, overwrite: true);
                return path;
            }

            TryDelete(temp);
            await File.WriteAllTextAsync(failed, string.Empty, cancellationToken).ConfigureAwait(false);
            log.LogInformation("No picture could be taken from item {Item}", item.Id);
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsInside(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Left for the next attempt to overwrite.
        }
    }
}

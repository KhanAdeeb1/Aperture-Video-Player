using System.Security.Cryptography;
using System.Text;

namespace Aperture.Media;

/// <summary>
/// Downloads artwork into Aperture's own data folder (never next to the user's media).
/// Only images are kept, each is capped in size, and a file is written under a temporary
/// name and moved into place so a cancelled download never leaves a broken image.
/// </summary>
public sealed class ArtworkCache(HttpClient http, string folder)
{
    /// <summary>Largest image kept; a w780 backdrop is usually 60–200 KB.</summary>
    public const long MaxBytes = 4L * 1024 * 1024;

    /// <summary>Where artwork is kept.</summary>
    public string Folder { get; } = folder;

    /// <summary>The local file for <paramref name="url"/>, downloading it when needed. Null when it could not be fetched.</summary>
    public async Task<string?> GetAsync(string url, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var target = PathFor(url);
        if (File.Exists(target))
        {
            return target;
        }

        Directory.CreateDirectory(Folder);
        var temporary = target + ".part";
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentType?.MediaType is not { } type
                || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxBytes)
                    {
                        throw new InvalidDataException("Artwork too large.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            File.Move(temporary, target, overwrite: true);
            return target;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>The cache file name for a URL: a hash, so nothing from the service ends up in a path.</summary>
    public string PathFor(string url)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];
        return Path.Combine(Folder, hash + ".jpg");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

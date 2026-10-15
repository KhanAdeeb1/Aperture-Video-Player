using System.Security.Cryptography;

namespace Aperture.Media;

/// <summary>
/// A cheap identity for a file's contents: its size plus a hash of its first and last 64 KB.
/// Enough to tell identical copies apart from different files without reading whole videos.
/// The file is opened read-only and shared, so other programs are not blocked.
/// </summary>
public static class FileFingerprint
{
    /// <summary>How much is read from each end.</summary>
    public const int SampleSize = 64 * 1024;

    /// <summary>Returns the fingerprint, or null when the file cannot be read.</summary>
    public static string? Compute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, SampleSize, FileOptions.SequentialScan);
            var length = stream.Length;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hash.AppendData(BitConverter.GetBytes(length));

            var buffer = new byte[SampleSize];
            hash.AppendData(buffer, 0, ReadUpTo(stream, buffer));
            if (length > SampleSize)
            {
                stream.Seek(Math.Max(SampleSize, length - SampleSize), SeekOrigin.Begin);
                hash.AppendData(buffer, 0, ReadUpTo(stream, buffer));
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset())[..32];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static int ReadUpTo(Stream stream, byte[] buffer)
    {
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        return total;
    }
}

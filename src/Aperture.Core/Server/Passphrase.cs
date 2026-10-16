using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aperture.Core.Server;

/// <summary>
/// The media server's passphrase. Only a salted PBKDF2-SHA256 hash is stored
/// (<c>pbkdf2-sha256$iterations$salt$hash</c>, base64), and checks take constant time.
/// </summary>
public static class Passphrase
{
    /// <summary>Shortest passphrase accepted.</summary>
    public const int MinimumLength = 10;

    /// <summary>Longest passphrase accepted, so a request cannot make hashing arbitrarily slow.</summary>
    public const int MaximumLength = 256;

    /// <summary>PBKDF2 iterations for new hashes (OWASP's 2023 figure for SHA-256).</summary>
    public const int Iterations = 600_000;

    private const string Scheme = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>Null when the passphrase can be used, otherwise a sentence saying why not.</summary>
    public static string? Check(string? passphrase)
    {
        if (string.IsNullOrEmpty(passphrase) || passphrase.Trim().Length < MinimumLength)
        {
            return $"Use at least {MinimumLength} characters. A few unrelated words are easy to type on a phone and hard to guess.";
        }

        if (passphrase.Length > MaximumLength)
        {
            return $"Use at most {MaximumLength} characters.";
        }

        if (passphrase.Distinct().Count() < 4)
        {
            return "That passphrase repeats the same few characters. Choose something harder to guess.";
        }

        return null;
    }

    /// <summary>Hashes a passphrase with a new random salt.</summary>
    public static string Hash(string passphrase, int iterations = Iterations)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return string.Join('$', Scheme, iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>True when <paramref name="passphrase"/> matches <paramref name="stored"/>. A malformed stored value never matches.</summary>
    public static bool Verify(string? passphrase, string? stored)
    {
        if (passphrase is null || stored is null || passphrase.Length > MaximumLength)
        {
            return false;
        }

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < 1)
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

/// <summary>Sign-in tokens handed to devices. The device keeps the token; the database keeps only its SHA-256.</summary>
public static class DeviceToken
{
    /// <summary>How long a sign-in lasts without being used.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>A new random token (256 bits, base64url).</summary>
    public static string Create() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The value stored for <paramref name="token"/>.</summary>
    public static string HashOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    /// <summary>True when the text has the shape of a token, checked before any database lookup.</summary>
    public static bool LooksValid(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

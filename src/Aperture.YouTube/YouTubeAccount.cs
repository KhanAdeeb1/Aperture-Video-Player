using Aperture.Core.Settings;

namespace Aperture.YouTube;

/// <summary>
/// The user's optional Google sign-in for YouTube. The OAuth client and the refresh token live
/// in the operating system's credential store (<see cref="ISecretStore"/>), never in Aperture's
/// database or log. The access token is kept only in memory.
/// </summary>
public sealed class YouTubeAccount(ISecretStore secrets, GoogleOAuth oauth, TimeProvider clock) : IDisposable
{
    public const string ClientIdName = "YouTubeClientId";
    public const string ClientSecretName = "YouTubeClientSecret";
    public const string RefreshTokenName = "YouTubeRefreshToken";

    /// <summary>How long the browser sign-in may take before Aperture stops waiting.</summary>
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private GoogleTokens? _tokens;

    /// <summary>Raised when the sign-in starts or stops working (signed in, signed out, or expired).</summary>
    public event EventHandler? Changed;

    /// <summary>The OAuth client the user set up, or null.</summary>
    public GoogleOAuthClient? Client =>
        secrets.Read(ClientIdName) is { Length: > 0 } id ? new GoogleOAuthClient(id, secrets.Read(ClientSecretName)) : null;

    public bool IsSignedIn => secrets.Read(RefreshTokenName) is { Length: > 0 };

    /// <summary>Stores the OAuth client (id and secret from Google Cloud Console). Changing it signs out.</summary>
    public void SetClient(string? clientId, string? clientSecret)
    {
        if (Client?.ClientId != clientId?.Trim())
        {
            ForgetTokens();
        }

        secrets.Write(ClientIdName, clientId?.Trim());
        secrets.Write(ClientSecretName, clientSecret?.Trim());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens Google's sign-in page with <paramref name="openBrowser"/> and waits for the answer on
    /// a loopback listener. Throws <see cref="YouTubeApiException"/> when it fails or is cancelled.
    /// </summary>
    public async Task SignInAsync(Func<Uri, Task<bool>> openBrowser, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        var client = Client ?? throw new YouTubeApiException(YouTubeProblem.KeyRejected, "No Google OAuth client is set up.");
        using var receiver = new LoopbackReceiver();
        var (verifier, challenge) = GoogleOAuth.CreatePkce();
        var state = GoogleOAuth.CreateState();
        if (!await openBrowser(GoogleOAuth.AuthorizationUri(client, receiver.RedirectUri, challenge, state)).ConfigureAwait(false))
        {
            throw new YouTubeApiException(YouTubeProblem.Other, "The browser couldn't be opened for sign-in.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        LoopbackAnswer answer;
        try
        {
            answer = await receiver.WaitAsync(state, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new YouTubeApiException(YouTubeProblem.Other, "Sign-in wasn't finished in the browser within five minutes.");
        }

        if (answer.Code is not { } code)
        {
            throw new YouTubeApiException(
                YouTubeProblem.Other,
                answer.Error == "access_denied" ? "Sign-in was cancelled in the browser." : $"Google didn't complete the sign-in ({answer.Error}).");
        }

        var tokens = await oauth.ExchangeCodeAsync(client, code, verifier, receiver.RedirectUri, cancellationToken).ConfigureAwait(false);
        if (tokens.RefreshToken is not { Length: > 0 } refresh)
        {
            throw new YouTubeApiException(YouTubeProblem.Other, "Google didn't return a lasting sign-in. Try signing in again.");
        }

        secrets.Write(RefreshTokenName, refresh);
        _tokens = tokens;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A valid access token, refreshed when needed; null when not signed in.</summary>
    public async Task<string?> AccessTokenAsync(CancellationToken cancellationToken)
    {
        if (secrets.Read(RefreshTokenName) is not { Length: > 0 } refresh || Client is not { } client)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_tokens is { } current && current.ExpiresUtc - clock.GetUtcNow() > TimeSpan.FromMinutes(1))
            {
                return current.AccessToken;
            }

            try
            {
                _tokens = await oauth.RefreshAsync(client, refresh, cancellationToken).ConfigureAwait(false);
            }
            catch (YouTubeApiException error) when (error.Problem == YouTubeProblem.SignInExpired)
            {
                // Revoked, or expired: forget it so the interface shows "Sign in" again.
                ForgetTokens();
                Changed?.Invoke(this, EventArgs.Empty);
                throw;
            }

            return _tokens.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Revokes the grant with Google and forgets it here. Returns false if Google couldn't be reached (it is still forgotten here).</summary>
    public async Task<bool> SignOutAsync(CancellationToken cancellationToken)
    {
        var refresh = secrets.Read(RefreshTokenName);
        ForgetTokens();
        Changed?.Invoke(this, EventArgs.Empty);
        return refresh is not { Length: > 0 } || await oauth.RevokeAsync(refresh, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();

    private void ForgetTokens()
    {
        _tokens = null;
        secrets.Write(RefreshTokenName, null);
    }
}

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aperture.YouTube;

/// <summary>Tokens from Google's token endpoint.</summary>
/// <param name="AccessToken">Short-lived token sent with API requests.</param>
/// <param name="RefreshToken">Long-lived token for new access tokens (only on the first exchange).</param>
/// <param name="ExpiresUtc">When the access token stops working.</param>
public sealed record GoogleTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresUtc);

/// <summary>The OAuth client the user created in Google Cloud Console (type "Desktop app").</summary>
/// <param name="ClientId">The client id.</param>
/// <param name="ClientSecret">Google issues one for desktop clients and expects it in token requests; it isn't confidential in an installed app, but it is still kept out of the database and log.</param>
public sealed record GoogleOAuthClient(string ClientId, string? ClientSecret);

/// <summary>
/// OAuth 2.0 for installed apps (https://developers.google.com/identity/protocols/oauth2/native-app):
/// the system browser shows Google's own sign-in page, the answer comes back to a one-time
/// listener on 127.0.0.1, and PKCE (S256) ties the code to this app. Aperture never sees the
/// password. Only the read-only YouTube scope is requested.
/// </summary>
public sealed class GoogleOAuth(HttpClient http, TimeProvider clock)
{
    /// <summary>Read-only access to the user's YouTube account (playlists, liked videos).</summary>
    public const string ReadOnlyScope = "https://www.googleapis.com/auth/youtube.readonly";

    public static readonly Uri AuthorizationEndpoint = new("https://accounts.google.com/o/oauth2/v2/auth");
    public static readonly Uri TokenEndpoint = new("https://oauth2.googleapis.com/token");
    public static readonly Uri RevocationEndpoint = new("https://oauth2.googleapis.com/revoke");

    /// <summary>A PKCE verifier (43 to 128 URL-safe characters) and its S256 challenge.</summary>
    public static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        return (verifier, Challenge(verifier));
    }

    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>A random value echoed back by Google, so an answer meant for another request is refused.</summary>
    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    /// <summary>The Google page the browser opens.</summary>
    public static Uri AuthorizationUri(GoogleOAuthClient client, Uri redirect, string challenge, string state)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(redirect);
        var query = string.Join('&', new[]
        {
            ("client_id", client.ClientId),
            ("redirect_uri", redirect.ToString()),
            ("response_type", "code"),
            ("scope", ReadOnlyScope),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("state", state),
            ("access_type", "offline"),
            ("prompt", "consent"),
        }.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));
        return new Uri($"{AuthorizationEndpoint}?{query}");
    }

    /// <summary>Exchanges the code from the browser for tokens.</summary>
    public Task<GoogleTokens> ExchangeCodeAsync(GoogleOAuthClient client, string code, string verifier, Uri redirect, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(redirect);
        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", client.ClientId),
            new("code", code),
            new("code_verifier", verifier),
            new("grant_type", "authorization_code"),
            new("redirect_uri", redirect.ToString()),
        };
        AddSecret(form, client);
        return PostTokenAsync(form, null, cancellationToken);
    }

    /// <summary>A new access token from the stored refresh token.</summary>
    public Task<GoogleTokens> RefreshAsync(GoogleOAuthClient client, string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", client.ClientId),
            new("refresh_token", refreshToken),
            new("grant_type", "refresh_token"),
        };
        AddSecret(form, client);
        return PostTokenAsync(form, refreshToken, cancellationToken);
    }

    /// <summary>Tells Google to revoke the token (and the grant behind it). Returns false if Google couldn't be told.</summary>
    public async Task<bool> RevokeAsync(string token, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("token", token)]);
            using var response = await http.PostAsync(RevocationEndpoint, content, cancellationToken).ConfigureAwait(false);
            // 400 invalid_token means it was already revoked or expired: the grant is gone either way.
            return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.BadRequest;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static void AddSecret(List<KeyValuePair<string, string>> form, GoogleOAuthClient client)
    {
        if (client.ClientSecret is { Length: > 0 } secret)
        {
            form.Add(new("client_secret", secret));
        }
    }

    private async Task<GoogleTokens> PostTokenAsync(List<KeyValuePair<string, string>> form, string? existingRefresh, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            response = await http.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new YouTubeApiException(YouTubeProblem.Offline, "Google's sign-in service could not be reached.", error);
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new YouTubeApiException(YouTubeProblem.Offline, "Google's sign-in service did not answer in time.", error);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException error)
            {
                throw new YouTubeApiException(YouTubeProblem.Other, $"Google's sign-in service answered {(int)response.StatusCode} with an unreadable response.", error);
            }

            using (document)
            {
                var root = document.RootElement;
                string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (!response.IsSuccessStatusCode || Text("access_token") is not { } access)
                {
                    // invalid_grant: the refresh token was revoked, expired (7 days for apps in "Testing"), or the code was reused.
                    var error = Text("error") ?? "unknown_error";
                    var problem = error is "invalid_grant" ? YouTubeProblem.SignInExpired : error is "invalid_client" or "unauthorized_client" ? YouTubeProblem.KeyRejected : YouTubeProblem.Other;
                    throw new YouTubeApiException(problem, $"Google refused the sign-in ({error}: {Text("error_description") ?? "no description"}).");
                }

                var seconds = root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var s) ? s : 3600;
                return new GoogleTokens(access, Text("refresh_token") ?? existingRefresh, clock.GetUtcNow().AddSeconds(seconds));
            }
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The answer Google's sign-in page sent back to the loopback listener.</summary>
/// <param name="Code">The authorization code, when the user agreed.</param>
/// <param name="Error">Google's error ("access_denied" when the user said no), when they didn't.</param>
public sealed record LoopbackAnswer(string? Code, string? Error);

/// <summary>
/// A one-request HTTP listener on 127.0.0.1 and a free port, for the browser's redirect back
/// to Aperture after Google sign-in. It accepts only a GET carrying the expected state, answers
/// with a short page, and stops. Nothing listens on any other interface.
/// </summary>
public sealed class LoopbackReceiver : IDisposable
{
    private readonly TcpListener _listener;

    public LoopbackReceiver()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(1);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        RedirectUri = new Uri($"http://127.0.0.1:{Port}/");
    }

    public int Port { get; }

    /// <summary>The redirect URI to register with this request ("http://127.0.0.1:port/").</summary>
    public Uri RedirectUri { get; }

    /// <summary>Waits for the browser to come back with an answer carrying <paramref name="state"/>. Requests without it are answered and ignored.</summary>
    public async Task<LoopbackAnswer> WaitAsync(string state, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (await HandleAsync(client, state, cancellationToken).ConfigureAwait(false) is { } answer)
                {
                    return answer;
                }
            }
            catch (IOException)
            {
                // The browser (or something else) dropped the connection; keep waiting for the real answer.
            }
        }
    }

    private static async Task<LoopbackAnswer?> HandleAsync(TcpClient client, string state, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        var target = await ReadRequestTargetAsync(stream, cancellationToken).ConfigureAwait(false);
        var query = ParseQuery(target);
        var matches = query.TryGetValue("state", out var echoed) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(echoed), Encoding.UTF8.GetBytes(state));
        if (!matches)
        {
            // A favicon request or a stray visit: answer it and keep waiting.
            await RespondAsync(stream, HttpStatusCode.NotFound, "This page isn’t part of the sign-in.", cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (query.TryGetValue("code", out var code) && code.Length > 0)
        {
            await TryRespondAsync(stream, "Signed in. You can close this tab and return to Aperture.", cancellationToken).ConfigureAwait(false);
            return new LoopbackAnswer(code, null);
        }

        await TryRespondAsync(stream, "Sign-in was cancelled. You can close this tab and return to Aperture.", cancellationToken).ConfigureAwait(false);
        return new LoopbackAnswer(null, query.GetValueOrDefault("error") ?? "no_code");
    }

    /// <summary>The answer is already known; a browser that went away before reading the page doesn't change it.</summary>
    private static async Task TryRespondAsync(Stream stream, string message, CancellationToken cancellationToken)
    {
        try
        {
            await RespondAsync(stream, HttpStatusCode.OK, message, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    public void Dispose() => _listener.Stop();

    /// <summary>Reads "GET /path?query HTTP/1.1" and the headers after it; returns the target.</summary>
    internal static async Task<string> ReadRequestTargetAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        var firstLine = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n", 2)[0];
        var parts = firstLine.Split(' ');
        return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : string.Empty;
    }

    internal static Dictionary<string, string> ParseQuery(string target)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var question = target.IndexOf('?', StringComparison.Ordinal);
        if (question < 0)
        {
            return values;
        }

        foreach (var pair in target[(question + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                values.TryAdd(Uri.UnescapeDataString(pair[..equals]), Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' ')));
            }
        }

        return values;
    }

    private static async Task RespondAsync(Stream stream, HttpStatusCode status, string message, CancellationToken cancellationToken)
    {
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Aperture</title></head>"
            + $"<body style=\"font-family:'Segoe UI',sans-serif;background:#141517;color:#f3f5f7;display:grid;place-items:center;height:100vh;margin:0\"><p>{WebUtility.HtmlEncode(message)}</p></body></html>";
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

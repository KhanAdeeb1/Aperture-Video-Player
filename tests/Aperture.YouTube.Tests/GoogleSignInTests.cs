using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Aperture.YouTube.Tests;

public class GoogleSignInTests
{
    // Stands in for the user's browser; never goes through a proxy, as a browser reaching 127.0.0.1 doesn't.
    private static readonly HttpClient Browser = new(new HttpClientHandler { UseProxy = false });

    private static readonly GoogleOAuthClient Client = new("client-id.apps.googleusercontent.com", "client-secret");

    [Fact]
    public void Pkce_matches_the_rfc_7636_example()
    {
        // RFC 7636, appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", GoogleOAuth.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var (verifier, challenge) = GoogleOAuth.CreatePkce();
        Assert.InRange(verifier.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", verifier);
        Assert.Equal(GoogleOAuth.Challenge(verifier), challenge);
        Assert.NotEqual(GoogleOAuth.CreateState(), GoogleOAuth.CreateState());
    }

    [Fact]
    public void Authorization_asks_only_for_read_only_youtube_access_with_pkce()
    {
        var uri = GoogleOAuth.AuthorizationUri(Client, new Uri("http://127.0.0.1:5555/"), "CHALLENGE", "STATE");
        var query = Uri.UnescapeDataString(uri.Query);
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", uri.ToString(), StringComparison.Ordinal);
        Assert.Contains("scope=https://www.googleapis.com/auth/youtube.readonly&", query, StringComparison.Ordinal);
        Assert.Contains("code_challenge=CHALLENGE", query, StringComparison.Ordinal);
        Assert.Contains("code_challenge_method=S256", query, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=http://127.0.0.1:5555/", query, StringComparison.Ordinal);
        Assert.Contains("state=STATE", query, StringComparison.Ordinal);
        Assert.DoesNotContain("client-secret", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Loopback_ignores_requests_without_the_state_and_returns_the_code()
    {
        using var receiver = new LoopbackReceiver();
        Assert.Equal("127.0.0.1", receiver.RedirectUri.Host);
        var waiting = receiver.WaitAsync("good-state", default);

        var stray = await SendRawAsync(receiver.Port, "GET /favicon.ico HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404", stray, StringComparison.Ordinal);
        var forged = await SendRawAsync(receiver.Port, "GET /?code=evil&state=other-state HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404", forged, StringComparison.Ordinal);
        Assert.False(waiting.IsCompleted);

        var good = await SendRawAsync(receiver.Port, "GET /?state=good-state&code=4%2Fabc&scope=x HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 200", good, StringComparison.Ordinal);
        Assert.Contains("return to Aperture", good, StringComparison.Ordinal);
        var answer = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("4/abc", answer.Code);
    }

    [Fact]
    public async Task Loopback_reports_a_refusal()
    {
        using var receiver = new LoopbackReceiver();
        var waiting = receiver.WaitAsync("s", default);
        await SendRawAsync(receiver.Port, "GET /?error=access_denied&state=s HTTP/1.1\r\n\r\n");
        var answer = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(answer.Code);
        Assert.Equal("access_denied", answer.Error);
    }

    [Fact]
    public async Task Sign_in_round_trip_stores_only_the_refresh_token_in_the_secret_store()
    {
        var clock = new ManualClock();
        var tokenRequests = 0;
        var http = new FakeHttp(request =>
        {
            if (request.RequestUri == GoogleOAuth.TokenEndpoint)
            {
                tokenRequests++;
                return FakeHttp.Json(tokenRequests == 1
                    ? """{ "access_token": "access-1", "expires_in": 3599, "refresh_token": "refresh-1", "scope": "https://www.googleapis.com/auth/youtube.readonly", "token_type": "Bearer" }"""
                    : """{ "access_token": "access-2", "expires_in": 3599, "token_type": "Bearer" }""");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var secrets = new MemorySecrets();
        using var account = new YouTubeAccount(secrets, new GoogleOAuth(http.Client(), clock), clock);
        account.SetClient(Client.ClientId, Client.ClientSecret);

        // The "browser": follow Google's redirect back to the loopback listener with a code.
        await account.SignInAsync(
            async uri =>
            {
                var query = LoopbackReceiver.ParseQuery(uri.PathAndQuery);
                _ = Browser.GetStringAsync(new Uri($"{query["redirect_uri"]}?state={Uri.EscapeDataString(query["state"])}&code=the-code"));
                await Task.Yield();
                return true;
            },
            default);

        Assert.True(account.IsSignedIn);
        Assert.Equal("refresh-1", secrets.Values[YouTubeAccount.RefreshTokenName]);
        Assert.DoesNotContain(secrets.Values.Values, v => v.StartsWith("access", StringComparison.Ordinal));
        var exchange = http.Requests.Single(r => r.Uri == GoogleOAuth.TokenEndpoint).Body!;
        Assert.Contains("grant_type=authorization_code", exchange, StringComparison.Ordinal);
        Assert.Contains("code=the-code", exchange, StringComparison.Ordinal);
        Assert.Contains("code_verifier=", exchange, StringComparison.Ordinal);

        Assert.Equal("access-1", await account.AccessTokenAsync(default));
        clock.Now = clock.Now.AddHours(1);
        Assert.Equal("access-2", await account.AccessTokenAsync(default)); // refreshed
        Assert.Equal("refresh-1", secrets.Values[YouTubeAccount.RefreshTokenName]); // kept when Google doesn't send a new one

        Assert.True(await account.SignOutAsync(default));
        Assert.False(account.IsSignedIn);
        Assert.Contains(http.Requests, r => r.Uri == GoogleOAuth.RevocationEndpoint && r.Body == "token=refresh-1");
    }

    [Fact]
    public async Task A_revoked_grant_signs_the_user_out()
    {
        var clock = new ManualClock();
        var http = new FakeHttp(_ => FakeHttp.Json("""{ "error": "invalid_grant", "error_description": "Token has been expired or revoked." }""", HttpStatusCode.BadRequest));
        var secrets = new MemorySecrets();
        secrets.Write(YouTubeAccount.ClientIdName, Client.ClientId);
        secrets.Write(YouTubeAccount.RefreshTokenName, "old");
        using var account = new YouTubeAccount(secrets, new GoogleOAuth(http.Client(), clock), clock);
        var changed = 0;
        account.Changed += (_, _) => changed++;

        var error = await Assert.ThrowsAsync<YouTubeApiException>(() => account.AccessTokenAsync(default));
        Assert.Equal(YouTubeProblem.SignInExpired, error.Problem);
        Assert.False(account.IsSignedIn);
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Cancelling_in_the_browser_is_reported()
    {
        var clock = new ManualClock();
        var secrets = new MemorySecrets();
        using var account = new YouTubeAccount(secrets, new GoogleOAuth(new FakeHttp(_ => throw new InvalidOperationException()).Client(), clock), clock);
        account.SetClient(Client.ClientId, null);
        var error = await Assert.ThrowsAsync<YouTubeApiException>(() => account.SignInAsync(
            uri =>
            {
                var query = LoopbackReceiver.ParseQuery(uri.PathAndQuery);
                _ = Browser.GetStringAsync(new Uri($"{query["redirect_uri"]}?state={Uri.EscapeDataString(query["state"])}&error=access_denied"));
                return Task.FromResult(true);
            },
            default));
        Assert.Contains("cancelled", error.Message, StringComparison.Ordinal);
        Assert.False(account.IsSignedIn);
    }

    [Fact]
    public void Changing_the_client_signs_out()
    {
        var clock = new ManualClock();
        var secrets = new MemorySecrets();
        using var account = new YouTubeAccount(secrets, new GoogleOAuth(new HttpClient(), clock), clock);
        account.SetClient("a", "s");
        secrets.Write(YouTubeAccount.RefreshTokenName, "r");
        account.SetClient("a", "s2");
        Assert.True(account.IsSignedIn);
        account.SetClient("b", "s2");
        Assert.False(account.IsSignedIn);
    }

    private static async Task<string> SendRawAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}

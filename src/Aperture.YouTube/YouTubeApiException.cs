namespace Aperture.YouTube;

/// <summary>Why a YouTube request failed, in terms the interface can act on.</summary>
public enum YouTubeProblem
{
    /// <summary>No connection, a timeout, or YouTube didn't answer.</summary>
    Offline,

    /// <summary>The project's daily quota is used up.</summary>
    QuotaExceeded,

    /// <summary>The API key was refused (wrong, deleted or restricted).</summary>
    KeyRejected,

    /// <summary>The YouTube Data API isn't turned on for the key's Google Cloud project.</summary>
    ApiNotEnabled,

    /// <summary>The Google sign-in has expired or was revoked.</summary>
    SignInExpired,

    /// <summary>Anything else YouTube reported.</summary>
    Other,
}

/// <summary>A YouTube request failed. <see cref="Exception.Message"/> is safe to show; it never holds a key or token.</summary>
public sealed class YouTubeApiException(YouTubeProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    public YouTubeProblem Problem { get; } = problem;

    /// <summary>One sentence for the interface.</summary>
    public string Explain() => Problem switch
    {
        YouTubeProblem.Offline => "YouTube can’t be reached. Check your connection; your own videos aren’t affected.",
        YouTubeProblem.QuotaExceeded => "Today’s YouTube search allowance for your API key is used up. It resets daily (midnight Pacific time).",
        YouTubeProblem.KeyRejected => "YouTube didn’t accept the API key. Check it in Settings › YouTube.",
        YouTubeProblem.ApiNotEnabled => "The YouTube Data API isn’t turned on for your key’s Google Cloud project. Settings › YouTube explains how.",
        YouTubeProblem.SignInExpired => "Your YouTube sign-in has expired. Sign in again in Settings › YouTube.",
        _ => "YouTube couldn’t complete the request. Details are in Aperture’s log.",
    };
}

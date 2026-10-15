using Aperture.Core.YouTube;

namespace Aperture.Core.Tests.YouTube;

public class YouTubeRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private static YouTubeVideoStatus Status(bool embeddable = true, string privacy = "public", bool age = false, string[]? allowed = null, string[]? blocked = null, YouTubeLiveState live = YouTubeLiveState.None, string? upload = "processed") =>
        new(embeddable, privacy, age, allowed, blocked, live, upload);

    [Fact]
    public void Ordinary_public_videos_play()
    {
        Assert.Equal(YouTubePlayability.Playable, YouTubePlayabilityRules.Evaluate(Status(), "GB"));
        Assert.Equal(YouTubePlayability.Playable, YouTubePlayabilityRules.Evaluate(null, "GB"));
        Assert.Equal(YouTubePlayability.Playable, YouTubePlayabilityRules.Evaluate(Status(live: YouTubeLiveState.Live), "GB"));
    }

    [Fact]
    public void Restrictions_are_reported_not_worked_around()
    {
        Assert.Equal(YouTubePlayability.Private, YouTubePlayabilityRules.Evaluate(Status(privacy: "private"), "GB"));
        Assert.Equal(YouTubePlayability.NotEmbeddable, YouTubePlayabilityRules.Evaluate(Status(embeddable: false), "GB"));
        Assert.Equal(YouTubePlayability.AgeRestricted, YouTubePlayabilityRules.Evaluate(Status(age: true), "GB"));
        Assert.Equal(YouTubePlayability.Upcoming, YouTubePlayabilityRules.Evaluate(Status(live: YouTubeLiveState.Upcoming), "GB"));
        Assert.Equal(YouTubePlayability.Processing, YouTubePlayabilityRules.Evaluate(Status(upload: "uploaded"), "GB"));
    }

    [Fact]
    public void Regional_limits_use_the_viewers_region()
    {
        Assert.Equal(YouTubePlayability.RegionBlocked, YouTubePlayabilityRules.Evaluate(Status(allowed: ["US", "CA"]), "GB"));
        Assert.Equal(YouTubePlayability.Playable, YouTubePlayabilityRules.Evaluate(Status(allowed: ["US", "GB"]), "gb"));
        Assert.Equal(YouTubePlayability.RegionBlocked, YouTubePlayabilityRules.Evaluate(Status(blocked: ["DE", "GB"]), "GB"));
        Assert.Equal(YouTubePlayability.Playable, YouTubePlayabilityRules.Evaluate(Status(blocked: ["DE"]), "GB"));
        // Unknown region: don't guess.
        Assert.Equal(YouTubePlayability.Playable, YouTubePlayabilityRules.Evaluate(Status(allowed: ["US"]), null));
    }

    [Theory]
    [InlineData(2, YouTubePlayability.PlayerProblem)]
    [InlineData(5, YouTubePlayability.PlayerProblem)]
    [InlineData(100, YouTubePlayability.NotFound)]
    [InlineData(101, YouTubePlayability.NotEmbeddable)]
    [InlineData(150, YouTubePlayability.NotEmbeddable)]
    [InlineData(153, YouTubePlayability.ClientIdentityRejected)]
    public void Player_errors_map_to_reasons(int code, YouTubePlayability expected) =>
        Assert.Equal(expected, YouTubePlayabilityRules.FromPlayerError(code));

    [Fact]
    public void Every_reason_but_playable_has_words()
    {
        foreach (var reason in Enum.GetValues<YouTubePlayability>().Where(r => r != YouTubePlayability.Playable))
        {
            var (title, detail) = YouTubePlayabilityRules.Describe(reason);
            Assert.False(string.IsNullOrWhiteSpace(title));
            Assert.False(string.IsNullOrWhiteSpace(detail));
        }
    }

    private static YouTubeEntry Entry(double position, double duration, DateTimeOffset? watched = null, DateTimeOffset? saved = null, DateTimeOffset? details = null) =>
        new("jNQXAC9IVRw", null, TimeSpan.FromSeconds(position), TimeSpan.FromSeconds(duration), watched, saved, details);

    [Fact]
    public void Continue_watching_uses_the_local_thresholds()
    {
        Assert.True(YouTubeDataRules.IsInProgress(Entry(60, 600, T0)));
        Assert.False(YouTubeDataRules.IsInProgress(Entry(5, 600, T0)));     // not really started
        Assert.False(YouTubeDataRules.IsInProgress(Entry(590, 600, T0)));   // last 20 s
        Assert.False(YouTubeDataRules.IsInProgress(Entry(60, 0, T0)));      // live: no length
        Assert.False(YouTubeDataRules.IsInProgress(Entry(60, 600, saved: T0))); // saved, never played here
    }

    [Fact]
    public void Start_position_prefers_the_links_time_then_progress()
    {
        var entry = Entry(120, 600, T0);
        Assert.Equal(TimeSpan.FromSeconds(30), YouTubeDataRules.StartPosition(entry, TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromSeconds(120), YouTubeDataRules.StartPosition(entry, TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, YouTubeDataRules.StartPosition(Entry(595, 600, T0), TimeSpan.Zero));
        Assert.Equal(TimeSpan.Zero, YouTubeDataRules.StartPosition(null, TimeSpan.Zero));
    }

    [Fact]
    public void Lists_are_ordered_by_recency()
    {
        var a = Entry(60, 600, T0.AddDays(-2)) with { VideoId = "aaaaaaaaaaa" };
        var b = Entry(60, 600, T0.AddDays(-1)) with { VideoId = "bbbbbbbbbbb", SavedUtc = T0.AddDays(-5) };
        var c = Entry(0, 0, saved: T0) with { VideoId = "ccccccccccc" };
        Assert.Equal(["bbbbbbbbbbb", "aaaaaaaaaaa"], YouTubeDataRules.ContinueWatching([a, b, c]).Select(e => e.VideoId));
        Assert.Equal(["ccccccccccc", "bbbbbbbbbbb"], YouTubeDataRules.Saved([a, b, c]).Select(e => e.VideoId));
    }

    [Fact]
    public void Youtubes_details_expire_after_thirty_days()
    {
        var fetched = Entry(0, 0, T0, details: T0);
        Assert.False(YouTubeDataRules.DetailsDueForRefresh(fetched, T0.AddDays(24)));
        Assert.True(YouTubeDataRules.DetailsDueForRefresh(fetched, T0.AddDays(25)));
        Assert.False(YouTubeDataRules.DetailsExpired(fetched, T0.AddDays(29.9)));
        Assert.True(YouTubeDataRules.DetailsExpired(fetched, T0.AddDays(30)));
        Assert.True(YouTubeDataRules.DetailsDueForRefresh(Entry(0, 0, T0), T0));
        Assert.False(YouTubeDataRules.DetailsExpired(Entry(0, 0, T0), T0.AddYears(1)));
    }

    [Fact]
    public void Entries_without_details_show_a_neutral_title_and_youtubes_own_thumbnail()
    {
        var entry = Entry(0, 0, T0);
        Assert.Equal("YouTube video", entry.Title);
        Assert.Equal("https://i.ytimg.com/vi/jNQXAC9IVRw/mqdefault.jpg", entry.ThumbnailUrl);
    }
}

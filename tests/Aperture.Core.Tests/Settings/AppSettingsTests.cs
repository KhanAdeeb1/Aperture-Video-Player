using Aperture.Core.Settings;

namespace Aperture.Core.Tests.Settings;

public class AppSettingsTests
{
    [Fact]
    public void Default_hide_delay_is_one_of_the_offered_choices() =>
        Assert.Contains(AppSettings.Default.ControlsHideDelaySeconds, AppSettings.HideDelayChoices);

    [Fact]
    public void Normalized_replaces_an_unknown_hide_delay() =>
        Assert.Equal(AppSettings.Default.ControlsHideDelaySeconds, (AppSettings.Default with { ControlsHideDelaySeconds = 7 }).Normalized().ControlsHideDelaySeconds);
}

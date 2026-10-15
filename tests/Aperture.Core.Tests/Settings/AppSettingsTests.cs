using Aperture.Core.Playback;
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

    [Fact]
    public void Normalized_repairs_out_of_range_values()
    {
        var damaged = AppSettings.Default with
        {
            Resume = (ResumeMode)42,
            Volume = double.NaN,
            Subtitles = new SubtitleStyle((SubtitleSize)9, SubtitleColor.White, SubtitleBackground.Box),
        };
        var repaired = damaged.Normalized();
        Assert.Equal(ResumeMode.Resume, repaired.Resume);
        Assert.Equal(1.0, repaired.Volume);
        Assert.Equal(SubtitleStyle.Default, repaired.Subtitles);
        Assert.Equal(0.0, (AppSettings.Default with { Volume = -3 }).Normalized().Volume);
    }

    [Fact]
    public void Defaults_are_offline_and_keep_pitch()
    {
        Assert.False(AppSettings.Default.OnlineMetadata);
        Assert.True(AppSettings.Default.PreservePitch);
        Assert.Equal(AppSettings.Default, AppSettings.Default.Normalized());
    }
}

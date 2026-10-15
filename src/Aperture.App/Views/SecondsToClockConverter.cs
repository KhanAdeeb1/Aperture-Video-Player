using Aperture.Core.Playback;
using Microsoft.UI.Xaml.Data;

namespace Aperture.App.Views;

/// <summary>Formats the timeline thumb tooltip (a value in seconds) as a clock time.</summary>
public sealed partial class SecondsToClockConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is double seconds ? TimeFormat.Clock(TimeSpan.FromSeconds(seconds)) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

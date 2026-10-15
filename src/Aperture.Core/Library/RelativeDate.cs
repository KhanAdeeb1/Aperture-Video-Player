using System.Globalization;

namespace Aperture.Core.Library;

/// <summary>Describes when something was played, the way a person would say it.</summary>
public static class RelativeDate
{
    /// <summary>"Just now", "Today", "Yesterday", "Monday", "12 Oct", or "12 Oct 2025".</summary>
    public static string Describe(DateTimeOffset when, DateTimeOffset now, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        if (now - when < TimeSpan.FromMinutes(5) && when <= now.AddMinutes(1))
        {
            return "Just now";
        }

        var day = when.ToOffset(now.Offset).Date;
        var today = now.Date;
        var days = (today - day).TotalDays;

        return days switch
        {
            0 => "Today",
            1 => "Yesterday",
            > 1 and < 7 => culture.DateTimeFormat.GetDayName(day.DayOfWeek),
            _ when day.Year == today.Year => day.ToString("d MMM", culture),
            _ => day.ToString("d MMM yyyy", culture),
        };
    }
}

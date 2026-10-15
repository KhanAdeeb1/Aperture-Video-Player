using System.Globalization;

namespace Aperture.Core.Playback;

/// <summary>The playback speeds Aperture offers, and stepping between them.</summary>
public static class PlaybackSpeed
{
    /// <summary>Normal speed.</summary>
    public const double Normal = 1.0;

    /// <summary>Offered speeds, slowest first.</summary>
    public static IReadOnlyList<double> Steps { get; } = [0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0];

    /// <summary>The next faster step, or the fastest.</summary>
    public static double Faster(double current)
    {
        foreach (var step in Steps)
        {
            if (step > current + 0.001)
            {
                return step;
            }
        }

        return Steps[^1];
    }

    /// <summary>The next slower step, or the slowest.</summary>
    public static double Slower(double current)
    {
        for (var i = Steps.Count - 1; i >= 0; i--)
        {
            if (Steps[i] < current - 0.001)
            {
                return Steps[i];
            }
        }

        return Steps[0];
    }

    /// <summary>"Normal", "1.25×", "0.5×".</summary>
    public static string Label(double speed) =>
        Math.Abs(speed - Normal) < 0.001
            ? "Normal"
            : speed.ToString("0.##", CultureInfo.InvariantCulture) + "×";
}

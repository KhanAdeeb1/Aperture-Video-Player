using Aperture.Core.Library;

namespace Aperture.Core.Casting;

/// <summary>
/// Decides which positions a TV reports are real enough to save. TVs report 0:00:00 while
/// loading, keep reporting the old position for a moment after a seek, and some jump back to
/// the start when they can't seek; saving any of those would wipe the resume point. A position
/// is trusted once the TV is playing or paused and has either reached the place Aperture asked
/// for or has clearly been playing on its own from where it is. A position well before the
/// place asked for is trusted only after a minute of steady playback from there.
/// </summary>
public sealed class CastPositionTracker
{
    /// <summary>How close to the requested place counts as having reached it.</summary>
    public const double ArrivalTolerance = 15;

    /// <summary>How long a TV must play from somewhere earlier than asked before that counts.</summary>
    public const double EarlierNeedsSeconds = 60;

    /// <summary>How long a TV must play steadily from somewhere other than asked before that counts.</summary>
    public const double SteadySeconds = 5;

    private double _requested;
    private double _offset;
    private double? _lastReading;
    private double? _advancingSince;
    private bool _settled;

    /// <param name="startSeconds">Where the cast was asked to start.</param>
    /// <param name="durationSeconds">The file's length, or 0 when unknown.</param>
    public CastPositionTracker(double startSeconds, double durationSeconds)
    {
        DurationSeconds = durationSeconds > 0 && double.IsFinite(durationSeconds) ? durationSeconds : 0;
        Restart(startSeconds, 0);
        Trusted = startSeconds > 0 ? startSeconds : null;
    }

    /// <summary>The last trusted position in the file, or null before there is one.</summary>
    public double? Trusted { get; private set; }

    /// <summary>Where the TV says it is (trusted or not), for showing; the requested place right after a move.</summary>
    public double? Latest { get; private set; }

    /// <summary>The file's length: the desktop's, or the TV's when the desktop didn't know.</summary>
    public double DurationSeconds { get; private set; }

    /// <summary>True when the last trusted position is at (or very near) the end.</summary>
    public bool NearEnd => Trusted is { } t && DurationSeconds > 0
        && WatchProgress.IsFinished(new HistoryEntry(string.Empty, string.Empty, TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(DurationSeconds), DateTimeOffset.MinValue));

    /// <summary>
    /// Aperture just asked the TV to go to <paramref name="requestedSeconds"/>. <paramref name="streamOffset"/>
    /// is where in the file the stream itself starts (non-zero for a converted stream started part-way),
    /// since the TV counts from the start of the stream.
    /// </summary>
    public void Restart(double requestedSeconds, double streamOffset)
    {
        _requested = Math.Max(0, requestedSeconds);
        _offset = Math.Max(0, streamOffset);
        Latest = _requested;
        _lastReading = null;
        _advancingSince = null;
        _settled = false;
    }

    /// <summary>Uses the TV's own length when the desktop didn't know the file's.</summary>
    public void OfferDuration(double? tvSeconds)
    {
        if (DurationSeconds <= 0 && tvSeconds is > 0 and <= 24 * 3600)
        {
            DurationSeconds = tvSeconds.Value + _offset;
        }
    }

    /// <summary>
    /// Takes one report. Returns true when it moved <see cref="Trusted"/>.
    /// </summary>
    /// <param name="state">What the TV says it is doing.</param>
    /// <param name="reportedSeconds">Its position in the stream, or null when it gave none.</param>
    /// <param name="nowSeconds">A steady clock in seconds, to time playback.</param>
    public bool Observe(CastState state, double? reportedSeconds, double nowSeconds)
    {
        if (state is not (CastState.Playing or CastState.Paused) || reportedSeconds is not { } reported || !double.IsFinite(reported) || reported < 0)
        {
            if (state is not (CastState.Playing or CastState.Paused))
            {
                _advancingSince = null;
            }

            return false;
        }

        var position = reported + _offset;
        if (DurationSeconds > 0 && position > DurationSeconds + 5)
        {
            return false; // a nonsense report
        }

        position = DurationSeconds > 0 ? Math.Min(position, DurationSeconds) : position;
        var previous = _lastReading;
        _lastReading = position;
        Latest = position;

        if (state == CastState.Playing && previous is { } p && position > p && position - p < 30)
        {
            _advancingSince ??= nowSeconds;
        }
        else if (state == CastState.Playing && previous is { } q && (position < q || position - q >= 30))
        {
            _advancingSince = null; // jumped: start timing again from here
        }

        // A jump back that Aperture didn't ask for (a TV resetting to 0:00, or the TV's own remote)
        // must hold for a minute before it replaces the saved place.
        if (_settled && Trusted is { } trusted && position < trusted - ArrivalTolerance)
        {
            _settled = false;
            _requested = trusted;
        }

        if (!_settled)
        {
            var arrived = Math.Abs(position - _requested) <= ArrivalTolerance;
            var earlier = position < _requested - ArrivalTolerance;
            var playedLongEnough = _advancingSince is { } since && nowSeconds - since >= EarlierNeedsSeconds;
            // A few seconds of steady playback, so a stale report from before a seek isn't taken.
            var playingOnItsOwn = _advancingSince is { } steady && nowSeconds - steady >= SteadySeconds && !earlier;
            if (!(arrived || playingOnItsOwn || playedLongEnough))
            {
                return false;
            }

            _settled = true;
        }

        if (Trusted == position)
        {
            return false;
        }

        Trusted = position;
        return true;
    }
}

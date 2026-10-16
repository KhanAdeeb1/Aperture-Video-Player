namespace Aperture.Core.Playback;

/// <summary>
/// An A–B loop: playback repeats the stretch between <see cref="Start"/> and <see cref="End"/>.
/// One key (or menu item) walks through the states: nothing, start marked, looping, nothing.
/// Loops belong to the open file only and are never saved.
/// </summary>
/// <param name="Start">Where the loop starts, once marked.</param>
/// <param name="End">Where it ends; the loop runs only when both are set.</param>
public sealed record LoopPoints(TimeSpan? Start = null, TimeSpan? End = null)
{
    /// <summary>The shortest loop accepted, so a double press doesn't trap playback on one frame.</summary>
    public static readonly TimeSpan ShortestLoop = TimeSpan.FromSeconds(0.5);

    /// <summary>No loop.</summary>
    public static LoopPoints None { get; } = new();

    /// <summary>True when playback is repeating a stretch.</summary>
    public bool IsLooping => Start is not null && End is not null;

    /// <summary>True when only the start has been marked.</summary>
    public bool IsMarking => Start is not null && End is null;

    /// <summary>
    /// The state after the loop key is pressed at <paramref name="position"/>: marks the start,
    /// then the end (an end before the start swaps them), then clears. An end too close to the
    /// start is refused and the start stays marked; <paramref name="refused"/> says so.
    /// </summary>
    public LoopPoints Next(TimeSpan position, out bool refused)
    {
        refused = false;
        position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        if (IsLooping)
        {
            return None;
        }

        if (Start is not { } start)
        {
            return new LoopPoints(position);
        }

        if ((position - start).Duration() < ShortestLoop)
        {
            refused = true;
            return this;
        }

        return position < start ? new LoopPoints(position, start) : new LoopPoints(start, position);
    }

    /// <summary>A start or end set directly (from the menu); the other end is kept when it still makes sense.</summary>
    public LoopPoints WithStart(TimeSpan position) =>
        End is { } end && end - position >= ShortestLoop ? new LoopPoints(position, end) : new LoopPoints(position);

    /// <summary>Sets the end; without a start, or with one too close or after it, nothing changes and false is returned.</summary>
    public bool TryWithEnd(TimeSpan position, out LoopPoints result)
    {
        if (Start is { } start && position - start >= ShortestLoop)
        {
            result = new LoopPoints(start, position);
            return true;
        }

        result = this;
        return false;
    }

    /// <summary>"Loop 1:02 – 1:15", "Loop starts at 1:02", or empty.</summary>
    public string Describe() => (Start, End) switch
    {
        ({ } a, { } b) => $"Loop {TimeFormat.Clock(a)} – {TimeFormat.Clock(b)}",
        ({ } a, null) => $"Loop starts at {TimeFormat.Clock(a)}",
        _ => string.Empty,
    };
}

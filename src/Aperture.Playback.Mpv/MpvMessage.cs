using Aperture.Playback.Mpv.Interop;

namespace Aperture.Playback.Mpv;

/// <summary>
/// A libmpv event copied into managed memory on the event thread, so it can be handled
/// after libmpv has reused the native event (which happens on the next wait).
/// </summary>
internal sealed record MpvMessage(MpvEventId Id, int Error, ulong ReplyUserData)
{
    public string? PropertyName { get; init; }

    public MpvFormat PropertyFormat { get; init; }

    public string? StringValue { get; init; }

    public double DoubleValue { get; init; }

    public bool FlagValue { get; init; }

    public long Int64Value { get; init; }

    public MpvEndFileReason EndReason { get; init; }

    public int EndError { get; init; }

    public long PlaylistEntryId { get; init; }

    public string? LogPrefix { get; init; }

    public string? LogLevel { get; init; }

    public string? LogText { get; init; }

    /// <summary>True when a property change carries no value (the property is unavailable).</summary>
    public bool IsEmpty => PropertyFormat == MpvFormat.None;
}

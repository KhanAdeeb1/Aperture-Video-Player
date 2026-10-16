namespace Aperture.Core.Casting;

/// <summary>A TV or other DLNA media renderer found on the home network.</summary>
/// <param name="Id">The renderer's own unique device name (UDN), stable across restarts.</param>
/// <param name="Name">The name it gives itself ("Living Room TV").</param>
/// <param name="Model">Manufacturer and model, when it says ("Samsung UE55").</param>
/// <param name="Address">Its IP address on the home network.</param>
public sealed record CastRenderer(string Id, string Name, string? Model, string Address);

/// <summary>Where a cast is.</summary>
public enum CastState
{
    /// <summary>No cast has been started since the server started.</summary>
    Idle,

    /// <summary>Sending the video to the TV and waiting for it to start.</summary>
    Connecting,

    Playing,

    Paused,

    /// <summary>The TV is loading (after starting or moving in the video).</summary>
    Buffering,

    /// <summary>The TV stopped answering; Aperture keeps trying for a while and keeps the last position.</summary>
    Reconnecting,

    /// <summary>Played to the end.</summary>
    Ended,

    /// <summary>Stopped from Aperture, from the TV, or because the TV started playing something else.</summary>
    Stopped,

    /// <summary>The TV didn't come back; the last position it reported is saved.</summary>
    Disconnected,

    /// <summary>It couldn't be played on the TV; <see cref="CastStatus.Message"/> says why.</summary>
    Failed,
}

/// <summary>The current (or last) cast, as the server tells the desktop app.</summary>
/// <param name="SessionId">Changes with every new cast, so the app can tell casts apart.</param>
/// <param name="State">Where it is.</param>
/// <param name="RendererName">The TV's name.</param>
/// <param name="Path">The file being cast.</param>
/// <param name="Title">Its title.</param>
/// <param name="PositionSeconds">The last position the TV reported (and Aperture trusted).</param>
/// <param name="DurationSeconds">The file's length, or 0 when unknown.</param>
/// <param name="Delivery">How the file reaches the TV, one sentence ("Plays the original file…").</param>
/// <param name="Message">Why it ended, failed or is reconnecting, one sentence; null otherwise.</param>
public sealed record CastStatus(
    string? SessionId,
    CastState State,
    string? RendererName,
    string? Path,
    string? Title,
    double PositionSeconds,
    double DurationSeconds,
    string? Delivery,
    string? Message)
{
    public static CastStatus None { get; } = new(null, CastState.Idle, null, null, null, 0, 0, null, null);

    /// <summary>True while the TV is (or may still be) playing for Aperture.</summary>
    public bool IsActive => IsActiveState(State);

    public static bool IsActiveState(CastState state) =>
        state is CastState.Connecting or CastState.Playing or CastState.Paused or CastState.Buffering or CastState.Reconnecting;
}

/// <summary>What the desktop app asks to cast.</summary>
/// <param name="RendererId">The TV, from <see cref="CastRenderer.Id"/>.</param>
/// <param name="Path">The file on this computer.</param>
/// <param name="Title">Its title, shown on the TV where it shows one.</param>
/// <param name="StartSeconds">Where to start (the desktop's current position).</param>
/// <param name="DurationSeconds">The file's length as the desktop player knows it, or 0.</param>
public sealed record CastStartRequest(string RendererId, string Path, string Title, double StartSeconds, double DurationSeconds);

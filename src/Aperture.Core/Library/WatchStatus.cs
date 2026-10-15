namespace Aperture.Core.Library;

/// <summary>How far through an item the user is.</summary>
public enum WatchState
{
    Unwatched,
    InProgress,
    Watched,
}

/// <summary>Combines playback history with the user's own "mark as watched" choice.</summary>
public static class WatchStatus
{
    /// <summary>
    /// The user's choice wins; otherwise history decides (finished is watched, started is in
    /// progress, never played is unwatched).
    /// </summary>
    public static WatchState Of(HistoryEntry? history, bool? watchedOverride)
    {
        if (watchedOverride is { } marked)
        {
            return marked ? WatchState.Watched : (history is not null && WatchProgress.IsInProgress(history) ? WatchState.InProgress : WatchState.Unwatched);
        }

        if (history is null)
        {
            return WatchState.Unwatched;
        }

        return WatchProgress.IsFinished(history) ? WatchState.Watched
            : WatchProgress.IsInProgress(history) ? WatchState.InProgress
            : WatchState.Unwatched;
    }
}

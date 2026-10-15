namespace Aperture.App.Interop;

/// <summary>
/// Hides the mouse pointer over the video while playback controls are hidden. Calls are
/// balanced so the pointer can never be left hidden: <see cref="Show"/> undoes exactly
/// what <see cref="Hide"/> did. Must be called on the UI thread.
/// </summary>
internal sealed class CursorVisibility
{
    private bool _hidden;

    public void Hide()
    {
        if (_hidden)
        {
            return;
        }

        _hidden = true;
        _ = NativeMethods.ShowCursor(false);
    }

    public void Show()
    {
        if (!_hidden)
        {
            return;
        }

        _hidden = false;
        _ = NativeMethods.ShowCursor(true);
    }
}

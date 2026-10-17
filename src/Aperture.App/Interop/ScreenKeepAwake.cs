namespace Aperture.App.Interop;

/// <summary>
/// Keeps the screen on and the computer out of idle sleep while a video plays, as video
/// players do (the Media Foundation player did this by itself; libmpv's render API does not).
/// It changes no power setting: the lid, the power button and Start › Sleep still sleep, and
/// Windows' normal timeouts return as soon as <see cref="Release"/> runs. The request belongs
/// to the calling thread, so it must always be called on the UI thread.
/// </summary>
internal sealed class ScreenKeepAwake
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    private bool _held;

    public void Hold()
    {
        if (_held)
        {
            return;
        }

        // Zero means Windows refused; leave _held false so the next call tries again.
        _held = NativeMethods.SetThreadExecutionState(EsContinuous | EsSystemRequired | EsDisplayRequired) != 0;
    }

    public void Release()
    {
        if (!_held)
        {
            return;
        }

        _held = false;
        _ = NativeMethods.SetThreadExecutionState(EsContinuous);
    }
}

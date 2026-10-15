using Microsoft.UI.Dispatching;

namespace Aperture.App.Views;

/// <summary>
/// Shows playback controls on activity and hides them after an idle delay. The overlay
/// stays up whenever <see cref="IsPinned"/> says so (paused, pointer on the controls, a
/// menu open, keyboard focus in the controls).
/// </summary>
internal sealed class IdleOverlay
{
    private readonly DispatcherQueueTimer _timer;

    public IdleOverlay(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => TryHide();
    }

    /// <summary>True while the controls are showing.</summary>
    public bool IsVisible { get; private set; } = true;

    /// <summary>How long to wait after the last activity.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>When this returns true the overlay never hides.</summary>
    public Func<bool> IsPinned { get; set; } = () => false;

    /// <summary>Raised when <see cref="IsVisible"/> changes.</summary>
    public event EventHandler? VisibilityChanged;

    /// <summary>Pointer, keyboard or state activity: show now and restart the idle wait.</summary>
    public void Poke()
    {
        SetVisible(true);
        _timer.Stop();
        _timer.Interval = Delay;
        _timer.Start();
    }

    /// <summary>Hides straight away unless pinned (for example when the pointer leaves the window).</summary>
    public void TryHide()
    {
        _timer.Stop();
        if (!IsPinned())
        {
            SetVisible(false);
        }
    }

    /// <summary>Stops the timer and shows the overlay; used when leaving the player.</summary>
    public void Reset()
    {
        _timer.Stop();
        SetVisible(true);
    }

    private void SetVisible(bool visible)
    {
        if (IsVisible == visible)
        {
            return;
        }

        IsVisible = visible;
        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }
}

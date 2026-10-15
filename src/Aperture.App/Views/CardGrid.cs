using Microsoft.UI.Xaml.Controls;

namespace Aperture.App.Views;

/// <summary>
/// Keeps a wrapping grid of cards sized to whole columns. A uniform grid measures its first
/// card at the minimum width and then stretches every card wider without making rows taller,
/// which cuts off the line under each card's 16:9 artwork. Setting the minimum to the exact
/// column width means cards are measured at the width they are shown at.
/// </summary>
internal static class CardGrid
{
    public static void Fit(ItemsRepeater repeater)
    {
        if (repeater.Layout is not UniformGridLayout layout)
        {
            return;
        }

        var minimum = layout.MinItemWidth;
        repeater.SizeChanged += (_, e) =>
        {
            // Only width matters; new rows change the height, and reacting to that would loop.
            var width = e.NewSize.Width;
            if (width <= 0 || Math.Abs(width - e.PreviousSize.Width) < 0.5)
            {
                return;
            }

            // Change the layout after this layout pass rather than during it.
            repeater.DispatcherQueue.TryEnqueue(() =>
            {
                var spacing = layout.MinColumnSpacing;
                var current = repeater.ActualWidth;
                if (current <= 0)
                {
                    return;
                }

                var columns = Math.Max(1, Math.Floor((current + spacing) / (minimum + spacing)));
                var column = Math.Max(minimum, Math.Floor((current - (spacing * (columns - 1))) / columns));
                if (Math.Abs(layout.MinItemWidth - column) > 0.5)
                {
                    layout.MinItemWidth = column;
                }
            });
        };
    }
}

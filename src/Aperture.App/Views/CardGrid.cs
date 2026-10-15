using Microsoft.UI.Xaml.Controls;

namespace Aperture.App.Views;

/// <summary>
/// Keeps a wrapping grid of cards sized to whole columns. A uniform grid measures one card and
/// gives every row that height, then stretches cards wider without re-measuring, which cut off
/// the line under the 16:9 artwork. Here each card is given the exact column width, so it is
/// measured at the width it is shown at.
/// </summary>
internal sealed class CardGrid
{
    private readonly ItemsRepeater _repeater;
    private readonly UniformGridLayout _layout;
    private readonly double _minimum;
    private double _column;

    private CardGrid(ItemsRepeater repeater, UniformGridLayout layout)
    {
        _repeater = repeater;
        _layout = layout;
        _minimum = layout.MinItemWidth;
        _column = _minimum;
        repeater.ElementPrepared += (_, e) => e.Element.Width = _column;
        repeater.SizeChanged += (_, e) =>
        {
            // Only width matters; new rows change the height, and reacting to that would loop.
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) >= 0.5)
            {
                // Change the layout after this layout pass rather than during it.
                repeater.DispatcherQueue.TryEnqueue(Update);
            }
        };
    }

    public static void Fit(ItemsRepeater repeater)
    {
        if (repeater.Layout is UniformGridLayout layout)
        {
            _ = new CardGrid(repeater, layout);
        }
    }

    private void Update()
    {
        var width = _repeater.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        var spacing = _layout.MinColumnSpacing;
        var columns = Math.Max(1, Math.Floor((width + spacing) / (_minimum + spacing)));
        var column = Math.Max(_minimum, Math.Floor((width - (spacing * (columns - 1))) / columns));
        if (Math.Abs(column - _column) < 0.5)
        {
            return;
        }

        _column = column;
        _layout.MinItemWidth = column;
        var count = _repeater.ItemsSourceView?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            if (_repeater.TryGetElement(i) is { } element)
            {
                element.Width = column;
            }
        }
    }
}

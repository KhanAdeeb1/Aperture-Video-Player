using Aperture.App.Services;
using Aperture.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Aperture.App.Views;

/// <summary>What the user asked a card to do.</summary>
internal enum CardActionKind
{
    Play,
    PlayFromStart,
    PlayCopy,
    MarkWatched,
    MarkUnwatched,
    EditDetails,
    RemoveFromHistory,
}

/// <summary>A card action; <see cref="Path"/> is the copy to play for <see cref="CardActionKind.PlayCopy"/>.</summary>
internal sealed record CardAction(CardActionKind Kind, MediaCardModel Model, string? Path = null);

public sealed partial class MediaCard : UserControl
{
    private MediaCardModel? _model;

    public MediaCard()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the card is invoked (click, tap, Enter or Space) or a menu action is chosen.</summary>
    internal event EventHandler<CardAction>? ActionRequested;

    internal MediaCardModel? Model
    {
        get => _model;
        set
        {
            _model = value;
            Render();
        }
    }

    /// <summary>Moves keyboard focus to the card.</summary>
    public bool FocusCard() => CardButton.Focus(FocusState.Keyboard);

    public void SetArtwork(Microsoft.UI.Xaml.Media.ImageSource? image)
    {
        if (_model is not null)
        {
            _model.Artwork = image;
        }

        ArtworkImage.Source = image;
        EmptyTitle.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Render()
    {
        if (_model is null)
        {
            return;
        }

        TitleText.Text = _model.Title;
        EmptyTitle.Text = _model.Title;
        MetaText.Text = _model.Meta;
        AutomationProperties.SetName(CardButton, _model.AccessibleName);
        ToolTipService.SetToolTip(CardButton, _model.Library?.Item.Overview is { Length: > 0 } overview ? $"{_model.Title}\n{overview}" : _model.Title);

        var showProgress = _model.Progress is not null;
        ProgressTrack.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        ProgressScrim.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        WatchedBadge.Visibility = _model.IsWatched ? Visibility.Visible : Visibility.Collapsed;
        CheckBadge.Visibility = _model.NeedsCheck ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressWidth();
        SetArtwork(_model.Artwork);
    }

    private void OnMenuOpening(object sender, object e)
    {
        CardMenu.Items.Clear();
        if (_model is not { } model)
        {
            return;
        }

        Add("Play", "", CardActionKind.Play);
        if (model.History is { } history && WatchProgress.IsInProgress(history))
        {
            Add("Play from the beginning", "", CardActionKind.PlayFromStart);
        }

        if (model.Library is { } library)
        {
            if (library.Group.HasCopies)
            {
                var copies = new MenuFlyoutSubItem { Text = "Play another copy", Icon = Glyph("") };
                foreach (var copy in library.Group.Others)
                {
                    var item = new MenuFlyoutItem { Text = library.Group.Describe(copy) };
                    ToolTipService.SetToolTip(item, copy.Path);
                    var path = copy.Path;
                    item.Click += (_, _) => Raise(CardActionKind.PlayCopy, path);
                    copies.Items.Add(item);
                }

                CardMenu.Items.Add(copies);
            }

            CardMenu.Items.Add(new MenuFlyoutSeparator());
            if (library.State == WatchState.Watched)
            {
                Add("Mark as unwatched", "", CardActionKind.MarkUnwatched);
            }
            else
            {
                Add("Mark as watched", "", CardActionKind.MarkWatched);
            }

            Add(model.NeedsCheck ? "Check details…" : "Edit details…", "", CardActionKind.EditDetails);
        }

        CardMenu.Items.Add(new MenuFlyoutSeparator());
        var folder = new MenuFlyoutItem { Text = "Show in folder", Icon = Glyph("") };
        folder.Click += (_, _) => ShellActions.ShowInFolder(model.Path);
        CardMenu.Items.Add(folder);

        if (model.History is not null)
        {
            Add("Remove from history", "", CardActionKind.RemoveFromHistory);
        }

        void Add(string text, string glyph, CardActionKind kind)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = Glyph(glyph) };
            item.Click += (_, _) => Raise(kind);
            CardMenu.Items.Add(item);
        }
    }

    private static FontIcon Glyph(string glyph) => new() { Glyph = glyph };

    private void Raise(CardActionKind kind, string? path = null)
    {
        if (_model is not null)
        {
            ActionRequested?.Invoke(this, new CardAction(kind, _model, path));
        }
    }

    /// <summary>
    /// Sets the 16:9 artwork height from the card's width before measuring, so the card reports
    /// its real height in the same pass. Card grids size their rows from that measurement.
    /// Cards are always given a width (in XAML, or by <see cref="CardGrid"/>).
    /// </summary>
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        var width = double.IsNaN(Width) ? availableSize.Width : Width;
        if (width > 0 && !double.IsInfinity(width))
        {
            var height = Math.Round(width * 9 / 16);
            // Height starts unset (NaN), and NaN never compares as different.
            if (double.IsNaN(Artwork.Height) || Math.Abs(Artwork.Height - height) > 0.5)
            {
                Artwork.Height = height;
            }
        }

        return base.MeasureOverride(availableSize);
    }

    private void OnArtworkSizeChanged(object sender, SizeChangedEventArgs e) => UpdateProgressWidth();

    private void UpdateProgressWidth()
    {
        if (_model?.Progress is double progress && ProgressTrack.ActualWidth > 0)
        {
            ProgressFill.Width = Math.Max(ProgressTrack.ActualWidth * progress, 4);
        }
        else if (_model?.Progress is double)
        {
            ProgressTrack.SizeChanged -= OnProgressTrackSizeChanged;
            ProgressTrack.SizeChanged += OnProgressTrackSizeChanged;
        }
    }

    private void OnProgressTrackSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_model?.Progress is double progress)
        {
            ProgressFill.Width = Math.Max(e.NewSize.Width * progress, 4);
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        VisualStateManager.GoToState(this, "Hover", useTransitions: true);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        VisualStateManager.GoToState(this, "Rest", useTransitions: true);
    }

    private void OnInvoke(object sender, RoutedEventArgs e) => Raise(CardActionKind.Play);
}

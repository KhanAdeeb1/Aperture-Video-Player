using Aperture.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Aperture.App.Views;

public sealed partial class MediaCard : UserControl
{
    private MediaCardModel? _model;

    public MediaCard()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the card is clicked, tapped, or invoked with Enter or Space.</summary>
    internal event EventHandler<MediaCardModel>? Invoked;

    /// <summary>Raised when the user chooses "Remove from history".</summary>
    internal event EventHandler<MediaCardModel>? RemoveRequested;

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
        ToolTipService.SetToolTip(CardButton, _model.Title);

        var showProgress = _model.Progress is not null;
        ProgressTrack.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        ProgressScrim.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgressWidth();
        SetArtwork(_model.Artwork);
    }

    private void OnArtworkSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var height = Math.Round(e.NewSize.Width * 9 / 16);
        if (Math.Abs(Artwork.Height - height) > 0.5)
        {
            Artwork.Height = height;
        }

        UpdateProgressWidth();
    }

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

    private void OnInvoke(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            Invoked?.Invoke(this, _model);
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            RemoveRequested?.Invoke(this, _model);
        }
    }

    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (_model is not null)
        {
            ShellActions.ShowInFolder(_model.Entry.Path);
        }
    }
}

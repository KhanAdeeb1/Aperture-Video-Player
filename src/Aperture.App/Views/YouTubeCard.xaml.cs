using Aperture.Core.Playback;
using Aperture.Core.YouTube;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Aperture.App.Views;

/// <summary>What a YouTube card shows: the video, and Aperture's own record of it when there is one.</summary>
internal sealed record YouTubeCardModel(string VideoId, YouTubeVideo? Video, YouTubeEntry? Entry, bool ShowProgress)
{
    public string Title => Video?.Title is { Length: > 0 } title ? title : Entry?.Title ?? "YouTube video";

    public string ThumbnailUrl => Video?.ThumbnailUrl ?? Entry?.ThumbnailUrl ?? YouTubeVideo.DefaultThumbnail(VideoId);

    public TimeSpan? Length => Video?.Duration ?? (Entry?.Duration > TimeSpan.Zero ? Entry.Duration : null);

    public bool IsLive => Video?.Status?.Live == YouTubeLiveState.Live;

    /// <summary>Under the title: time left when in progress, else the channel.</summary>
    public string Meta =>
        ShowProgress && Entry is { } entry ? $"{TimeFormat.Remaining(entry.Remaining)}{Channel(" · ")}"
        : Video?.Channel is { Length: > 0 } channel ? channel
        : Entry?.Video?.Channel ?? string.Empty;

    private string Channel(string separator) => (Video?.Channel ?? Entry?.Video?.Channel) is { Length: > 0 } channel ? separator + channel : string.Empty;

    public static YouTubeCardModel From(YouTubeVideo video, YouTubeEntry? entry = null) => new(video.Id, video, entry, false);

    public static YouTubeCardModel From(YouTubeEntry entry, bool showProgress) => new(entry.VideoId, entry.Video, entry, showProgress);
}

/// <summary>Things a YouTube card can ask for.</summary>
internal enum YouTubeCardAction
{
    Play,
    PlayFromStart,
    Save,
    Unsave,
    RemoveFromContinue,
    OpenOnYouTube,
    CopyLink,
}

public sealed partial class YouTubeCard : UserControl
{
    private YouTubeCardModel? _model;

    public YouTubeCard()
    {
        InitializeComponent();
    }

    internal event EventHandler<(YouTubeCardAction Action, YouTubeCardModel Model)>? ActionRequested;

    /// <summary>Whether the video is in Saved, so the menu offers the right action.</summary>
    internal bool IsSaved { get; set; }

    internal YouTubeCardModel? Model
    {
        get => _model;
        set
        {
            _model = value;
            Render();
        }
    }

    public bool FocusCard() => CardButton.Focus(FocusState.Keyboard);

    private void Render()
    {
        if (_model is not { } model)
        {
            return;
        }

        TitleText.Text = model.Title;
        MetaText.Text = model.Meta;
        ToolTipService.SetToolTip(CardButton, model.Title);
        AutomationProperties.SetName(CardButton, $"{model.Title}, {model.Meta}");

        var length = model.IsLive ? "Live" : model.Length is { } value ? TimeFormat.Clock(value) : null;
        LengthTag.Visibility = length is null || model.ShowProgress ? Visibility.Collapsed : Visibility.Visible;
        LengthText.Text = length ?? string.Empty;

        var showProgress = model.ShowProgress && model.Entry is not null;
        ProgressTrack.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        ProgressScrim.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress(ProgressTrack.ActualWidth);

        // YouTube's own thumbnail, decoded at the size it is shown (the 320 px "medium" image).
        Thumbnail.Source = Uri.TryCreate(model.ThumbnailUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? new BitmapImage(uri) { DecodePixelWidth = 320 }
            : null;
    }

    private void UpdateProgress(double width)
    {
        if (_model?.Entry is { } entry && width > 0)
        {
            ProgressFill.Width = Math.Max(width * entry.Progress, 4);
        }
    }

    private void OnProgressTrackSizeChanged(object sender, SizeChangedEventArgs e) => UpdateProgress(e.NewSize.Width);

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
    {
        var width = double.IsNaN(Width) ? availableSize.Width : Width;
        if (width > 0 && !double.IsInfinity(width))
        {
            var height = Math.Round(width * 9 / 16);
            if (double.IsNaN(Artwork.Height) || Math.Abs(Artwork.Height - height) > 0.5)
            {
                Artwork.Height = height;
            }
        }

        return base.MeasureOverride(availableSize);
    }

    private void OnMenuOpening(object sender, object e)
    {
        CardMenu.Items.Clear();
        if (_model is not { } model)
        {
            return;
        }

        Add("Play", "", YouTubeCardAction.Play);
        if (model.Entry is { } entry && YouTubeDataRules.IsInProgress(entry))
        {
            Add("Play from the beginning", "", YouTubeCardAction.PlayFromStart);
        }

        CardMenu.Items.Add(new MenuFlyoutSeparator());
        if (IsSaved)
        {
            Add("Remove from Saved", "", YouTubeCardAction.Unsave);
        }
        else
        {
            Add("Save for later", "", YouTubeCardAction.Save);
        }

        if (model.ShowProgress)
        {
            Add("Remove from Continue watching", "", YouTubeCardAction.RemoveFromContinue);
        }

        CardMenu.Items.Add(new MenuFlyoutSeparator());
        Add("Watch on YouTube", "", YouTubeCardAction.OpenOnYouTube);
        Add("Copy link", "", YouTubeCardAction.CopyLink);

        void Add(string text, string glyph, YouTubeCardAction action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => Raise(action);
            CardMenu.Items.Add(item);
        }
    }

    private void Raise(YouTubeCardAction action)
    {
        if (_model is not null)
        {
            ActionRequested?.Invoke(this, (action, _model));
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "Hover", useTransitions: true);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => VisualStateManager.GoToState(this, "Rest", useTransitions: true);

    private void OnInvoke(object sender, RoutedEventArgs e) => Raise(YouTubeCardAction.Play);
}

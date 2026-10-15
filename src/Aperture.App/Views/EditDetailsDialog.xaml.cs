using System.Globalization;
using Aperture.App.Services;
using Aperture.Core.Library;
using Aperture.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Aperture.App.Views;

/// <summary>Corrects a library item's details. Saving writes Aperture's database only.</summary>
public sealed partial class EditDetailsDialog : ContentDialog
{
    private readonly LibraryItem _item;
    private readonly LibraryCoordinator _library;

    internal EditDetailsDialog(LibraryItem item, LibraryCoordinator library)
    {
        _item = item;
        _library = library;
        InitializeComponent();

        PathText.Text = item.Path;
        foreach (var category in MediaCategories.All)
        {
            CategoryBox.Items.Add(new ComboBoxItem { Content = MediaCategories.SingularLabel(category), Tag = category });
        }

        CategoryBox.SelectedItem = CategoryBox.Items.OfType<ComboBoxItem>().First(i => (MediaCategory)i.Tag == item.Category);
        TitleBox.Text = item.Title;
        YearBox.Value = item.Year ?? double.NaN;
        SeasonBox.Value = item.Season ?? double.NaN;
        EpisodeBox.Value = item.Episode ?? double.NaN;
        EpisodeTitleBox.Text = item.EpisodeTitle ?? string.Empty;

        if (item.SuggestedMatch is { } match)
        {
            SuggestionPanel.Visibility = Visibility.Visible;
            var year = match.Year is { } y ? $" ({y.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
            SuggestionText.Text = match.EpisodeTitle is { Length: > 0 } episode ? $"{match.Title}{year} · {episode}" : match.Title + year;
        }

        SourceText.Text = item.Source switch
        {
            MetadataSource.Manual => "You corrected these details. Scans and online lookups leave them alone.",
            MetadataSource.Online => $"These details came from {item.SuggestedMatch?.Service ?? "an online service"}. Saving keeps your version from now on.",
            _ when item.Confidence == MatchConfidence.Uncertain => "Aperture guessed these from the file name and isn’t sure. Saving keeps your version from now on.",
            _ => "Read from the file name. Saving keeps your version from now on.",
        };

        // "Use file name" only makes sense once the details differ from what the file name says.
        SecondaryButtonText = item.Source == MetadataSource.FileName ? string.Empty : "Use file name";
        UpdateFields();
    }

    /// <summary>True when something was saved, reset or accepted, so the caller should refresh.</summary>
    public bool Changed { get; private set; }

    private MediaCategory SelectedCategory =>
        CategoryBox.SelectedItem is ComboBoxItem { Tag: MediaCategory category } ? category : _item.Category;

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e) => UpdateFields();

    private void OnTitleChanged(object sender, TextChangedEventArgs e) => IsPrimaryButtonEnabled = TitleBox.Text.Trim().Length > 0;

    private void UpdateFields()
    {
        var category = SelectedCategory;
        YearBox.Visibility = category == MediaCategory.Movie || category == MediaCategory.Episode ? Visibility.Visible : Visibility.Collapsed;
        SeasonBox.Visibility = category == MediaCategory.Episode ? Visibility.Visible : Visibility.Collapsed;
        EpisodeBox.Visibility = category is MediaCategory.Episode or MediaCategory.Podcast ? Visibility.Visible : Visibility.Collapsed;
        EpisodeTitleBox.Visibility = EpisodeBox.Visibility;
        TitleBox.Header = category == MediaCategory.Episode ? "Show" : category == MediaCategory.Podcast ? "Podcast" : "Title";
    }

    private async void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var title = TitleBox.Text.Trim();
        if (title.Length == 0)
        {
            args.Cancel = true;
            return;
        }

        var deferral = args.GetDeferral();
        try
        {
            var category = SelectedCategory;
            var details = new ManualDetails(
                category,
                title,
                YearBox.Visibility == Visibility.Visible ? Whole(YearBox.Value) : null,
                category == MediaCategory.Episode ? Whole(SeasonBox.Value) : null,
                category is MediaCategory.Episode or MediaCategory.Podcast ? Whole(EpisodeBox.Value) : null,
                category is MediaCategory.Episode or MediaCategory.Podcast ? EpisodeTitleBox.Text : null);
            await _library.SaveDetailsAsync(_item, details);
            Changed = true;
        }
        catch (Exception error) when (error is System.Data.Common.DbException or IOException)
        {
            AppLog.Error("Could not save corrected details", error);
            args.Cancel = true;
            SourceText.Text = "Aperture couldn’t save these details. Details are in Aperture’s log.";
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnResetClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            await _library.ResetDetailsAsync(_item);
            Changed = true;
        }
        catch (Exception error) when (error is System.Data.Common.DbException or IOException)
        {
            AppLog.Error("Could not reset details", error);
            args.Cancel = true;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnAcceptSuggestionClick(object sender, RoutedEventArgs e)
    {
        AcceptSuggestionButton.IsEnabled = false;
        try
        {
            await _library.AcceptSuggestionAsync(_item);
            Changed = true;
            Hide();
        }
        catch (Exception error) when (error is System.Data.Common.DbException or IOException or HttpRequestException)
        {
            AppLog.Error("Could not apply the suggested match", error);
            AcceptSuggestionButton.IsEnabled = true;
        }
    }

    private static int? Whole(double value) => double.IsNaN(value) ? null : (int)Math.Round(value);
}

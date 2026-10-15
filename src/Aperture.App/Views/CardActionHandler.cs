using Aperture.App.Services;
using Microsoft.UI.Xaml;

namespace Aperture.App.Views;

/// <summary>A request to play a file; <see cref="FromStart"/> ignores the saved position.</summary>
internal sealed record PlayRequest(string Path, bool FromStart = false);

/// <summary>
/// Carries out card actions the same way on Home and in the library. Playing is handed
/// back to the window; everything else changes Aperture's records only, never files.
/// </summary>
internal sealed class CardActionHandler(AppServices services, ThumbnailCache thumbnails)
{
    /// <summary>Handles <paramref name="action"/>. Returns true when the caller should reload what it shows.</summary>
    public async Task<bool> HandleAsync(CardAction action, XamlRoot? root, Action<PlayRequest> play)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(play);
        var model = action.Model;
        var copies = model.Library?.Group.All.Select(i => i.Path).ToList() ?? new List<string> { model.Path };
        try
        {
            switch (action.Kind)
            {
                case CardActionKind.Play:
                    play(new PlayRequest(model.Path));
                    return false;
                case CardActionKind.PlayFromStart:
                    play(new PlayRequest(model.Path, FromStart: true));
                    return false;
                case CardActionKind.PlayCopy when action.Path is { } copy:
                    play(new PlayRequest(copy));
                    return false;
                case CardActionKind.MarkWatched:
                    await services.Library.SetWatchedAsync(copies, true);
                    return true;
                case CardActionKind.MarkUnwatched:
                    await services.Library.SetWatchedAsync(copies, false);
                    return true;
                case CardActionKind.EditDetails when model.Library is { } entry && root is not null:
                    var dialog = new EditDetailsDialog(entry.Item, services.Library) { XamlRoot = root };
                    await dialog.ShowAsync();
                    return dialog.Changed;
                case CardActionKind.RemoveFromHistory:
                    await services.History.RemoveAsync(model.Path);
                    thumbnails.Forget(model.Path);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception error) when (error is System.Data.Common.DbException or IOException)
        {
            AppLog.Error($"Could not {action.Kind}", error);
            return false;
        }
    }
}

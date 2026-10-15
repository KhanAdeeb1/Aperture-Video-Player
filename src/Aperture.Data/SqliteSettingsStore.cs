using System.Globalization;
using Aperture.Core.Playback;
using Aperture.Core.Series;
using Aperture.Core.Settings;
using Microsoft.Data.Sqlite;

namespace Aperture.Data;

/// <summary>Settings and customised shortcuts stored as key/value rows in the <c>settings</c> table.</summary>
public sealed class SqliteSettingsStore(ApertureDatabase database) : ISettingsStore, IShortcutStore
{
    // Milestone 1 stored resume as a yes/no; it is still read when the newer key is absent.
    private const string LegacyResumeKey = "playback.resume";
    private const string ResumeModeKey = "playback.resumeMode";
    private const string RememberHistoryKey = "history.remember";
    private const string HideDelayKey = "playback.controlsHideDelaySeconds";
    private const string PreservePitchKey = "playback.preservePitch";
    private const string SubtitleSizeKey = "subtitles.size";
    private const string SubtitleColorKey = "subtitles.color";
    private const string SubtitleBackgroundKey = "subtitles.background";
    private const string VolumeKey = "playback.volume";
    private const string MutedKey = "playback.muted";
    private const string OnlineMetadataKey = "library.onlineMetadata";
    private const string AutoplayKey = "episodes.autoplay";
    private const string CountdownKey = "episodes.countdownSeconds";
    private const string IntroSkipKey = "episodes.introSkip";
    private const string RecapSkipKey = "episodes.recapSkip";
    private const string SkipCreditsKey = "episodes.offerSkipCredits";
    private const string FindSegmentsKey = "episodes.findSegments";
    private const string YouTubeAutoplayKey = "youtube.autoplay";
    private const string YouTubeAcceptedKey = "youtube.accepted";
    private const string ClearDialogueKey = "sound.clearDialogue";
    private const string EvenVolumeKey = "sound.evenVolume";
    private const string SeekPreviewsKey = "playback.seekPreviews";
    private const string ShortcutPrefix = "shortcut.";

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var values = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var defaults = AppSettings.Default;
        var resume = ReadEnum(values, ResumeModeKey, (ResumeMode?)null)
            ?? (values.ContainsKey(LegacyResumeKey) ? (ReadBool(values, LegacyResumeKey, true) ? ResumeMode.Resume : ResumeMode.StartOver) : defaults.Resume);

        return new AppSettings(
            Resume: resume,
            RememberHistory: ReadBool(values, RememberHistoryKey, defaults.RememberHistory),
            ControlsHideDelaySeconds: ReadInt(values, HideDelayKey, defaults.ControlsHideDelaySeconds),
            PreservePitch: ReadBool(values, PreservePitchKey, defaults.PreservePitch),
            Subtitles: new SubtitleStyle(
                ReadEnum(values, SubtitleSizeKey, (SubtitleSize?)null) ?? defaults.SubtitleStyle.Size,
                ReadEnum(values, SubtitleColorKey, (SubtitleColor?)null) ?? defaults.SubtitleStyle.Color,
                ReadEnum(values, SubtitleBackgroundKey, (SubtitleBackground?)null) ?? defaults.SubtitleStyle.Background),
            Volume: ReadDouble(values, VolumeKey, defaults.Volume),
            Muted: ReadBool(values, MutedKey, defaults.Muted),
            OnlineMetadata: ReadBool(values, OnlineMetadataKey, defaults.OnlineMetadata),
            AutoplayNextEpisode: ReadBool(values, AutoplayKey, defaults.AutoplayNextEpisode),
            NextEpisodeCountdownSeconds: ReadInt(values, CountdownKey, defaults.NextEpisodeCountdownSeconds),
            IntroSkip: ReadEnum(values, IntroSkipKey, (SkipMode?)null) ?? defaults.IntroSkip,
            RecapSkip: ReadEnum(values, RecapSkipKey, (SkipMode?)null) ?? defaults.RecapSkip,
            OfferSkipCredits: ReadBool(values, SkipCreditsKey, defaults.OfferSkipCredits),
            FindSegments: ReadBool(values, FindSegmentsKey, defaults.FindSegments),
            YouTubeAutoplay: ReadBool(values, YouTubeAutoplayKey, defaults.YouTubeAutoplay),
            YouTubeAccepted: ReadBool(values, YouTubeAcceptedKey, defaults.YouTubeAccepted),
            Sound: new SoundOptions(
                ReadBool(values, ClearDialogueKey, defaults.SoundDefaults.ClearDialogue),
                ReadBool(values, EvenVolumeKey, defaults.SoundDefaults.EvenVolume)),
            SeekPreviews: ReadBool(values, SeekPreviewsKey, defaults.SeekPreviews)).Normalized();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = settings.Normalized();
        var style = settings.SubtitleStyle;
        await WriteAsync(
            [
                (ResumeModeKey, settings.Resume.ToString()),
                (LegacyResumeKey, settings.Resume != ResumeMode.StartOver ? "true" : "false"),
                (RememberHistoryKey, Bool(settings.RememberHistory)),
                (HideDelayKey, settings.ControlsHideDelaySeconds.ToString(CultureInfo.InvariantCulture)),
                (PreservePitchKey, Bool(settings.PreservePitch)),
                (SubtitleSizeKey, style.Size.ToString()),
                (SubtitleColorKey, style.Color.ToString()),
                (SubtitleBackgroundKey, style.Background.ToString()),
                (VolumeKey, settings.Volume.ToString("0.###", CultureInfo.InvariantCulture)),
                (MutedKey, Bool(settings.Muted)),
                (OnlineMetadataKey, Bool(settings.OnlineMetadata)),
                (AutoplayKey, Bool(settings.AutoplayNextEpisode)),
                (CountdownKey, settings.NextEpisodeCountdownSeconds.ToString(CultureInfo.InvariantCulture)),
                (IntroSkipKey, settings.IntroSkip.ToString()),
                (RecapSkipKey, settings.RecapSkip.ToString()),
                (SkipCreditsKey, Bool(settings.OfferSkipCredits)),
                (FindSegmentsKey, Bool(settings.FindSegments)),
                (YouTubeAutoplayKey, Bool(settings.YouTubeAutoplay)),
                (YouTubeAcceptedKey, Bool(settings.YouTubeAccepted)),
                (ClearDialogueKey, Bool(settings.SoundDefaults.ClearDialogue)),
                (EvenVolumeKey, Bool(settings.SoundDefaults.EvenVolume)),
                (SeekPreviewsKey, Bool(settings.SeekPreviews)),
            ],
            deletePrefix: null,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, string>> LoadShortcutsAsync(CancellationToken cancellationToken = default)
    {
        var values = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return values
            .Where(p => p.Key.StartsWith(ShortcutPrefix, StringComparison.Ordinal))
            .ToDictionary(p => p.Key[ShortcutPrefix.Length..], p => p.Value, StringComparer.Ordinal);
    }

    public Task SaveShortcutsAsync(IReadOnlyDictionary<string, string> shortcuts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        return WriteAsync(shortcuts.Select(p => (ShortcutPrefix + p.Key, p.Value)).ToList(), deletePrefix: ShortcutPrefix, cancellationToken);
    }

    private async Task<Dictionary<string, string>> ReadAllAsync(CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values;
    }

    private async Task WriteAsync(IReadOnlyList<(string Key, string Value)> rows, string? deletePrefix, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (deletePrefix is not null)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM settings WHERE substr(key, 1, length($prefix)) = $prefix;";
            delete.Parameters.AddWithValue("$prefix", deletePrefix);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var (key, value) in rows)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static bool ReadBool(Dictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var text) && bool.TryParse(text, out var value) ? value : fallback;

    private static int ReadInt(Dictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static double ReadDouble(Dictionary<string, string> values, string key, double fallback) =>
        values.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static T? ReadEnum<T>(Dictionary<string, string> values, string key, T? fallback)
        where T : struct, Enum =>
        values.TryGetValue(key, out var text) && Enum.TryParse<T>(text, ignoreCase: false, out var value) && Enum.IsDefined(value) ? value : fallback;
}

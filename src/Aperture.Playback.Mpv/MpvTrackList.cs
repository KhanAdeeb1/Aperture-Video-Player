using System.Text.Json;
using Aperture.Core.Playback;

namespace Aperture.Playback.Mpv;

/// <summary>Reads libmpv's <c>track-list</c> property (JSON when read as a string) into <see cref="MediaTrack"/> records.</summary>
internal static class MpvTrackList
{
    /// <summary>Parses the JSON array. Entries of unknown type are skipped; malformed JSON gives an empty list.</summary>
    public static IReadOnlyList<MediaTrack> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var tracks = new List<MediaTrack>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                var kind = Text(entry, "type") switch
                {
                    "video" => TrackKind.Video,
                    "audio" => TrackKind.Audio,
                    "sub" => TrackKind.Subtitle,
                    _ => (TrackKind?)null,
                };
                if (kind is null || Int(entry, "id") is not { } id)
                {
                    continue;
                }

                tracks.Add(new MediaTrack(
                    Id: id,
                    Kind: kind.Value,
                    Title: Text(entry, "title"),
                    Language: Text(entry, "lang"),
                    Codec: Text(entry, "codec"),
                    IsSelected: Flag(entry, "selected"),
                    IsDefault: Flag(entry, "default"),
                    IsForced: Flag(entry, "forced"),
                    IsExternal: Flag(entry, "external"),
                    IsImage: Flag(entry, "image") || Flag(entry, "albumart"),
                    Width: (int?)Int(entry, "demux-w"),
                    Height: (int?)Int(entry, "demux-h"),
                    FramesPerSecond: Double(entry, "demux-fps"),
                    Channels: (int?)Int(entry, "demux-channel-count"),
                    SampleRate: (int?)Int(entry, "demux-samplerate"),
                    ExternalFileName: Text(entry, "external-filename")));
            }

            return tracks;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static long? Int(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : null;

    private static double? Double(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}

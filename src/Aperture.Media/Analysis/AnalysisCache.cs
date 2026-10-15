using Aperture.Core.Series;

namespace Aperture.Media.Analysis;

/// <summary>
/// Keeps each episode's measurements (sound fingerprint, dark stretches, chapters) in
/// Aperture's data folder, one small file per content fingerprint, so comparing a new episode
/// with the rest of its season does not decode the others again. Never written next to media.
/// A file that is damaged or from an older format is ignored and measured again.
/// </summary>
public sealed class AnalysisCache(string directory)
{
    private const int Format = 1;
    private const uint Magic = 0x41505331; // "APS1"

    public string Directory { get; } = directory;

    public EpisodeSignals? Load(string fingerprint)
    {
        var path = PathFor(fingerprint);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Format || reader.ReadInt32() != SegmentDetector.Version)
            {
                return null;
            }

            var duration = TimeSpan.FromTicks(reader.ReadInt64());
            AudioPrint? audio = null;
            if (reader.ReadBoolean())
            {
                var start = TimeSpan.FromTicks(reader.ReadInt64());
                var count = reader.ReadInt32();
                if (count < 0 || count > 10_000_000)
                {
                    return null;
                }

                var hashes = new uint[count];
                var quiet = new bool[count];
                for (var i = 0; i < count; i++)
                {
                    hashes[i] = reader.ReadUInt32();
                }

                var bits = reader.ReadBytes((count + 7) / 8);
                for (var i = 0; i < count; i++)
                {
                    quiet[i] = (bits[i / 8] & (1 << (i % 8))) != 0;
                }

                audio = new AudioPrint(hashes, quiet, start);
            }

            var darkFrom = TimeSpan.FromTicks(reader.ReadInt64());
            List<TimeRange>? dark = null;
            if (reader.ReadBoolean())
            {
                var count = reader.ReadInt32();
                dark = new List<TimeRange>(Math.Clamp(count, 0, 10_000));
                for (var i = 0; i < count; i++)
                {
                    dark.Add(new TimeRange(TimeSpan.FromTicks(reader.ReadInt64()), TimeSpan.FromTicks(reader.ReadInt64())));
                }
            }

            var chapterCount = reader.ReadInt32();
            var chapters = new List<ChapterMark>(Math.Clamp(chapterCount, 0, 1000));
            for (var i = 0; i < chapterCount; i++)
            {
                var title = reader.ReadBoolean() ? reader.ReadString() : null;
                chapters.Add(new ChapterMark(title, TimeSpan.FromTicks(reader.ReadInt64())));
            }

            return new EpisodeSignals(fingerprint, duration, audio, darkFrom, dark, chapters);
        }
        catch (Exception error) when (error is IOException or EndOfStreamException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public void Save(EpisodeSignals signals)
    {
        ArgumentNullException.ThrowIfNull(signals);
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(signals.Id);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(Format);
            writer.Write(SegmentDetector.Version);
            writer.Write(signals.Duration.Ticks);
            writer.Write(signals.Audio is not null);
            if (signals.Audio is { } audio)
            {
                writer.Write(audio.Start.Ticks);
                writer.Write(audio.Count);
                foreach (var hash in audio.Hashes)
                {
                    writer.Write(hash);
                }

                var bits = new byte[(audio.Count + 7) / 8];
                for (var i = 0; i < audio.Count; i++)
                {
                    if (audio.Quiet[i])
                    {
                        bits[i / 8] |= (byte)(1 << (i % 8));
                    }
                }

                writer.Write(bits);
            }

            writer.Write(signals.DarkFrom.Ticks);
            writer.Write(signals.Dark is not null);
            if (signals.Dark is { } dark)
            {
                writer.Write(dark.Count);
                foreach (var range in dark)
                {
                    writer.Write(range.Start.Ticks);
                    writer.Write(range.End.Ticks);
                }
            }

            writer.Write(signals.Chapters.Count);
            foreach (var chapter in signals.Chapters)
            {
                writer.Write(chapter.Title is not null);
                if (chapter.Title is { } title)
                {
                    writer.Write(title);
                }

                writer.Write(chapter.Start.Ticks);
            }
        }

        File.Move(temporary, path, overwrite: true);
    }

    private string PathFor(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        // Fingerprints are hex; anything else is refused rather than used in a path.
        if (!fingerprint.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("A fingerprint must be hexadecimal.", nameof(fingerprint));
        }

        return Path.Combine(Directory, fingerprint + ".signals");
    }
}

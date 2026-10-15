using Aperture.Core.Library;
using Aperture.Core.Series;

namespace Aperture.Data.Tests;

public class SqliteSeriesStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 8, 0, 0, TimeSpan.Zero);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static AnalysisRecord Record(string fingerprint, string signature = "a,b") =>
        new(fingerprint, 1, signature, T0, AnalysisOutcome.Done);

    [Fact]
    public async Task Detected_segments_round_trip_and_are_replaced_by_the_next_analysis()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteSeriesStore(temp.Database);
        await store.SaveDetectedAsync(Record("fp1"), [
            new MediaSegment(SegmentKind.Intro, S(62.5), S(112), 0.9, SegmentSource.Detected, "Matched 3 other episodes"),
            new MediaSegment(SegmentKind.Credits, S(1250), S(1300), 0.95, SegmentSource.Chapter, "Chapter “End Credits”"),
        ]);

        var segments = await store.GetSegmentsAsync("fp1");
        var intro = segments.Effective(SegmentKind.Intro)!;
        Assert.Equal(S(62.5), intro.Start);
        Assert.Equal(S(112), intro.End);
        Assert.Equal(0.9, intro.Confidence, 3);
        Assert.Equal("Matched 3 other episodes", intro.Evidence);
        Assert.Equal(SegmentSource.Chapter, segments.Effective(SegmentKind.Credits)!.Source);
        Assert.Equal(EpisodeSegments.None.Detected, (await store.GetSegmentsAsync("other")).Detected);

        await store.SaveDetectedAsync(Record("fp1", "a,b,c"), [new MediaSegment(SegmentKind.Intro, S(60), S(110), 0.92, SegmentSource.Detected)]);
        segments = await store.GetSegmentsAsync("fp1");
        Assert.Null(segments.Effective(SegmentKind.Credits));
        Assert.Equal(S(60), segments.Effective(SegmentKind.Intro)!.Start);
        Assert.Equal("a,b,c", (await store.GetAnalysesAsync())["fp1"].Signature);
    }

    [Fact]
    public async Task Corrections_persist_win_over_detection_and_survive_reanalysis()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteSeriesStore(temp.Database);
        await store.SaveDetectedAsync(Record("fp1"), [new MediaSegment(SegmentKind.Intro, S(60), S(110), 0.7, SegmentSource.Detected)]);
        await store.SaveCorrectionAsync("fp1", new SegmentCorrection(SegmentKind.Intro, S(58), S(104)));
        await store.SaveCorrectionAsync("fp1", new SegmentCorrection(SegmentKind.Recap, null, null));

        // Analysis runs again later (new episodes added): the user's times stay.
        await store.SaveDetectedAsync(Record("fp1", "a,b,c"), [
            new MediaSegment(SegmentKind.Intro, S(61), S(111), 0.9, SegmentSource.Detected),
            new MediaSegment(SegmentKind.Recap, S(0), S(40), 0.9, SegmentSource.Detected),
        ]);

        // A fresh store reads the same file again, as after a restart.
        var segments = await new SqliteSeriesStore(temp.Database).GetSegmentsAsync("fp1");
        var intro = segments.Effective(SegmentKind.Intro)!;
        Assert.Equal(SegmentSource.Manual, intro.Source);
        Assert.Equal(S(58), intro.Start);
        Assert.Equal(S(104), intro.End);
        Assert.Equal(1.0, intro.Confidence);
        Assert.Null(segments.Effective(SegmentKind.Recap));
        Assert.Equal(S(0), segments.DetectedFor(SegmentKind.Recap)!.Start);

        await store.ClearCorrectionAsync("fp1", SegmentKind.Intro);
        Assert.Equal(S(61), (await store.GetSegmentsAsync("fp1")).Effective(SegmentKind.Intro)!.Start);
    }

    [Fact]
    public async Task Corrections_belong_to_one_file_contents_only()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteSeriesStore(temp.Database);
        await store.SaveCorrectionAsync("release-a", new SegmentCorrection(SegmentKind.Intro, S(30), S(80)));

        Assert.NotNull((await store.GetSegmentsAsync("release-a")).Effective(SegmentKind.Intro));
        Assert.Null((await store.GetSegmentsAsync("release-b")).Effective(SegmentKind.Intro));
    }

    [Fact]
    public async Task A_correction_shorter_than_a_second_is_refused()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteSeriesStore(temp.Database);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveCorrectionAsync("fp", new SegmentCorrection(SegmentKind.Intro, S(30), S(30.5))));
    }

    [Fact]
    public async Task Show_preferences_round_trip_and_defaults_are_not_stored()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteSeriesStore(temp.Database);
        Assert.Equal(SeriesPreferences.Default, await store.GetPreferencesAsync("nightshift"));

        var chosen = new SeriesPreferences(Autoplay: false, Intro: SkipMode.Automatic, Recap: null);
        await store.SavePreferencesAsync("nightshift", chosen);
        Assert.Equal(chosen, await store.GetPreferencesAsync("nightshift"));
        Assert.Equal(SeriesPreferences.Default, await store.GetPreferencesAsync("harbourwatch"));

        await store.SavePreferencesAsync("nightshift", SeriesPreferences.Default);
        Assert.Equal(SeriesPreferences.Default, await store.GetPreferencesAsync("nightshift"));
    }

    [Fact]
    public async Task Multi_episode_files_keep_their_last_episode()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var library = new SqliteLibraryStore(temp.Database);
        var folder = await library.AddFolderAsync(@"D:\Shows", LibraryFolderKind.Shows);
        var parsed = MediaNameParser.Parse(@"D:\Shows\Night Shift\Night Shift S01E01E02 Pilot.mkv", @"D:\Shows", LibraryFolderKind.Shows);
        await library.UpsertAsync(folder.Id, [new ScannedFile(@"D:\Shows\Night Shift\Night Shift S01E01E02 Pilot.mkv", 100, T0, parsed)], T0);

        var item = Assert.Single(await library.GetItemsAsync());
        Assert.Equal(1, item.Episode);
        Assert.Equal(2, item.EpisodeEnd);
        Assert.Equal("S1 E1–2 · Pilot", item.Subtitle);

        await library.SaveManualDetailsAsync(item.Id, new ManualDetails(MediaCategory.Episode, "Night Shift", null, 1, 1, "Pilot"));
        Assert.Equal(2, (await library.FindByPathAsync(item.Path))!.EpisodeEnd);
        await library.SaveManualDetailsAsync(item.Id, new ManualDetails(MediaCategory.Episode, "Night Shift", null, 1, 3, "Pilot"));
        Assert.Null((await library.FindByPathAsync(item.Path))!.EpisodeEnd);
    }

    [Fact]
    public async Task A_milestone_4_database_upgrades_and_rereads_multi_episode_names()
    {
        await using var temp = new TempDatabase();
        await using (var connection = await temp.Database.OpenAsync())
        await using (var command = connection.CreateCommand())
        {
            // Migrations 1 and 2 as Milestone 4 shipped them, with two episodes already indexed.
            command.CommandText = ReadMigration("0001_initial.sql") + ReadMigration("0002_library.sql") + """
                INSERT INTO library_folders (id, path, kind, added_utc) VALUES (1, 'D:\Shows', 'Shows', 0);
                INSERT INTO library_items (folder_id, path, size_bytes, modified_utc, added_utc, last_seen_utc, category, title, season, episode, source, confidence)
                VALUES (1, 'D:\Shows\Night Shift S01E01E02.mkv', 10, 500, 0, 0, 'Episode', 'Night Shift', 1, 1, 'FileName', 'Certain'),
                       (1, 'D:\Shows\Night Shift S01E03.mkv', 10, 500, 0, 0, 'Episode', 'Night Shift', 1, 3, 'FileName', 'Certain');
                PRAGMA user_version = 2;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await temp.Database.MigrateAsync();

        Assert.Single(Directory.GetFiles(temp.Folder, "aperture.db.v2.bak"));
        var index = await new SqliteLibraryStore(temp.Database).GetIndexAsync(1);
        Assert.Equal(-1, index.Single(f => f.Path.Contains("E01E02", StringComparison.Ordinal)).ModifiedUtc.ToUnixTimeMilliseconds());
        Assert.Equal(500, index.Single(f => f.Path.Contains("E03", StringComparison.Ordinal)).ModifiedUtc.ToUnixTimeMilliseconds());
    }

    private static string ReadMigration(string name)
    {
        using var stream = typeof(ApertureDatabase).Assembly.GetManifestResourceStream("Aperture.Data.Migrations." + name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

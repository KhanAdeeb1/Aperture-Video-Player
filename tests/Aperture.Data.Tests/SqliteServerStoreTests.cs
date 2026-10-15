using Aperture.Core.Library;
using Aperture.Core.Server;
using Aperture.Core.Settings;

namespace Aperture.Data.Tests;

public class SqliteServerStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Settings_start_at_the_safe_defaults_and_round_trip()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteServerStore(temp.Database);
        Assert.Equal(ServerSettings.Default, await store.LoadSettingsAsync());

        var changed = new ServerSettings(Port: 9443, SetupPort: 9080, AllowVpn: true, KeepAwake: false, FfmpegPath: @"C:\Tools\ffmpeg\bin\ffmpeg.exe", MaxConversions: 2, StartWithWindows: true);
        await store.SaveSettingsAsync(changed);
        Assert.Equal(changed, await store.LoadSettingsAsync());

        await store.SaveSettingsAsync(changed with { FfmpegPath = null });
        Assert.Null((await store.LoadSettingsAsync()).FfmpegPath);
    }

    [Fact]
    public async Task Server_settings_and_the_apps_settings_leave_each_other_alone()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var server = new SqliteServerStore(temp.Database);
        var app = new SqliteSettingsStore(temp.Database);
        await server.SaveSettingsAsync(ServerSettings.Default with { AllowVpn = true });
        await server.SetPassphraseHashAsync("pbkdf2-sha256$1$AAAA$AAAA");

        await app.SaveAsync(AppSettings.Default with { RememberHistory = false });

        Assert.True((await server.LoadSettingsAsync()).AllowVpn);
        Assert.NotNull(await server.GetPassphraseHashAsync());
        Assert.False((await app.LoadAsync()).RememberHistory);
    }

    [Fact]
    public async Task Only_existing_library_folders_can_be_shared_and_removing_a_folder_unshares_it()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteServerStore(temp.Database);
        var library = new SqliteLibraryStore(temp.Database);
        var films = await library.AddFolderAsync(@"D:\Films", LibraryFolderKind.Movies);
        var home = await library.AddFolderAsync(@"D:\Home videos", LibraryFolderKind.Personal);
        Assert.Empty(await store.GetSharedFolderIdsAsync());

        await store.SetFolderSharedAsync(films.Id, true);
        await store.SetFolderSharedAsync(films.Id, true);
        await store.SetFolderSharedAsync(999, true);
        Assert.Equal([films.Id], await store.GetSharedFolderIdsAsync());

        await store.SetFolderSharedAsync(home.Id, true);
        await store.SetFolderSharedAsync(home.Id, false);
        Assert.Equal([films.Id], await store.GetSharedFolderIdsAsync());

        await library.RemoveFolderAsync(films.Id);
        Assert.Empty(await store.GetSharedFolderIdsAsync());
    }

    [Fact]
    public async Task Devices_are_found_by_token_hash_until_they_expire()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteServerStore(temp.Database);
        var token = DeviceToken.Create();
        var device = await store.AddDeviceAsync(DeviceToken.HashOf(token), "iPhone · Safari", T0, T0.AddDays(30), "192.168.1.30");

        var found = await store.FindDeviceAsync(DeviceToken.HashOf(token), T0.AddDays(29));
        Assert.Equal(device.Id, found?.Id);
        Assert.Null(await store.FindDeviceAsync(DeviceToken.HashOf(DeviceToken.Create()), T0));
        Assert.Null(await store.FindDeviceAsync(DeviceToken.HashOf(token), T0.AddDays(30)));

        await store.TouchDeviceAsync(device.Id, T0.AddDays(29), T0.AddDays(59), "192.168.1.31");
        var touched = await store.FindDeviceAsync(DeviceToken.HashOf(token), T0.AddDays(40));
        Assert.Equal("192.168.1.31", touched?.LastAddress);
    }

    [Fact]
    public async Task The_raw_token_is_never_stored()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteServerStore(temp.Database);
        var token = DeviceToken.Create();
        await store.AddDeviceAsync(DeviceToken.HashOf(token), "Pixel · Chrome", T0, T0.AddDays(30), null);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(temp.Folder, "aperture.db"));
        var wal = Path.Combine(temp.Folder, "aperture.db-wal");
        if (File.Exists(wal))
        {
            bytes = [.. bytes, .. await File.ReadAllBytesAsync(wal)];
        }

        Assert.DoesNotContain(token, System.Text.Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_the_passphrase_signs_every_device_out()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteServerStore(temp.Database);
        await store.AddDeviceAsync(DeviceToken.HashOf(DeviceToken.Create()), "A", T0, T0.AddDays(30), null);
        await store.AddDeviceAsync(DeviceToken.HashOf(DeviceToken.Create()), "B", T0, T0.AddDays(30), null);
        Assert.Equal(2, (await store.GetDevicesAsync()).Count);

        await store.SetPassphraseHashAsync(Passphrase.Hash("lantern river 42", 1000));

        Assert.Empty(await store.GetDevicesAsync());
        Assert.True(Passphrase.Verify("lantern river 42", await store.GetPassphraseHashAsync()));
    }

    [Fact]
    public async Task Devices_can_be_signed_out_one_at_a_time_or_all_together()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteServerStore(temp.Database);
        var a = await store.AddDeviceAsync(DeviceToken.HashOf(DeviceToken.Create()), "A", T0, T0.AddDays(30), null);
        await store.AddDeviceAsync(DeviceToken.HashOf(DeviceToken.Create()), "B", T0, T0.AddDays(30), null);

        await store.RemoveDeviceAsync(a.Id);
        Assert.Equal("B", Assert.Single(await store.GetDevicesAsync()).Name);

        await store.RemoveAllDevicesAsync();
        Assert.Empty(await store.GetDevicesAsync());
    }

    [Fact]
    public async Task Expired_devices_are_cleared_when_another_signs_in()
    {
        await using var temp = await TempDatabase.CreateMigratedAsync();
        var store = new SqliteServerStore(temp.Database);
        await store.AddDeviceAsync(DeviceToken.HashOf(DeviceToken.Create()), "Old", T0, T0.AddDays(30), null);

        await store.AddDeviceAsync(DeviceToken.HashOf(DeviceToken.Create()), "New", T0.AddDays(31), T0.AddDays(61), null);

        Assert.Equal("New", Assert.Single(await store.GetDevicesAsync()).Name);
    }
}

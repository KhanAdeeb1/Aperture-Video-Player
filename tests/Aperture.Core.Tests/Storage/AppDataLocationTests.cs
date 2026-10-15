using Aperture.Core.Storage;

namespace Aperture.Core.Tests.Storage;

public sealed class AppDataLocationTests
{
    private static readonly string LocalAppData = Path.Combine(Path.GetTempPath(), "local-app-data");

    [Fact]
    public void Uses_local_app_data_when_no_override_is_set()
    {
        var location = AppDataLocation.Resolve(overrideRoot: null, LocalAppData);

        Assert.Equal(Path.Combine(LocalAppData, "Aperture"), location.Root);
        Assert.Equal(Path.Combine(LocalAppData, "Aperture", "aperture.db"), location.DatabasePath);
        Assert.Equal(Path.Combine(LocalAppData, "Aperture", "Logs"), location.LogsDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ignores_blank_override(string overrideRoot)
    {
        var location = AppDataLocation.Resolve(overrideRoot, LocalAppData);

        Assert.Equal(Path.Combine(LocalAppData, "Aperture"), location.Root);
    }

    [Fact]
    public void Uses_absolute_override_as_is()
    {
        var overrideRoot = Path.Combine(Path.GetTempPath(), "aperture-portable");

        var location = AppDataLocation.Resolve(overrideRoot, LocalAppData);

        Assert.Equal(overrideRoot, location.Root);
    }

    [Fact]
    public void Rejects_relative_override()
    {
        Assert.Throws<ArgumentException>(() => AppDataLocation.Resolve("relative/data", LocalAppData));
    }

    [Fact]
    public void Fails_clearly_when_local_app_data_is_unknown()
    {
        Assert.Throws<InvalidOperationException>(() => AppDataLocation.Resolve(null, string.Empty));
    }

    [Fact]
    public void EnsureCreated_creates_root_and_logs_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "aperture-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var location = AppDataLocation.Resolve(root, LocalAppData);

            location.EnsureCreated();

            Assert.True(Directory.Exists(location.Root));
            Assert.True(Directory.Exists(location.LogsDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

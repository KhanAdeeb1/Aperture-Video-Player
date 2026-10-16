using System.Text.RegularExpressions;
using Aperture.Core.Media;

namespace Aperture.Core.Tests.Packaging;

/// <summary>Keeps the installer's "Open with" registrations in step with the files Aperture plays.</summary>
public sealed partial class InstallerTests
{
    [Fact]
    public void Installer_offers_open_with_for_exactly_the_video_types()
    {
        var script = File.ReadAllLines(Path.Combine(RepositoryRoot(), "installer", "Aperture.iss"));
        var begin = Array.IndexOf(script, "; video-extensions-begin");
        var end = Array.IndexOf(script, "; video-extensions-end");
        Assert.True(begin >= 0 && end > begin, "the extension markers are missing from Aperture.iss");

        var registered = script[(begin + 1)..end]
            .Select(line => ExtensionKey().Match(line))
            .Where(match => match.Success)
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.Equal(end - begin - 1, registered.Count);
        Assert.Equal(MediaFileTypes.VideoExtensions.Order(StringComparer.Ordinal), registered.Order(StringComparer.Ordinal));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aperture.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Aperture.sln was not found above the test folder.");
    }

    [GeneratedRegex(@"^Root: HKCU; Subkey: ""Software\\Classes\\(\.[a-z0-9]+)\\OpenWithProgids""; ValueType: string; ValueName: ""Aperture\.Video""")]
    private static partial Regex ExtensionKey();
}

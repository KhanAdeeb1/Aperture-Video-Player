namespace Aperture.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/>.</summary>
public interface ISettingsStore
{
    /// <summary>Loads settings, falling back to defaults for anything not saved yet.</summary>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Saves settings.</summary>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

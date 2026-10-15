namespace Aperture.Core.Settings;

/// <summary>Stores the customised keyboard shortcuts (see <c>ShortcutMap.ToStorage</c>).</summary>
public interface IShortcutStore
{
    /// <summary>Command name to keys, for customised commands only.</summary>
    Task<IReadOnlyDictionary<string, string>> LoadShortcutsAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces every stored customisation with <paramref name="shortcuts"/>.</summary>
    Task SaveShortcutsAsync(IReadOnlyDictionary<string, string> shortcuts, CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps secrets the user typed in (an online service's API key) out of the database, in
/// the operating system's credential store.
/// </summary>
public interface ISecretStore
{
    /// <summary>The secret, or null when none is stored.</summary>
    string? Read(string name);

    /// <summary>Stores a secret, or deletes it when <paramref name="value"/> is null or empty.</summary>
    void Write(string name, string? value);
}

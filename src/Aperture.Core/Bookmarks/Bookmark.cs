namespace Aperture.Core.Bookmarks;

/// <summary>
/// A moment in a video the user marked, with an optional note. Bookmarks belong to the file's
/// path, like its history; they live only in Aperture's database and never touch the file.
/// </summary>
/// <param name="Id">Database id.</param>
/// <param name="Path">The video.</param>
/// <param name="Position">Where in the video.</param>
/// <param name="Note">The user's note; empty when none was written.</param>
/// <param name="CreatedUtc">When it was made.</param>
public sealed record Bookmark(long Id, string Path, TimeSpan Position, string Note, DateTimeOffset CreatedUtc)
{
    /// <summary>The longest note kept; longer text is cut, so a pasted essay can't bloat the database.</summary>
    public const int MaximumNoteLength = 500;

    /// <summary>Two bookmarks closer than this are the same moment; adding again focuses the existing one.</summary>
    public static readonly TimeSpan SameMoment = TimeSpan.FromSeconds(1);

    /// <summary>Trims a note and limits its length; line breaks become spaces.</summary>
    public static string CleanNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return string.Empty;
        }

        var flat = string.Join(' ', note.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length > MaximumNoteLength ? flat[..MaximumNoteLength].TrimEnd() : flat;
    }

    /// <summary>The bookmark at about <paramref name="position"/>, if one exists.</summary>
    public static Bookmark? Near(IEnumerable<Bookmark> bookmarks, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        return bookmarks
            .Where(b => (b.Position - position).Duration() < SameMoment)
            .OrderBy(b => (b.Position - position).Duration())
            .FirstOrDefault();
    }

    /// <summary>
    /// The bookmark to go to from <paramref name="position"/>: the next one after it, or the
    /// previous one (a bookmark within two seconds behind counts as "here", so pressing the key
    /// twice walks backwards rather than returning to the same mark).
    /// </summary>
    public static Bookmark? Adjacent(IReadOnlyList<Bookmark> bookmarks, TimeSpan position, bool forward)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);
        var ordered = bookmarks.OrderBy(b => b.Position);
        return forward
            ? ordered.FirstOrDefault(b => b.Position > position + SameMoment)
            : ordered.LastOrDefault(b => b.Position < position - TimeSpan.FromSeconds(2));
    }
}

/// <summary>Stores bookmarks in Aperture's database.</summary>
public interface IBookmarkStore
{
    /// <summary>The file's bookmarks, earliest first.</summary>
    Task<IReadOnlyList<Bookmark>> ListAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Adds a bookmark and returns it with its id.</summary>
    Task<Bookmark> AddAsync(string path, TimeSpan position, string note, CancellationToken cancellationToken = default);

    /// <summary>Replaces a bookmark's note.</summary>
    Task UpdateNoteAsync(long id, string note, CancellationToken cancellationToken = default);

    /// <summary>Deletes a bookmark.</summary>
    Task DeleteAsync(long id, CancellationToken cancellationToken = default);
}

using Aperture.Core.Bookmarks;

namespace Aperture.Data;

/// <summary>Bookmarks and notes in the <c>bookmarks</c> table.</summary>
public sealed class SqliteBookmarkStore(ApertureDatabase database, TimeProvider? clock = null) : IBookmarkStore
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<IReadOnlyList<Bookmark>> ListAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, path, position_ms, note, created_utc FROM bookmarks WHERE path = $path ORDER BY position_ms, id;";
        command.Parameters.AddWithValue("$path", path);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<Bookmark>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new Bookmark(
                reader.GetInt64(0),
                reader.GetString(1),
                TimeSpan.FromMilliseconds(Math.Max(0, reader.GetInt64(2))),
                reader.GetString(3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
        }

        return result;
    }

    public async Task<Bookmark> AddAsync(string path, TimeSpan position, string note, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var clean = Bookmark.CleanNote(note);
        var at = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        var created = _clock.GetUtcNow();
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO bookmarks (path, position_ms, note, created_utc) VALUES ($path, $position, $note, $created);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$position", (long)Math.Round(at.TotalMilliseconds));
        command.Parameters.AddWithValue("$note", clean);
        command.Parameters.AddWithValue("$created", created.ToUnixTimeMilliseconds());
        var id = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new Bookmark(id, path, TimeSpan.FromMilliseconds(Math.Round(at.TotalMilliseconds)), clean, DateTimeOffset.FromUnixTimeMilliseconds(created.ToUnixTimeMilliseconds()));
    }

    public async Task UpdateNoteAsync(long id, string note, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE bookmarks SET note = $note WHERE id = $id;";
        command.Parameters.AddWithValue("$note", Bookmark.CleanNote(note));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM bookmarks WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

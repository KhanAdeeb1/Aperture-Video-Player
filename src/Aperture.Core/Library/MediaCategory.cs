namespace Aperture.Core.Library;

/// <summary>What a library item is. Stored by name; do not rename.</summary>
public enum MediaCategory
{
    Movie,
    Episode,
    Personal,
    Podcast,
}

/// <summary>What the user said a library folder holds. Stored by name; do not rename.</summary>
public enum LibraryFolderKind
{
    /// <summary>Let Aperture decide per file from its name.</summary>
    Mixed,
    Movies,
    Shows,
    Personal,
    Podcasts,
}

/// <summary>Names for categories and folder kinds as Aperture shows them.</summary>
public static class MediaCategories
{
    /// <summary>Categories in the order the library lists them.</summary>
    public static IReadOnlyList<MediaCategory> All { get; } = [MediaCategory.Movie, MediaCategory.Episode, MediaCategory.Personal, MediaCategory.Podcast];

    /// <summary>Folder kinds in the order the "Add folder" choice lists them.</summary>
    public static IReadOnlyList<LibraryFolderKind> FolderKinds { get; } =
        [LibraryFolderKind.Mixed, LibraryFolderKind.Movies, LibraryFolderKind.Shows, LibraryFolderKind.Personal, LibraryFolderKind.Podcasts];

    /// <summary>"Movies", "TV shows", "Personal videos", "Podcasts".</summary>
    public static string PluralLabel(MediaCategory category) => category switch
    {
        MediaCategory.Movie => "Movies",
        MediaCategory.Episode => "TV shows",
        MediaCategory.Personal => "Personal videos",
        MediaCategory.Podcast => "Podcasts",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    /// <summary>"Movie", "TV episode", "Personal video", "Podcast episode".</summary>
    public static string SingularLabel(MediaCategory category) => category switch
    {
        MediaCategory.Movie => "Movie",
        MediaCategory.Episode => "TV episode",
        MediaCategory.Personal => "Personal video",
        MediaCategory.Podcast => "Podcast episode",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    /// <summary>The label for a folder kind.</summary>
    public static string Label(LibraryFolderKind kind) => kind switch
    {
        LibraryFolderKind.Mixed => "A mix (decide from file names)",
        LibraryFolderKind.Movies => "Movies",
        LibraryFolderKind.Shows => "TV shows",
        LibraryFolderKind.Personal => "Personal videos",
        LibraryFolderKind.Podcasts => "Podcasts",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

namespace Aperture.Core.Playback;

/// <summary>One labelled value in the playback information panel.</summary>
public sealed record DiagnosticItem(string Label, string Value);

/// <summary>A group of related values ("Video", "Audio").</summary>
public sealed record DiagnosticSection(string Title, IReadOnlyList<DiagnosticItem> Items);

/// <summary>
/// What the engine reports about the open file and how it is being played, read at one
/// moment. Only values the engine actually reported appear; missing ones are left out.
/// </summary>
public sealed record PlaybackDiagnostics(IReadOnlyList<DiagnosticSection> Sections)
{
    /// <summary>Nothing to report (no file open).</summary>
    public static PlaybackDiagnostics Empty { get; } = new([]);

    /// <summary>Plain text, one "Label: value" per line, for logs and copying.</summary>
    public string ToText()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var section in Sections)
        {
            builder.Append(section.Title).AppendLine();
            foreach (var item in section.Items)
            {
                builder.Append("  ").Append(item.Label).Append(": ").Append(item.Value).AppendLine();
            }
        }

        return builder.ToString();
    }
}

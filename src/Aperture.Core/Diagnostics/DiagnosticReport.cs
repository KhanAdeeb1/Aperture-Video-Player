using System.Text;
using System.Text.RegularExpressions;

namespace Aperture.Core.Diagnostics;

/// <summary>
/// A plain-text report for a bug report or a support question: what Aperture and the PC are,
/// what went wrong, and the end of Aperture's log. It is only ever copied to the clipboard by
/// the user; nothing is sent anywhere. File and folder paths and the Windows user name are
/// removed first, so the names of someone's videos don't travel with it.
/// </summary>
public static partial class DiagnosticReport
{
    /// <summary>Log lines included from the end of the log.</summary>
    public const int LogLines = 60;

    /// <summary>
    /// Builds the report. <paramref name="facts"/> are (label, value) lines such as the app and
    /// engine versions; <paramref name="problem"/> describes the failure being reported, if any.
    /// </summary>
    public static string Build(IEnumerable<(string Label, string? Value)> facts, string? problem, IEnumerable<string> logTail, string? userName)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(logTail);
        var text = new StringBuilder();
        text.AppendLine("Aperture diagnostic report");
        text.AppendLine("(file and folder names removed)");
        text.AppendLine();
        foreach (var (label, value) in facts)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                text.Append(label).Append(": ").AppendLine(value.Trim());
            }
        }

        if (!string.IsNullOrWhiteSpace(problem))
        {
            text.AppendLine();
            text.AppendLine("Problem");
            text.AppendLine(problem.Trim());
        }

        var lines = logTail.TakeLast(LogLines).ToList();
        if (lines.Count > 0)
        {
            text.AppendLine();
            text.Append("Last ").Append(lines.Count).AppendLine(" log lines");
            foreach (var line in lines)
            {
                text.AppendLine(line);
            }
        }

        return Redact(text.ToString(), userName);
    }

    /// <summary>
    /// Replaces file paths (drive letters, network shares, Unix-style paths of two or more parts)
    /// with "&lt;path&gt;", keeping only the file's extension, and the user name with "&lt;user&gt;".
    /// </summary>
    public static string Redact(string text, string? userName)
    {
        ArgumentNullException.ThrowIfNull(text);
        var redacted = PathPattern().Replace(text, match =>
        {
            var extension = Path.GetExtension(match.Value.TrimEnd('.', ',', ';', ':', ')'));
            return extension.Length is > 1 and <= 6 && extension.Skip(1).All(char.IsLetterOrDigit) ? $"<path>{extension}" : "<path>";
        });
        if (!string.IsNullOrWhiteSpace(userName) && userName.Length >= 3)
        {
            redacted = redacted.Replace(userName, "<user>", StringComparison.OrdinalIgnoreCase);
        }

        return redacted;
    }

    // C:\…, \\server\share\…, and /a/b… up to the end of the line or a quote. Paths may hold
    // spaces, so a Windows path runs until a quote, a line end or " | " (the log's separator).
    [GeneratedRegex(@"(?:[A-Za-z]:\\|\\\\[^\\\s]+\\)(?:[^""'\r\n|<>]*[^""'\r\n|<>\s])?|(?<![\w.])/(?:[\w.-]+/)+[\w.-]*", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();
}

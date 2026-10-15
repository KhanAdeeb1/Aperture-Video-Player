using System.Text.RegularExpressions;
using Aperture.Core.Library;
using Aperture.Core.Media;

namespace Aperture.Media;

/// <summary>
/// Scans one library folder: finds media files, reads their names, records them, then
/// inspects new and changed files for length, picture size and a content fingerprint.
/// <para>
/// Safe to cancel at any point: everything found so far is already saved, unchanged files
/// are skipped on the next scan, and files are only marked missing after a scan that saw
/// the whole folder. It never follows links out of the folder, never opens files for
/// writing, and never asks a cloud provider to download files that are not on this computer.
/// </para>
/// </summary>
public sealed partial class LibraryScanner(ILibraryStore store, IMediaProbe? probe, TimeProvider clock, Action<string, string> log)
{
    /// <summary>Files written to the database per transaction.</summary>
    public const int BatchSize = 250;

    // Cloud placeholders (OneDrive "online-only" and similar). Reading them downloads them.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private static readonly long SampleSizeLimit = 300L * 1024 * 1024;

    private static readonly EnumerationOptions Enumeration = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    /// <summary>Scans <paramref name="folder"/>. Throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    public async Task<ScanSummary> ScanAsync(LibraryFolder folder, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder.Path))
        {
            // An unplugged drive or a disconnected share: keep every record as it is.
            log("warn", "A library folder is not available right now; its items were kept.");
            return new ScanSummary(Completed: false, 0, 0, 0, 1, FolderUnavailable: true);
        }

        var started = clock.GetUtcNow();
        await store.MarkScanStartedAsync(folder.Id, started, cancellationToken).ConfigureAwait(false);
        var index = (await store.GetIndexAsync(folder.Id, cancellationToken).ConfigureAwait(false))
            .ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        var upserts = new List<ScannedFile>(BatchSize);
        var touches = new List<long>(BatchSize);
        int found = 0, added = 0, updated = 0, problems = 0;

        async Task FlushAsync()
        {
            await store.UpsertAsync(folder.Id, upserts, started, cancellationToken).ConfigureAwait(false);
            await store.TouchAsync(touches, started, cancellationToken).ConfigureAwait(false);
            upserts.Clear();
            touches.Clear();
        }

        foreach (var file in Walk(folder.Path, () => problems++, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCandidate(file, folder))
            {
                continue;
            }

            long size;
            DateTimeOffset modified;
            try
            {
                file.Refresh();
                if ((file.Attributes & (RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline)) != 0)
                {
                    continue;
                }

                size = file.Length;
                modified = DateTimeOffset.FromUnixTimeMilliseconds(new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds());
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                problems++;
                continue;
            }

            if (IsSample(file, size))
            {
                continue;
            }

            found++;
            if (index.TryGetValue(file.FullName, out var known) && known.SizeBytes == size && known.ModifiedUtc == modified)
            {
                touches.Add(known.Id);
            }
            else
            {
                upserts.Add(new ScannedFile(file.FullName, size, modified, MediaNameParser.Parse(file.FullName, folder.Path, folder.Kind)));
                if (known is null)
                {
                    added++;
                }
                else
                {
                    updated++;
                }
            }

            if (upserts.Count + touches.Count >= BatchSize)
            {
                await FlushAsync().ConfigureAwait(false);
            }

            if (found % 50 == 0)
            {
                progress?.Report(new ScanProgress(folder.Id, ScanPhase.Finding, found, null, added));
            }
        }

        await FlushAsync().ConfigureAwait(false);
        progress?.Report(new ScanProgress(folder.Id, ScanPhase.Finding, found, found, added));

        // Only now, having seen the whole folder, is it safe to say what has gone.
        var missing = await store.MarkMissingAsync(folder.Id, started, cancellationToken).ConfigureAwait(false);

        await InspectAsync(folder, progress, added, cancellationToken).ConfigureAwait(false);
        await store.MarkScanCompletedAsync(folder.Id, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        log("info", $"Library scan finished: {found} files, {added} new, {updated} changed, {missing} missing, {problems} unreadable");
        return new ScanSummary(Completed: true, added, updated, missing, problems);
    }

    private async Task InspectAsync(LibraryFolder folder, IProgress<ScanProgress>? progress, int added, CancellationToken cancellationToken)
    {
        var pending = await store.GetUnprobedAsync(folder.Id, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < pending.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress(folder.Id, ScanPhase.Inspecting, i, pending.Count, added));
            var file = pending[i];
            MediaProbeResult? result = null;
            if (probe is not null)
            {
                try
                {
                    result = await probe.ProbeAsync(file.Path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    log("warn", $"Could not inspect a library file: {error.Message}");
                }
            }

            var fingerprint = await Task.Run(() => FileFingerprint.Compute(file.Path), cancellationToken).ConfigureAwait(false);
            await store.SaveProbeAsync(file.Id, result, fingerprint, cancellationToken).ConfigureAwait(false);
        }

        if (pending.Count > 0)
        {
            progress?.Report(new ScanProgress(folder.Id, ScanPhase.Inspecting, pending.Count, pending.Count, added));
        }
    }

    /// <summary>
    /// Every file under <paramref name="root"/>, depth first. Hidden and system entries are
    /// skipped, and so are links (symbolic links and junctions), so a scan never leaves the
    /// folder or loops. Unreadable folders are counted and skipped.
    /// </summary>
    internal static IEnumerable<FileInfo> Walk(string root, Action onProblem, CancellationToken cancellationToken)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            FileInfo[] files;
            DirectoryInfo[] children;
            try
            {
                files = directory.GetFiles("*", Enumeration);
                children = directory.GetDirectories("*", Enumeration);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                onProblem();
                continue;
            }

            foreach (var file in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                yield return file;
            }

            foreach (var child in children.OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (child.LinkTarget is not null)
                {
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    /// <summary>Video files, plus audio files where podcasts are expected.</summary>
    internal static bool IsCandidate(FileInfo file, LibraryFolder folder)
    {
        if (MediaFileTypes.IsVideoFile(file.Name))
        {
            return true;
        }

        if (!MediaFileTypes.IsAudioFile(file.Name))
        {
            return false;
        }

        return folder.Kind == LibraryFolderKind.Podcasts
            || (folder.Kind == LibraryFolderKind.Mixed && file.FullName.Contains("podcast", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Release "sample" clips that sit next to the real film, or in a "Sample" folder beside it.</summary>
    internal static bool IsSample(FileInfo file, long size) =>
        size < SampleSizeLimit
        && (SamplePattern().IsMatch(file.Name)
            || string.Equals(file.Directory?.Name, "sample", StringComparison.OrdinalIgnoreCase)
            || string.Equals(file.Directory?.Name, "samples", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?<![a-z])sample(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex SamplePattern();
}

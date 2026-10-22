using System.Runtime.Versioning;
using System.Security;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.Core.Services;

public sealed record DeleteOutcome(string Path, bool Success, string? Error);

/// <summary>
/// Scans direct child folders of a given root and reports their total size, sorted
/// largest-first — a text/table alternative to visual treemap tools (WinDirStat and
/// similar), which rely on a colored rectangle layout that a screen reader can't read.
/// One level at a time by design: scan, read the list, drill into whichever folder is
/// actually the problem, scan again — rather than one giant recursive tree to navigate.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiskSpaceAnalyzerService
{
    /// <summary>
    /// Scans the direct child folders of <paramref name="rootPath"/> and returns them
    /// sorted by size, descending. Reports structured progress (how many folders have
    /// been measured out of how many total, and which one is current) through
    /// <paramref name="progress"/> so the UI can show a real determinate progress bar
    /// and accurate "X of Y" status, not just an indeterminate spinner.
    /// </summary>
    public async Task<IReadOnlyList<FolderSizeEntry>> ScanAsync(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException(L.F("Folder not found: {0}", rootPath));

        var results = new List<FolderSizeEntry>();

        string[] childDirs;
        try
        {
            childDirs = Directory.GetDirectories(rootPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            childDirs = Array.Empty<string>();
        }

        for (var i = 0; i < childDirs.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dir = childDirs[i];
            var name = Path.GetFileName(dir);
            progress?.Report(new ScanProgress(i, childDirs.Length, name));

            var (size, approximate) = await Task.Run(() => GetDirectorySize(dir, cancellationToken), cancellationToken);

            results.Add(new FolderSizeEntry
            {
                FullPath = dir,
                Name = name,
                SizeBytes = size,
                SizeIsApproximate = approximate,
            });
        }

        progress?.Report(new ScanProgress(childDirs.Length, childDirs.Length, L.T("Finishing up")));

        // Also report loose files directly in this folder (not in a subfolder) as one combined entry,
        // since otherwise large files sitting loose at this level would be invisible to the scan.
        try
        {
            var looseFilesSize = Directory.GetFiles(rootPath).Sum(f =>
            {
                try { return new FileInfo(f).Length; }
                catch { return 0L; }
            });

            if (looseFilesSize > 0)
            {
                results.Add(new FolderSizeEntry
                {
                    FullPath = rootPath,
                    Name = "(files directly in this folder)",
                    SizeBytes = looseFilesSize,
                });
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            // Skip loose-file accounting if we can't even list this level.
        }

        return results.OrderByDescending(r => r.SizeBytes).ToList();
    }

    /// <summary>
    /// Deletes each given folder or file to the Recycle Bin (never a permanent delete)
    /// so a multi-select mistake is always recoverable. Returns one outcome per path so
    /// the caller can report exactly which of a multi-select batch succeeded or failed.
    /// Uses the Shell's own SHFileOperation API directly (rather than the
    /// Microsoft.VisualBasic helper) so this stays in Core without pulling in the
    /// Windows Desktop framework, which would stop Core from being buildable
    /// cross-platform.
    /// </summary>
    public async Task<IReadOnlyList<DeleteOutcome>> DeleteToRecycleBinAsync(IEnumerable<string> paths)
    {
        var outcomes = new List<DeleteOutcome>();

        foreach (var path in paths)
        {
            var outcome = await Task.Run(() =>
            {
                try
                {
                    if (!Directory.Exists(path) && !File.Exists(path))
                        return new DeleteOutcome(path, true, L.T("Already gone."));

                    var result = ShellRecycleBin.SendToRecycleBin(path);
                    return result.Success
                        ? new DeleteOutcome(path, true, null)
                        : new DeleteOutcome(path, false, result.Error);
                }
                catch (Exception ex)
                {
                    return new DeleteOutcome(path, false, ex.Message);
                }
            });

            outcomes.Add(outcome);
        }

        return outcomes;
    }

    /// <summary>
    /// Manually walks the folder tree (rather than Directory.EnumerateFiles with
    /// SearchOption.AllDirectories) because that lazy enumeration throws and aborts
    /// the *entire* scan the moment it hits a single protected subfolder anywhere in
    /// the tree — a near-certainty on a real Windows drive (e.g. Windows Defender's
    /// own folders). Walking directory-by-directory lets us skip just the one folder
    /// we can't read, flag the total as approximate, and keep going.
    /// </summary>
    private static (long SizeBytes, bool Approximate) GetDirectorySize(string path, CancellationToken cancellationToken)
    {
        long total = 0;
        var approximate = false;
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
            {
                approximate = true; // Can't read this folder's files (permissions/locked) - skip it, don't abort the whole scan.
                files = Array.Empty<string>();
            }

            foreach (var file in files)
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    approximate = true; // Locked or permission-denied file: keep going, flag the total as a lower bound.
                }
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
            {
                approximate = true; // Can't even list this folder's subfolders - skip its subtree, keep going with the rest.
                subDirs = Array.Empty<string>();
            }

            foreach (var subDir in subDirs)
            {
                // Never recurse into a symlink/junction: it can point back to an ancestor
                // (an infinite loop) or to an entirely different drive/network location
                // (wildly wrong, double-counted size). Skipping it means its own content
                // isn't included here, matching how disk usage tools generally treat
                // reparse points - the total becomes a safe lower bound, not a crash risk.
                if (IsReparsePoint(subDir))
                {
                    approximate = true;
                    continue;
                }

                pending.Push(subDir);
            }
        }

        return (total, approximate);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false; // Can't stat it - let the normal per-folder error handling deal with it instead.
        }
    }
}

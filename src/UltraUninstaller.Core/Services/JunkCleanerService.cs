using System.Runtime.Versioning;
using System.Security;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.Core.Services;

/// <summary>One thing Junk Cleaner can target: either a folder (clean its contents, keep the folder itself) or a single specific file.</summary>
internal sealed record JunkTarget(string Path, bool IsFolder);

/// <summary>
/// Scans and cleans a fixed, deliberately short list of well-documented, safe-to-
/// clear locations: temp folders, browser caches, the Windows Update download
/// cache, the thumbnail cache, the Delivery Optimization cache, the Windows Error
/// Reporting queue, and the Recycle Bin. Every category here is something the
/// owning system (Windows, or the browser) already knows how to rebuild on its
/// own - nothing is flagged on a guess. This is the same philosophy as Registry
/// Cleaner: a short list of things that are unambiguously safe beats a long list
/// that inflates a "problems found" number with things that don't matter.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JunkCleanerService
{
    private static readonly string SystemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private sealed record CategoryDefinition(JunkCategoryKind Kind, string DisplayName, string Description, Func<IReadOnlyList<JunkTarget>> ResolveTargets);

    private static readonly List<CategoryDefinition> Categories = new()
    {
        new CategoryDefinition(JunkCategoryKind.UserTempFiles, L.T("Your Temp folder"),
            L.T("Temporary files programs create for themselves and are supposed to clean up but often don't. Safe to remove - anything still needed gets recreated automatically."),
            () => ResolveFolderTargets(Path.GetTempPath())),

        new CategoryDefinition(JunkCategoryKind.WindowsTempFiles, L.T("Windows Temp folder"),
            L.T("The system-wide equivalent of your own Temp folder, used by installers, Windows Update, and background system tasks. Same rule: safe to clear, gets recreated as needed."),
            () => ResolveFolderTargets(Path.Combine(SystemRoot, "Temp"))),

        new CategoryDefinition(JunkCategoryKind.WindowsUpdateCache, L.T("Windows Update download cache"),
            L.T("Installer files Windows Update already applied and no longer needs. If Windows needs the same update again, it simply re-downloads it."),
            () => ResolveFolderTargets(Path.Combine(SystemRoot, "SoftwareDistribution", "Download"))),

        new CategoryDefinition(JunkCategoryKind.BrowserCaches, L.T("Browser caches"),
            L.T("Cached web page data (images, scripts, style sheets) your browsers keep to load sites faster. Clearing it never removes bookmarks, saved passwords, or history - the browser just rebuilds the cache as you browse."),
            ResolveBrowserCacheTargets),

        new CategoryDefinition(JunkCategoryKind.ThumbnailCache, L.T("Thumbnail and icon cache"),
            L.T("Cached preview images Windows Explorer builds so folders open faster. Removing it is invisible except that thumbnails briefly regenerate the next time you browse those folders."),
            ResolveThumbnailCacheTargets),

        new CategoryDefinition(JunkCategoryKind.DeliveryOptimizationCache, L.T("Delivery Optimization cache"),
            L.T("Windows Update/Store files Windows keeps around so it can potentially share them with other PCs on your network or the internet (peer-to-peer updates). Safe to clear; Windows rebuilds it as needed."),
            () => ResolveFolderTargets(Path.Combine(SystemRoot, "SoftwareDistribution", "DeliveryOptimization", "Cache"))),

        new CategoryDefinition(JunkCategoryKind.WindowsErrorReportingQueue, L.T("Windows Error Reporting queue"),
            L.T("Crash/error report data Windows collected to optionally send to Microsoft, sitting in a local queue. Safe to clear - it's diagnostic data, not something any program depends on to run."),
            () => new[]
            {
                Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"),
                Path.Combine(ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"),
            }.SelectMany(ResolveFolderTargets).ToList()),
    };

    /// <summary>
    /// Scans every category and returns only the ones that actually found
    /// something (an empty category is just noise, not a result worth showing -
    /// the same reasoning behind not padding Registry Cleaner's count).
    /// </summary>
    public Task<IReadOnlyList<JunkCategoryResult>> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var results = new List<JunkCategoryResult>();

            foreach (var def in Categories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(L.F("Checking {0}", def.DisplayName));

                var targets = def.ResolveTargets();
                var (size, count, approximate) = MeasureTargets(targets, cancellationToken);

                if (size > 0 || count > 0)
                {
                    results.Add(new JunkCategoryResult
                    {
                        Kind = def.Kind,
                        DisplayName = def.DisplayName,
                        Description = def.Description,
                        TargetPaths = targets.Select(t => t.Path).ToList(),
                        SizeBytes = size,
                        FileCount = count,
                        SizeIsApproximate = approximate,
                    });
                }
            }

            progress?.Report(L.T("Checking Recycle Bin"));
            var (rbOk, rbSize, rbItems) = ShellRecycleBin.QueryRecycleBin();
            if (rbOk && (rbSize > 0 || rbItems > 0))
            {
                results.Add(new JunkCategoryResult
                {
                    Kind = JunkCategoryKind.RecycleBin,
                    DisplayName = L.T("Recycle Bin"),
                    Description = L.T("Files you've already deleted, still sitting here in case you want them back. Emptying it is permanent - there's no further undo after this."),
                    TargetPaths = Array.Empty<string>(),
                    SizeBytes = rbSize,
                    FileCount = (int)rbItems,
                });
            }

            return (IReadOnlyList<JunkCategoryResult>)results;
        }, cancellationToken);
    }

    /// <summary>
    /// Cleans each given category. Folder targets have their contents removed but
    /// the folder itself is kept (some locations, like %TEMP%, are expected by
    /// Windows/programs to always exist). File targets (thumbnail cache database
    /// files) are deleted directly. A locked file anywhere is skipped, not treated
    /// as a failure of the whole category - the rest of what could be removed
    /// still is.
    /// </summary>
    public async Task<IReadOnlyList<JunkCleanOutcome>> CleanAsync(IEnumerable<JunkCategoryResult> categories, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var outcomes = new List<JunkCleanOutcome>();

        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(L.F("Cleaning {0}", category.DisplayName));

            if (category.Kind == JunkCategoryKind.RecycleBin)
            {
                var (ok, error) = await Task.Run(ShellRecycleBin.EmptyRecycleBin, cancellationToken);
                outcomes.Add(ok
                    ? new JunkCleanOutcome(category.Kind, category.DisplayName, category.FileCount, category.SizeBytes, 0, null)
                    : new JunkCleanOutcome(category.Kind, category.DisplayName, 0, 0, 0, error));
                continue;
            }

            // Re-resolve targets fresh rather than trusting the paths captured at scan
            // time - browser profiles or temp contents can change between scan and clean.
            var def = Categories.First(c => c.Kind == category.Kind);
            var targets = def.ResolveTargets();

            var deleted = 0;
            long bytes = 0;
            var skipped = 0;

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var (d, b, s) = await Task.Run(() => target.IsFolder
                    ? DeleteFolderContents(target.Path, cancellationToken)
                    : DeleteSingleFile(target.Path), cancellationToken);

                deleted += d;
                bytes += b;
                skipped += s;
            }

            outcomes.Add(new JunkCleanOutcome(category.Kind, category.DisplayName, deleted, bytes, skipped, null));
        }

        return outcomes;
    }

    private static IReadOnlyList<JunkTarget> ResolveFolderTargets(string folderPath) =>
        Directory.Exists(folderPath) ? new[] { new JunkTarget(folderPath, IsFolder: true) } : Array.Empty<JunkTarget>();

    /// <summary>
    /// Chromium-family browsers (Chrome, Edge, Brave) each store cache per profile
    /// under "User Data\&lt;Profile&gt;\Cache" (or "Cache2" on newer versions);
    /// Firefox stores it per profile under "Profiles\&lt;profile&gt;\cache2". This
    /// walks every profile folder found rather than assuming just "Default", since
    /// people commonly have several browser profiles.
    /// </summary>
    private static IReadOnlyList<JunkTarget> ResolveBrowserCacheTargets()
    {
        var targets = new List<JunkTarget>();

        void AddChromiumProfiles(string userDataRoot)
        {
            if (!Directory.Exists(userDataRoot))
                return;

            try
            {
                foreach (var profileDir in Directory.GetDirectories(userDataRoot))
                {
                    foreach (var cacheFolderName in new[] { "Cache", "Cache2", Path.Combine("Code Cache", "js") })
                    {
                        var cachePath = Path.Combine(profileDir, cacheFolderName);
                        if (Directory.Exists(cachePath))
                            targets.Add(new JunkTarget(cachePath, IsFolder: true));
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
            {
                // Can't enumerate this browser's profiles - skip it, don't fail the whole scan.
            }
        }

        AddChromiumProfiles(Path.Combine(LocalAppData, "Google", "Chrome", "User Data"));
        AddChromiumProfiles(Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data"));
        AddChromiumProfiles(Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data"));

        var firefoxProfilesRoot = Path.Combine(LocalAppData, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefoxProfilesRoot))
        {
            try
            {
                foreach (var profileDir in Directory.GetDirectories(firefoxProfilesRoot))
                {
                    var cache2 = Path.Combine(profileDir, "cache2");
                    if (Directory.Exists(cache2))
                        targets.Add(new JunkTarget(cache2, IsFolder: true));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
            {
                // Can't enumerate Firefox profiles - skip it.
            }
        }

        return targets;
    }

    private static IReadOnlyList<JunkTarget> ResolveThumbnailCacheTargets()
    {
        var explorerFolder = Path.Combine(LocalAppData, "Microsoft", "Windows", "Explorer");
        var targets = new List<JunkTarget>();

        if (!Directory.Exists(explorerFolder))
            return targets;

        foreach (var pattern in new[] { "thumbcache_*.db", "iconcache_*.db" })
        {
            try
            {
                foreach (var file in Directory.GetFiles(explorerFolder, pattern))
                    targets.Add(new JunkTarget(file, IsFolder: false));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
            {
                // Can't list this pattern - skip it.
            }
        }

        return targets;
    }

    /// <summary>Same folder-walking approach as DiskSpaceAnalyzerService: skip what can't be read rather than aborting the whole measurement, and never follow a reparse point.</summary>
    private static (long SizeBytes, int FileCount, bool Approximate) MeasureTargets(IReadOnlyList<JunkTarget> targets, CancellationToken cancellationToken)
    {
        long totalBytes = 0;
        var totalCount = 0;
        var approximate = false;

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!target.IsFolder)
            {
                try
                {
                    totalBytes += new FileInfo(target.Path).Length;
                    totalCount++;
                }
                catch
                {
                    approximate = true;
                }
                continue;
            }

            var (size, count, folderApproximate) = MeasureFolder(target.Path, cancellationToken);
            totalBytes += size;
            totalCount += count;
            approximate |= folderApproximate;
        }

        return (totalBytes, totalCount, approximate);
    }

    private static (long SizeBytes, int FileCount, bool Approximate) MeasureFolder(string path, CancellationToken cancellationToken)
    {
        long total = 0;
        var count = 0;
        var approximate = false;
        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            string[] files;
            try { files = Directory.GetFiles(current); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
            {
                approximate = true;
                files = Array.Empty<string>();
            }

            foreach (var file in files)
            {
                try
                {
                    total += new FileInfo(file).Length;
                    count++;
                }
                catch
                {
                    approximate = true;
                }
            }

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(current); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
            {
                approximate = true;
                subDirs = Array.Empty<string>();
            }

            foreach (var subDir in subDirs)
            {
                if (IsReparsePoint(subDir))
                {
                    approximate = true;
                    continue;
                }

                pending.Push(subDir);
            }
        }

        return (total, count, approximate);
    }

    /// <summary>
    /// Recursively deletes everything inside a folder but leaves the folder itself
    /// standing. A subfolder that can't be fully emptied (e.g. one locked file
    /// inside it) is left in place rather than aborting - whatever could be
    /// removed from it already was, and it's counted in FilesSkipped, not treated
    /// as an overall failure.
    /// </summary>
    private static (int Deleted, long Bytes, int Skipped) DeleteFolderContents(string folderPath, CancellationToken cancellationToken)
    {
        var deleted = 0;
        long bytes = 0;
        var skipped = 0;

        string[] entries;
        try { entries = Directory.GetFileSystemEntries(folderPath); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            return (0, 0, 0); // Can't even list this folder - nothing to report.
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (Directory.Exists(entry))
                {
                    if (IsReparsePoint(entry))
                    {
                        skipped++;
                        continue; // Never follow/delete through a reparse point - could point outside the intended cache folder entirely.
                    }

                    var (d, b, s) = DeleteFolderContents(entry, cancellationToken);
                    deleted += d;
                    bytes += b;
                    skipped += s;

                    try { Directory.Delete(entry); }
                    catch { /* Not empty (something inside was locked) or otherwise locked - leave it, its contents were already handled above. */ }
                }
                else
                {
                    var (d, b, s) = DeleteSingleFile(entry);
                    deleted += d;
                    bytes += b;
                    skipped += s;
                }
            }
            catch
            {
                skipped++;
            }
        }

        return (deleted, bytes, skipped);
    }

    private static (int Deleted, long Bytes, int Skipped) DeleteSingleFile(string filePath)
    {
        try
        {
            var length = new FileInfo(filePath).Length;

            // Clear read-only/hidden/system attributes that would otherwise block a
            // plain delete - common on cache files browsers mark read-only while idle.
            try { File.SetAttributes(filePath, FileAttributes.Normal); }
            catch { /* Couldn't reset attributes - the delete below will fail on its own terms if that's fatal. */ }

            File.Delete(filePath);
            return (1, length, 0);
        }
        catch
        {
            return (0, 0, 1); // Locked/in use/access denied right now - skip it, don't fail the whole category over one file.
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }
}

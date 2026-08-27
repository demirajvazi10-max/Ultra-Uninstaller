namespace UltraUninstaller.Core.Models;

public enum JunkCategoryKind
{
    /// <summary>The current user's own %TEMP% folder.</summary>
    UserTempFiles,

    /// <summary>The system-wide C:\Windows\Temp folder.</summary>
    WindowsTempFiles,

    /// <summary>Update installer files Windows Update has already applied.</summary>
    WindowsUpdateCache,

    /// <summary>Cached web content from installed Chromium/Firefox-family browsers.</summary>
    BrowserCaches,

    /// <summary>Windows Explorer's thumbnail/icon cache database files.</summary>
    ThumbnailCache,

    /// <summary>Windows' peer-to-peer Delivery Optimization cache for Update/Store downloads.</summary>
    DeliveryOptimizationCache,

    /// <summary>Queued crash/error report data collected by Windows Error Reporting.</summary>
    WindowsErrorReportingQueue,

    /// <summary>The Recycle Bin - handled specially (no filesystem scan, emptied via the Shell API).</summary>
    RecycleBin,
}

/// <summary>
/// One scanned junk category with everything needed to explain it to the user and
/// to clean exactly what was scanned. Deliberately a short, fixed list of
/// well-documented, safe-to-clear locations - not a generic "scan everywhere for
/// anything that looks unused" sweep, for the same reason Registry Cleaner stays
/// narrow: guessing wrong at what's safe to delete is how junk cleaners earn their
/// bad reputation.
/// </summary>
public sealed class JunkCategoryResult
{
    public required JunkCategoryKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }

    /// <summary>The folder(s)/file(s) this category resolved to on this machine, shown in details.</summary>
    public required IReadOnlyList<string> TargetPaths { get; init; }

    public long SizeBytes { get; init; }
    public int FileCount { get; init; }

    /// <summary>True if at least one file or folder within this category couldn't be measured (locked/access denied) - the size shown is a lower bound.</summary>
    public bool SizeIsApproximate { get; init; }

    public double SizeMb => SizeBytes / 1024.0 / 1024.0;
    public double SizeGb => SizeBytes / 1024.0 / 1024.0 / 1024.0;

    public override string ToString() => DisplayName;
}

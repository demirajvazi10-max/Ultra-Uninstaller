namespace UltraUninstaller.Core.Models;

/// <summary>
/// Result of cleaning one junk category. FilesSkipped counts files that were
/// locked/access-denied at the moment of cleaning (e.g. a browser cache file the
/// browser currently has open) - never treated as a failure of the whole category,
/// since the rest of what could be removed still was.
/// </summary>
public sealed record JunkCleanOutcome(
    JunkCategoryKind Kind,
    string DisplayName,
    int FilesDeleted,
    long BytesFreed,
    int FilesSkipped,
    string? Error);

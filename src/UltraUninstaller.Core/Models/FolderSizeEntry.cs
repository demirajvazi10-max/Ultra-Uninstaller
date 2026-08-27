namespace UltraUninstaller.Core.Models;

/// <summary>
/// One row in a Disk Space Analyzer scan: a direct child folder of the scanned root,
/// with its total recursive size. Deliberately flat (one level at a time) rather than
/// a full recursive tree, so a screen reader user drills down folder by folder instead
/// of being handed a giant nested structure to navigate.
/// </summary>
public sealed class FolderSizeEntry
{
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>True if the scan couldn't read some part of this folder (permissions, locked files) — size is a lower bound.</summary>
    public bool SizeIsApproximate { get; init; }

    public double SizeGb => SizeBytes / 1024.0 / 1024.0 / 1024.0;
    public double SizeMb => SizeBytes / 1024.0 / 1024.0;
}

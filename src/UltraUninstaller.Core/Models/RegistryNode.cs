namespace UltraUninstaller.Core.Models;

public enum RegistryNodeKind
{
    SubKey,
    Value,
}

/// <summary>
/// One row shown when browsing a registry path - either a subkey (like a folder) or
/// a value (like a file). Deliberately read-only beyond delete: this browser lets the
/// user go anywhere and remove anything they choose, same as the Disk Space Analyzer
/// does for folders, but it does not support editing value data - that's a much
/// higher-risk operation than delete-with-backup and is out of scope.
/// </summary>
public sealed class RegistryNode
{
    public required RegistryNodeKind Kind { get; init; }
    public required string Name { get; init; }

    /// <summary>Full path to this node (including the value name, for values) - used for display and as the delete/backup target.</summary>
    public required string FullPath { get; init; }

    /// <summary>For subkeys: how many child subkeys and values it contains, as an at-a-glance sense of size (like a folder's byte count).</summary>
    public int? ChildSubKeyCount { get; init; }
    public int? ChildValueCount { get; init; }

    /// <summary>For values: the REG_* type (e.g. "String", "DWord").</summary>
    public string? ValueKindDisplay { get; init; }

    /// <summary>For values: a short, truncated preview of the data - never the full data for large/binary values.</summary>
    public string? DataPreview { get; init; }
}

namespace UltraUninstaller.Core.Models;

/// <summary>Which layer of the Force Delete pipeline actually succeeded.</summary>
public enum ForceDeleteLayer
{
    /// <summary>Normal Recycle Bin delete worked on the first try - no force needed.</summary>
    RecycleBin,

    /// <summary>Detaching an orphaned Windows image (WinPE/WIM/VHD) mount point unlocked the folder.</summary>
    ImageMountCleanup,

    /// <summary>Taking ownership and resetting permissions unlocked the folder.</summary>
    OwnershipReset,

    /// <summary>A permanent delete (bypassing the Recycle Bin) succeeded where a normal delete didn't.</summary>
    PermanentDelete,

    /// <summary>Nothing could remove it immediately; deletion was scheduled for the next restart.</summary>
    ScheduledOnReboot,

    Failed
}

public sealed class ForceDeleteResult
{
    public required bool Success { get; init; }
    public required ForceDeleteLayer LayerUsed { get; init; }
    public required IReadOnlyList<OperationStep> Steps { get; init; }
    public string? FailureReason { get; init; }
}

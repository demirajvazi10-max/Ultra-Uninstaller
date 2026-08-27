namespace UltraUninstaller.Core.Models;

/// <summary>
/// Which layer of the Force Uninstall pipeline actually succeeded (or was attempted).
/// Exposed to the UI so the user always knows what happened, not just "done".
/// </summary>
public enum UninstallLayer
{
    /// <summary>Native quiet/silent uninstall string, unmodified.</summary>
    NativeSilent,

    /// <summary>msiexec /x {ProductCode} /quiet — used for MSI packages.</summary>
    MsiQuiet,

    /// <summary>Native uninstall string with a silent switch we appended ourselves (e.g. /S, /VERYSILENT).</summary>
    NativeWithInferredSilentSwitch,

    /// <summary>Manual removal: files, registry key, shortcuts, AppData remnants.</summary>
    ForceRemoval,

    /// <summary>Nothing worked.</summary>
    Failed
}

public enum UninstallStepStatus
{
    Started,
    Succeeded,
    Skipped,
    Failed
}

/// <summary>
/// A single reported step, meant to be spoken/announced as it happens (not just logged),
/// since the whole point of this tool is that the process must be followable without
/// looking at a progress bar.
/// </summary>
public sealed class UninstallStep
{
    public required string Description { get; init; }
    public required UninstallStepStatus Status { get; init; }
    public string? Detail { get; init; }
}

public sealed class UninstallResult
{
    public required bool Success { get; init; }
    public required UninstallLayer LayerUsed { get; init; }
    public required IReadOnlyList<UninstallStep> Steps { get; init; }
    public string? FailureReason { get; init; }
    public string? BackupPath { get; init; }
}

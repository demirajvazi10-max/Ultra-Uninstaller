namespace UltraUninstaller.Core.Models;

public enum OperationStepStatus
{
    Started,
    Succeeded,
    Skipped,
    Failed
}

/// <summary>
/// A single reported step in a multi-layer force operation, meant to be
/// spoken/announced as it happens - the same pattern already used by the Force
/// Uninstall pipeline (<see cref="UninstallStep"/>), generalized here so other
/// "try increasingly aggressive things" pipelines (like Force Delete) can reuse it.
/// </summary>
public sealed class OperationStep
{
    public required string Description { get; init; }
    public required OperationStepStatus Status { get; init; }
    public string? Detail { get; init; }
}

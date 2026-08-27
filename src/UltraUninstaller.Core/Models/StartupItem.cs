namespace UltraUninstaller.Core.Models;

public enum StartupSource
{
    RegistryRun,
    StartupFolder,
    ScheduledTask,
    AutoStartService,
}

/// <summary>
/// One thing that runs automatically at startup or logon, from any of the four
/// sources UltraUninstaller checks - most basic "startup manager" tools only show
/// registry Run entries; this also covers the Startup folder, logon-triggered
/// Scheduled Tasks, and services set to start automatically, since any of those can
/// be the real cause of a slow boot.
/// </summary>
public sealed class StartupItem
{
    public required StartupSource Source { get; init; }
    public required string Name { get; init; }

    /// <summary>Friendly description read from the executable's own version info, when available (e.g. "Realtek Audio Manager" instead of a cryptic filename).</summary>
    public string? Description { get; init; }

    public string? Publisher { get; init; }
    public string? FilePath { get; init; }

    /// <summary>True if the file has an embedded Authenticode signature. Null if this couldn't be determined (e.g. no resolvable file path).</summary>
    public bool? IsSigned { get; init; }

    /// <summary>Flagged true when the file sits in a location commonly used to hide unwanted startup items (Temp, Downloads) - a heads-up, not an accusation.</summary>
    public bool IsInUnusualLocation { get; init; }

    public required bool IsEnabled { get; init; }

    /// <summary>Everything needed by the service to toggle this specific item - meaning varies by Source (registry value name, task path, service key name, shortcut file path).</summary>
    public required string Identifier { get; init; }

    /// <summary>For RegistryRun items: which registry scope the Run value lives in.</summary>
    public RegistryScope? Scope { get; init; }

    public override string ToString() => Name;
}

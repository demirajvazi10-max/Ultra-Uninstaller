namespace UltraUninstaller.Core.Models;

/// <summary>
/// The registry hive/view an uninstall entry was found under. We keep this because
/// the same key path can exist in three different places (64-bit, 32-bit-on-64-bit,
/// and per-user), and we need to know exactly which one to write back to / delete from.
/// </summary>
public enum RegistryScope
{
    LocalMachine64,
    LocalMachine32OnWow64,
    CurrentUser
}

/// <summary>
/// Best-guess classification of what kind of installer technology produced this
/// uninstall entry. Used to pick a silent uninstall command line when the app
/// itself doesn't expose one via QuietUninstallString.
/// </summary>
public enum InstallerKind
{
    Unknown,
    Msi,
    InnoSetup,
    NsisSetup,
    InstallShield,
    Squirrel,      // common for Electron apps (e.g. many chat/creator tools)
    Msix
}

/// <summary>
/// One entry read from HKLM/HKCU ...\Uninstall. This is the thing shown in the
/// app list and passed into the uninstall engine.
/// </summary>
public sealed class InstalledApp
{
    /// <summary>Registry key name, e.g. "{GUID}" for MSI apps or a vendor-chosen string for others.</summary>
    public required string RegistryKeyName { get; init; }

    public required RegistryScope Scope { get; init; }

    public required string DisplayName { get; init; }

    public string? DisplayVersion { get; init; }

    public string? Publisher { get; init; }

    public string? InstallLocation { get; init; }

    /// <summary>Raw UninstallString value exactly as stored in the registry.</summary>
    public string? UninstallString { get; init; }

    /// <summary>QuietUninstallString, when the installer was well-behaved enough to provide one.</summary>
    public string? QuietUninstallString { get; init; }

    /// <summary>Size in KB as reported by the installer (EstimatedSize), if present.</summary>
    public long? EstimatedSizeKb { get; init; }

    /// <summary>Raw InstallDate string as stored (usually yyyyMMdd), if present.</summary>
    public string? InstallDate { get; init; }

    /// <summary>True if the registry key name is a GUID, which almost always means MSI.</summary>
    public bool LooksLikeMsi { get; init; }

    /// <summary>Full path to this entry's registry key, for use by BackupService / ForceRemovalService.</summary>
    public required string FullRegistryPath { get; init; }

    public InstallerKind DetectedInstallerKind { get; set; } = InstallerKind.Unknown;

    public override string ToString() => DisplayName;
}

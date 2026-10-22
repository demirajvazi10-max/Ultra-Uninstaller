namespace UltraUninstaller.Core.Models;

public enum RegistryIssueCategory
{
    /// <summary>An Uninstall entry whose own uninstaller .exe no longer exists on disk - the program was removed by hand, leaving Windows still thinking it's installed.</summary>
    OrphanedUninstallEntry,

    /// <summary>A "Run at startup" entry pointing at a program that no longer exists.</summary>
    OrphanedStartupEntry,

    /// <summary>A structural persistence/hijack technique: Image File Execution Options Debugger redirection, Winlogon Shell/Userinit tampering, or a non-empty AppInit_DLLs. Well-documented techniques with near-zero legitimate consumer use.</summary>
    SecurityReview,

    /// <summary>A Run-at-startup command containing a known "living off the land" pattern (encoded PowerShell, mshta/regsvr32/certutil fetching from a URL) - a real technique, not a guess, but still just a pattern match, so always framed as "review", never "this is malware".</summary>
    SuspiciousStartupCommand,
}

/// <summary>
/// One flagged registry issue, with everything needed to both explain it to the user
/// and to remove exactly that one thing - never more than what was actually flagged.
/// </summary>
public sealed class RegistryIssue
{
    public required RegistryIssueCategory Category { get; init; }
    public required string DisplayName { get; init; }
    public required string Reason { get; init; }
    public required RegistryScope Scope { get; init; }

    /// <summary>Full path including hive name, e.g. HKEY_CURRENT_USER\...\Run - used for the pre-delete backup export and shown to the user.</summary>
    public required string FullRegistryPath { get; init; }

    /// <summary>Relative subkey path (no hive prefix) containing either the subkey or the value to delete.</summary>
    public required string ParentKeyPath { get; init; }

    /// <summary>Set for OrphanedUninstallEntry - the subkey name to delete entirely under ParentKeyPath.</summary>
    public string? SubKeyNameToDelete { get; init; }

    /// <summary>Set when the fix is a value under ParentKeyPath - either deleted (if CorrectedValueData is null) or reset to CorrectedValueData (if set).</summary>
    public string? ValueNameToDelete { get; init; }

    /// <summary>
    /// When set, "removing" this issue means resetting the value to this known-safe
    /// default instead of deleting it outright - used for Winlogon Shell/Userinit
    /// (which Windows expects to always have a value) and AppInit_DLLs (expected to
    /// exist but empty). Null means the normal case: delete the value/key entirely.
    /// </summary>
    public object? CorrectedValueData { get; init; }

    /// <summary>
    /// The raw data that made this an issue (the Debugger path, the startup command, the
    /// missing file...). Used so an "ignored" item comes back if that data ever changes -
    /// ignoring today's Defender Control entry must not hide a different program later.
    /// </summary>
    public string? Evidence { get; init; }

    /// <summary>True when the user chose "Ignore this item" for exactly this entry and evidence.</summary>
    public bool IsIgnored { get; set; }

    /// <summary>Stable identity of the entry, independent of the UI language.</summary>
    public string IgnoreKey => $"{Category}|{FullRegistryPath}|{SubKeyNameToDelete ?? ValueNameToDelete}";

    public override string ToString() => DisplayName;
}

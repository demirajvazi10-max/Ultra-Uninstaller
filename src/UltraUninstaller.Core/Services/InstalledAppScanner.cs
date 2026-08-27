using System.Runtime.Versioning;
using Microsoft.Win32;
using UltraUninstaller.Core.Models;

namespace UltraUninstaller.Core.Services;

/// <summary>
/// Reads the standard Windows "Add/Remove Programs" registry locations directly,
/// instead of relying on any GUI (Programs and Features, or a vendor's own installer
/// window) which may itself be inaccessible. This is the same data those tools use;
/// we just read it as plain text instead of rendering it as an unreadable list box.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstalledAppScanner
{
    private static readonly (RegistryHive Hive, string SubKey, RegistryScope Scope, RegistryView View)[] Locations =
    {
        (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryScope.LocalMachine64, RegistryView.Registry64),
        (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", RegistryScope.LocalMachine32OnWow64, RegistryView.Registry32),
        (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", RegistryScope.CurrentUser, RegistryView.Registry64),
    };

    /// <summary>
    /// Scans all locations and returns every entry that has a visible DisplayName
    /// and is not flagged as a hidden system component (SystemComponent=1), matching
    /// what Programs and Features itself would show.
    /// </summary>
    public IReadOnlyList<InstalledApp> ScanInstalledApps()
    {
        var results = new List<InstalledApp>();

        foreach (var (hive, subKeyPath, scope, view) in Locations)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(subKeyPath);
            if (uninstallKey is null)
                continue;

            foreach (var keyName in uninstallKey.GetSubKeyNames())
            {
                using var entryKey = uninstallKey.OpenSubKey(keyName);
                if (entryKey is null)
                    continue;

                var app = TryReadEntry(entryKey, keyName, scope, hive, subKeyPath);
                if (app is not null)
                    results.Add(app);
            }
        }

        return results
            .OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Finds installed apps whose DisplayName contains the given text (case-insensitive).
    /// This is the entry point used for "find CapCut" style lookups.
    /// </summary>
    public IReadOnlyList<InstalledApp> FindByName(string searchText)
    {
        return ScanInstalledApps()
            .Where(a => a.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static InstalledApp? TryReadEntry(RegistryKey entryKey, string keyName, RegistryScope scope, RegistryHive hive, string subKeyPath)
    {
        var displayName = entryKey.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(displayName))
            return null; // Not a real, user-facing entry (e.g. an update/patch sub-record).

        var systemComponent = entryKey.GetValue("SystemComponent") as int?;
        if (systemComponent == 1)
            return null; // Hidden component, not something a user would ever try to uninstall.

        var releaseType = entryKey.GetValue("ReleaseType") as string;
        if (string.Equals(releaseType, "Hotfix", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(releaseType, "Update", StringComparison.OrdinalIgnoreCase))
            return null;

        var parentKeyName = entryKey.GetValue("ParentKeyName") as string;
        if (!string.IsNullOrEmpty(parentKeyName))
            return null; // This is a sub-patch of another product, not a standalone app.

        long? estimatedSizeKb = entryKey.GetValue("EstimatedSize") is int sizeKb ? sizeKb : null;

        var hiveName = hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";
        var fullPath = $@"{hiveName}\{subKeyPath}\{keyName}";

        var looksLikeMsi = keyName.StartsWith("{") && keyName.EndsWith("}") && Guid.TryParse(keyName, out _);

        return new InstalledApp
        {
            RegistryKeyName = keyName,
            Scope = scope,
            DisplayName = displayName,
            DisplayVersion = entryKey.GetValue("DisplayVersion") as string,
            Publisher = entryKey.GetValue("Publisher") as string,
            InstallLocation = entryKey.GetValue("InstallLocation") as string,
            UninstallString = entryKey.GetValue("UninstallString") as string,
            QuietUninstallString = entryKey.GetValue("QuietUninstallString") as string,
            EstimatedSizeKb = estimatedSizeKb,
            InstallDate = entryKey.GetValue("InstallDate") as string,
            LooksLikeMsi = looksLikeMsi,
            FullRegistryPath = fullPath,
        };
    }
}

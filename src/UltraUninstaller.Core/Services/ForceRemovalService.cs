using System.Runtime.Versioning;
using Microsoft.Win32;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.Core.Services;

/// <summary>
/// The last-resort layer: used only when neither the app's own quiet uninstall
/// string nor a silent switch we inferred actually removed it. Manually deletes
/// the install folder, the registry Uninstall entry, Start Menu / Desktop
/// shortcuts, and known AppData/LocalAppData/ProgramData remnants.
///
/// This is only ever called after BackupService has made a restore point and/or
/// folder backup — this class assumes that already happened and does not create
/// backups itself, to keep the two responsibilities separate.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ForceRemovalService
{
    /// <summary>
    /// Deletes the install folder itself, if we know where it is.
    /// </summary>
    public bool RemoveInstallFolder(InstalledApp app, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(app.InstallLocation) || !Directory.Exists(app.InstallLocation))
            return true; // Nothing to do — not a failure.

        try
        {
            Directory.Delete(app.InstallLocation, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Deletes the app's own key under the Uninstall registry path, so it stops
    /// showing up in Programs and Features / UltraUninstaller's own list.
    /// </summary>
    public bool RemoveUninstallRegistryEntry(InstalledApp app, out string? error)
    {
        error = null;
        try
        {
            var (baseKey, subKeyPath) = ResolveBaseKeyAndUninstallSubPath(app);
            using var uninstallKey = baseKey.OpenSubKey(subKeyPath, writable: true);
            uninstallKey?.DeleteSubKeyTree(app.RegistryKeyName, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Removes Start Menu and Desktop shortcuts that reference the app by name.
    /// Matches by the app's display name against the .lnk file name, which is how
    /// nearly every installer names its shortcuts.
    /// </summary>
    public IReadOnlyList<string> RemoveShortcuts(InstalledApp app)
    {
        var removed = new List<string>();
        var searchRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        foreach (var root in searchRoots.Where(Directory.Exists))
        {
            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileNameWithoutExtension(f).Contains(app.DisplayName, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                continue; // Skip roots we can't enumerate (permissions), don't fail the whole operation.
            }

            foreach (var shortcut in matches)
            {
                try
                {
                    File.Delete(shortcut);
                    removed.Add(shortcut);
                }
                catch
                {
                    // Leftover shortcut is cosmetic, not worth failing the uninstall over.
                }
            }
        }

        return removed;
    }

    /// <summary>
    /// Removes known leftover-data folders under AppData\Roaming, AppData\Local and
    /// ProgramData whose folder name matches the app's display name or publisher.
    /// Returns the list of folders it removed, so the caller can report exactly
    /// what was cleaned up rather than a vague "done".
    /// </summary>
    public IReadOnlyList<string> RemoveAppDataRemnants(InstalledApp app)
    {
        var removed = new List<string>();
        var candidateRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),      // Roaming
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), // Local
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),// ProgramData
        };

        var nameCandidates = new[] { app.DisplayName, app.Publisher }
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in candidateRoots.Where(Directory.Exists))
        {
            foreach (var name in nameCandidates)
            {
                var candidate = Path.Combine(root, name);
                if (!Directory.Exists(candidate))
                    continue;

                try
                {
                    Directory.Delete(candidate, recursive: true);
                    removed.Add(candidate);
                }
                catch
                {
                    // A locked/leftover data folder shouldn't abort the whole force-removal.
                }
            }
        }

        return removed;
    }

    private static (RegistryKey BaseKey, string SubKeyPath) ResolveBaseKeyAndUninstallSubPath(InstalledApp app)
    {
        const string subPath64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string subPathWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        return app.Scope switch
        {
            RegistryScope.LocalMachine64 => (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), subPath64),
            RegistryScope.LocalMachine32OnWow64 => (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), subPathWow),
            RegistryScope.CurrentUser => (RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64), subPath64),
            _ => throw new ArgumentOutOfRangeException(nameof(app), L.T("Unknown registry scope.")),
        };
    }
}

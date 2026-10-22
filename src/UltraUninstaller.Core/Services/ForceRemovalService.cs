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

        // Some installers write a bogus InstallLocation such as "C:\Program Files" or a
        // drive root. Deleting that recursively would destroy the system, so refuse.
        if (PathSafety.IsProtected(app.InstallLocation))
        {
            error = L.F("Skipped: {0} is a system or shared folder, not this program's own folder.", app.InstallLocation);
            return false;
        }

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
                // Only shortcuts that clearly belong to this program: its exact name, its
                // "Uninstall <name>" entry, or anything pointing into its install folder.
                // (Earlier versions removed every shortcut whose name merely CONTAINED the
                // program name, so a program called e.g. "Go" took unrelated shortcuts with it.)
                matches = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories)
                    .Where(f => ShortcutBelongsTo(f, app))
                    .ToList();
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

        // Only folders named after the program itself (also inside its publisher's folder).
        // Earlier versions also deleted a folder named after the PUBLISHER - with a
        // publisher like "Mozilla" or "Microsoft" that meant wiping other programs' data
        // (Firefox profiles, Office settings...). Never again.
        var displayName = SafeFolderName(app.DisplayName);
        var publisher = SafeFolderName(app.Publisher);
        if (displayName is null)
            return removed;

        foreach (var root in candidateRoots.Where(Directory.Exists))
        {
            var candidates = new List<string> { Path.Combine(root, displayName) };
            if (publisher is not null)
                candidates.Add(Path.Combine(root, publisher, displayName));

            foreach (var candidate in candidates)
            {
                if (!Directory.Exists(candidate) || PathSafety.IsProtected(candidate))
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

    private static bool ShortcutBelongsTo(string shortcutPath, InstalledApp app)
    {
        var name = Path.GetFileNameWithoutExtension(shortcutPath);
        if (string.Equals(name, app.DisplayName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "Uninstall " + app.DisplayName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.IsNullOrWhiteSpace(app.InstallLocation) || PathSafety.IsProtected(app.InstallLocation))
            return false;

        // Cheap pre-filter before resolving the shortcut (which needs COM).
        if (!name.Contains(app.DisplayName, StringComparison.OrdinalIgnoreCase) &&
            (app.Publisher is null || !shortcutPath.Contains(app.Publisher, StringComparison.OrdinalIgnoreCase)))
            return false;

        var target = ShortcutHelper.TryResolveTarget(shortcutPath);
        return target is not null && PathSafety.IsSameOrInside(target, app.InstallLocation!);
    }

    /// <summary>A single, plain folder name - or null if the text is empty or could escape the intended root.</summary>
    private static string? SafeFolderName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();
        if (trimmed.Length < 3 || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed is "." or "..")
            return null;

        return trimmed;
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

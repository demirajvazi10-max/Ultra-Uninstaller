using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.Core.Services;

public sealed record RegistryDeleteOutcome(bool Success, string? Error, string? BackupPath);

/// <summary>
/// Lets the user navigate anywhere in the registry, one level at a time, and delete
/// exactly what they choose - the same principle as the Disk Space Analyzer applied
/// to the registry: the tool never decides what's "safe" to remove on its own here,
/// the user does, and every deletion is backed up first.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryBrowserService
{
    private readonly BackupService _backup;

    private static readonly Dictionary<string, RegistryHive> HiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HKEY_CURRENT_USER"] = RegistryHive.CurrentUser,
        ["HKCU"] = RegistryHive.CurrentUser,
        ["HKEY_LOCAL_MACHINE"] = RegistryHive.LocalMachine,
        ["HKLM"] = RegistryHive.LocalMachine,
        ["HKEY_CLASSES_ROOT"] = RegistryHive.ClassesRoot,
        ["HKCR"] = RegistryHive.ClassesRoot,
        ["HKEY_USERS"] = RegistryHive.Users,
        ["HKU"] = RegistryHive.Users,
        ["HKEY_CURRENT_CONFIG"] = RegistryHive.CurrentConfig,
        ["HKCC"] = RegistryHive.CurrentConfig,
    };

    /// <summary>The set of root hives offered as starting points, in their canonical display form.</summary>
    public static IReadOnlyList<string> RootHiveNames { get; } = new[]
    {
        "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE", "HKEY_CLASSES_ROOT", "HKEY_USERS", "HKEY_CURRENT_CONFIG",
    };

    public RegistryBrowserService(BackupService? backup = null)
    {
        _backup = backup ?? new BackupService();
    }

    /// <summary>Parses "HKEY_CURRENT_USER\Software\Foo" (or "HKCU\Software\Foo") into a hive and the remaining subpath.</summary>
    public static bool TryParsePath(string fullPath, out RegistryHive hive, out string subPath)
    {
        hive = default;
        subPath = string.Empty;

        var trimmed = fullPath.Trim().Trim('\\');
        if (trimmed.Length == 0)
            return false;

        var separatorIndex = trimmed.IndexOf('\\');
        var hiveToken = separatorIndex < 0 ? trimmed : trimmed[..separatorIndex];

        if (!HiveNames.TryGetValue(hiveToken, out hive))
            return false;

        subPath = separatorIndex < 0 ? string.Empty : trimmed[(separatorIndex + 1)..];
        return true;
    }

    /// <summary>Lists the subkeys and values directly under the given path (one level, like a folder listing).</summary>
    public (IReadOnlyList<RegistryNode> SubKeys, IReadOnlyList<RegistryNode> Values) ListChildren(string fullPath)
    {
        if (!TryParsePath(fullPath, out var hive, out var subPath))
            throw new ArgumentException(L.F("Not a recognized registry path: {0}", fullPath));

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = string.IsNullOrEmpty(subPath) ? baseKey : baseKey.OpenSubKey(subPath);

        if (key is null)
            throw new IOException(L.F("Registry path not found or not accessible: {0}", fullPath));

        var subKeys = new List<RegistryNode>();
        foreach (var name in key.GetSubKeyNames())
        {
            int? subCount = null, valueCount = null;
            try
            {
                using var child = key.OpenSubKey(name);
                if (child is not null)
                {
                    subCount = child.SubKeyCount;
                    valueCount = child.ValueCount;
                }
            }
            catch
            {
                // Leave counts null if we can't peek inside (permissions) - the row still shows, just without a size hint.
            }

            subKeys.Add(new RegistryNode
            {
                Kind = RegistryNodeKind.SubKey,
                Name = name,
                FullPath = CombinePath(fullPath, name),
                ChildSubKeyCount = subCount,
                ChildValueCount = valueCount,
            });
        }

        var values = new List<RegistryNode>();
        foreach (var valueName in key.GetValueNames())
        {
            string kindDisplay;
            string preview;
            try
            {
                var kind = key.GetValueKind(valueName);
                kindDisplay = kind.ToString();
                var data = key.GetValue(valueName);
                preview = FormatPreview(data);
            }
            catch
            {
                kindDisplay = "Unknown";
                preview = "(unreadable)";
            }

            values.Add(new RegistryNode
            {
                Kind = RegistryNodeKind.Value,
                Name = string.IsNullOrEmpty(valueName) ? "(Default)" : valueName,
                FullPath = CombinePath(fullPath, valueName),
                ValueKindDisplay = kindDisplay,
                DataPreview = preview,
            });
        }

        return (
            subKeys.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<RegistryDeleteOutcome> DeleteSubKeyAsync(string parentPath, string subKeyName)
    {
        var fullTargetPath = CombinePath(parentPath, subKeyName);
        var backupOutcome = await _backup.ExportRegistryKeyAsync(fullTargetPath, subKeyName);

        try
        {
            if (!TryParsePath(parentPath, out var hive, out var subPath))
                return new RegistryDeleteOutcome(false, L.T("Could not parse the registry path."), backupOutcome.Path);

            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var parentKey = string.IsNullOrEmpty(subPath) ? baseKey : baseKey.OpenSubKey(subPath, writable: true);

            if (parentKey is null)
                return new RegistryDeleteOutcome(false, L.T("Parent key no longer exists."), backupOutcome.Path);

            parentKey.DeleteSubKeyTree(subKeyName, throwOnMissingSubKey: false);
            return new RegistryDeleteOutcome(true, null, backupOutcome.Path);
        }
        catch (Exception ex)
        {
            return new RegistryDeleteOutcome(false, ex.Message, backupOutcome.Path);
        }
    }

    public async Task<RegistryDeleteOutcome> DeleteValueAsync(string parentPath, string valueName)
    {
        // reg.exe export only works on whole keys, not a single value - back up the containing key.
        var backupOutcome = await _backup.ExportRegistryKeyAsync(parentPath, $"{valueName}_parent");

        try
        {
            if (!TryParsePath(parentPath, out var hive, out var subPath))
                return new RegistryDeleteOutcome(false, L.T("Could not parse the registry path."), backupOutcome.Path);

            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var parentKey = string.IsNullOrEmpty(subPath) ? baseKey : baseKey.OpenSubKey(subPath, writable: true);

            if (parentKey is null)
                return new RegistryDeleteOutcome(false, L.T("Parent key no longer exists."), backupOutcome.Path);

            var actualValueName = valueName == "(Default)" ? string.Empty : valueName;
            parentKey.DeleteValue(actualValueName, throwOnMissingValue: false);
            return new RegistryDeleteOutcome(true, null, backupOutcome.Path);
        }
        catch (Exception ex)
        {
            return new RegistryDeleteOutcome(false, ex.Message, backupOutcome.Path);
        }
    }

    /// <summary>
    /// For a subkey a normal delete couldn't touch (access denied): grants the
    /// current Windows user Full Control on that key's permissions, then retries.
    /// This resets an overly-restrictive ACL - it does not perform a full Windows
    /// "take ownership" privilege escalation, so a key whose owner actively refuses
    /// even a permission change may still not budge; that's a deliberate boundary,
    /// not an oversight - full ownership takeover is a stronger, riskier operation
    /// this version doesn't attempt.
    /// </summary>
    public async Task<RegistryDeleteOutcome> ForceDeleteSubKeyAsync(string parentPath, string subKeyName)
    {
        var fullTargetPath = CombinePath(parentPath, subKeyName);

        try
        {
            if (!TryParsePath(parentPath, out var hive, out var subPath))
                return new RegistryDeleteOutcome(false, L.T("Could not parse the registry path."), null);

            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var parentKey = string.IsNullOrEmpty(subPath) ? baseKey : baseKey.OpenSubKey(subPath, writable: true);

            if (parentKey is null)
                return new RegistryDeleteOutcome(false, L.T("Parent key no longer exists."), null);

            using (var targetKey = parentKey.OpenSubKey(subKeyName, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions))
            {
                if (targetKey is not null)
                {
                    var security = targetKey.GetAccessControl();
                    var currentUser = WindowsIdentity.GetCurrent().User;
                    if (currentUser is not null)
                    {
                        security.AddAccessRule(new RegistryAccessRule(
                            currentUser, RegistryRights.FullControl, InheritanceFlags.ContainerInherit,
                            PropagationFlags.None, AccessControlType.Allow));
                        targetKey.SetAccessControl(security);
                    }
                }
            }
        }
        catch
        {
            // Couldn't even adjust permissions (still access denied at that level, or the key
            // doesn't allow WRITE_DAC without full ownership) - fall through and try the plain
            // delete anyway in case the permission issue was actually somewhere deeper in the tree.
        }

        return await DeleteSubKeyAsync(parentPath, subKeyName);
    }

    private static string CombinePath(string parent, string child) =>
        parent.TrimEnd('\\') + '\\' + child;

    private static string FormatPreview(object? data)
    {
        if (data is null)
            return string.Empty;

        var text = data switch
        {
            byte[] bytes => $"({bytes.Length} bytes)",
            string[] arr => string.Join(", ", arr),
            _ => data.ToString() ?? string.Empty,
        };

        const int maxLength = 120;
        return text.Length > maxLength ? text[..maxLength] + "..." : text;
    }
}

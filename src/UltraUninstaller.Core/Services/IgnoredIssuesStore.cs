using System.Runtime.Versioning;
using Microsoft.Win32;
using UltraUninstaller.Core.Models;

namespace UltraUninstaller.Core.Services;

/// <summary>
/// Registry Cleaner findings the user has chosen to ignore, e.g. the Image File
/// Execution Options entry that Defender Control deliberately creates.
/// Stored per user under HKCU\Software\UltraUninstaller\IgnoredRegistryIssues:
/// value name = the issue's IgnoreKey, value data = its Evidence at the time.
/// </summary>
[SupportedOSPlatform("windows")]
public static class IgnoredIssuesStore
{
    private const string StorePath = @"Software\UltraUninstaller\IgnoredRegistryIssues";

    public static bool IsIgnored(RegistryIssue issue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StorePath);
            if (key?.GetValue(issue.IgnoreKey) is not string savedEvidence)
                return false;

            // Same entry but different data now (e.g. the Debugger points somewhere else):
            // that is a new situation, so it is reported again.
            return string.Equals(savedEvidence, issue.Evidence ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void Ignore(RegistryIssue issue)
    {
        using var key = Registry.CurrentUser.CreateSubKey(StorePath, writable: true);
        key.SetValue(issue.IgnoreKey, issue.Evidence ?? string.Empty, RegistryValueKind.String);
    }

    public static void StopIgnoring(RegistryIssue issue)
    {
        using var key = Registry.CurrentUser.OpenSubKey(StorePath, writable: true);
        key?.DeleteValue(issue.IgnoreKey, throwOnMissingValue: false);
    }
}

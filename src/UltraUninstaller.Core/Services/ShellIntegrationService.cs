using System.Runtime.Versioning;
using Microsoft.Win32;

namespace UltraUninstaller.Core.Services;

/// <summary>
/// Adds/removes a right-click context menu entry for folders in Windows Explorer:
/// "Analyze size with UltraUninstaller". Implemented as a plain registry shell verb
/// under HKEY_CURRENT_USER\Software\Classes\Directory\shell — deliberately not a COM
/// shell extension, which would need registration, a class factory, and admin rights.
/// A per-user registry verb needs neither, and Explorer honors it immediately without
/// a restart.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellIntegrationService
{
    private const string VerbKeyPath = @"Software\Classes\Directory\shell\UltraUninstallerAnalyze";
    private const string VerbName = "Analyze size with UltraUninstaller";

    /// <summary>
    /// Registers the right-click entry, pointing at the given UltraUninstaller.exe path.
    /// Safe to call repeatedly (overwrites any existing registration).
    /// </summary>
    public void Register(string executablePath)
    {
        using var verbKey = Registry.CurrentUser.CreateSubKey(VerbKeyPath);
        verbKey.SetValue(string.Empty, VerbName);
        verbKey.SetValue("Icon", $"\"{executablePath}\",0");

        using var commandKey = verbKey.CreateSubKey("command");
        commandKey.SetValue(string.Empty, $"\"{executablePath}\" --analyze \"%1\"");
    }

    /// <summary>
    /// Removes the right-click entry. Safe to call even if it was never registered.
    /// </summary>
    public void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(VerbKeyPath, throwOnMissingSubKey: false);
    }

    /// <summary>
    /// Reports whether the entry is currently registered, so the UI can show an
    /// accurate "Enable" vs "Disable" state instead of a stateless toggle button.
    /// </summary>
    public bool IsRegistered()
    {
        using var verbKey = Registry.CurrentUser.OpenSubKey(VerbKeyPath);
        return verbKey is not null;
    }
}

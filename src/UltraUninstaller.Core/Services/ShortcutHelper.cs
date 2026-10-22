namespace UltraUninstaller.Core.Services;

/// <summary>Reads where a .lnk shortcut points, through the Windows Script Host COM object.</summary>
internal static class ShortcutHelper
{
    public static string? TryResolveTarget(string shortcutPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return null;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return null;

            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            string target = shortcut.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null; // Best effort - an unreadable shortcut is simply treated as "unknown target".
        }
    }
}

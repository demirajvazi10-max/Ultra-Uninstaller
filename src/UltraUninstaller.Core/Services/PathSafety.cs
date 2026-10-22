namespace UltraUninstaller.Core.Services;

/// <summary>
/// Last line of defence before anything deletes a folder: refuses Windows' own folders,
/// drive roots and the big shared "container" folders (Program Files, ProgramData,
/// Users, AppData...), no matter what a registry entry or a click says.
///
/// Registry data written by installers is not always sane - an InstallLocation of
/// "C:\Program Files" or even "C:\" exists in the wild - and a recursive delete of
/// that would wreck the system. Same for the Disk Analyzer: one wrong selection on
/// C:\ should never be able to force-delete "Windows" or "Users".
/// </summary>
public static class PathSafety
{
    public static bool IsProtected(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return true;

        string full;
        try
        {
            full = Normalize(Environment.ExpandEnvironmentVariables(path));
        }
        catch
        {
            return true; // Can't even make sense of the path - don't touch it.
        }

        // Drive roots (C:\) and UNC share roots.
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || string.Equals(Normalize(root), full, StringComparison.OrdinalIgnoreCase))
            return true;

        // Anything inside the Windows folder.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && IsSameOrInside(full, windows))
            return true;

        // The containers themselves (but not the program/data folders inside them).
        foreach (var container in ProtectedContainers())
        {
            if (string.Equals(full, container, StringComparison.OrdinalIgnoreCase))
                return true;

            // Also refuse anything that CONTAINS one of these (e.g. "C:\Users" contains the profile).
            if (IsSameOrInside(container, full))
                return true;
        }

        return false;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> itself or somewhere inside it.</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        return string.Equals(p, f, StringComparison.OrdinalIgnoreCase)
               || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static IEnumerable<string> ProtectedContainers()
    {
        var folders = new[]
        {
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles,
            Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,  // C:\ProgramData
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.ApplicationData,        // Roaming
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.CommonDocuments,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.StartMenu,
            Environment.SpecialFolder.CommonStartMenu,
            Environment.SpecialFolder.Programs,
            Environment.SpecialFolder.CommonPrograms,
            Environment.SpecialFolder.Startup,
            Environment.SpecialFolder.CommonStartup,
        };

        foreach (var folder in folders)
        {
            string value;
            try { value = Environment.GetFolderPath(folder); }
            catch { continue; }

            if (!string.IsNullOrEmpty(value))
                yield return Normalize(value);
        }

        // Folders without a SpecialFolder value.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            yield return Normalize(Path.Combine(profile, "Downloads"));
            yield return Normalize(Path.Combine(profile, "AppData"));
            yield return Normalize(Path.Combine(profile, "AppData", "LocalLow"));
            yield return Normalize(Path.Combine(profile, "OneDrive"));

            var usersRoot = Path.GetDirectoryName(Normalize(profile));
            if (!string.IsNullOrEmpty(usersRoot))
                yield return Normalize(usersRoot);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            yield return Normalize(Path.Combine(local, "Programs"));
            yield return Normalize(Path.Combine(local, "Temp"));
            yield return Normalize(Path.Combine(local, "Microsoft"));
            yield return Normalize(Path.Combine(local, "Packages"));
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(roaming))
            yield return Normalize(Path.Combine(roaming, "Microsoft"));

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
            yield return Normalize(Path.Combine(programData, "Microsoft"));
    }
}

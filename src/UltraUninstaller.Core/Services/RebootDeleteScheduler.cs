using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UltraUninstaller.Core.Services;

/// <summary>
/// Schedules a file or an entire folder tree for deletion the next time Windows
/// starts, via the same MoveFileEx mechanism Windows Installer itself uses for files
/// it can't remove while they're in use. This is the true last resort: nothing is
/// actually removed now, but it's guaranteed to be gone after the next restart,
/// without needing any special tool.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class RebootDeleteScheduler
{
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    /// <summary>
    /// Recursively schedules every file (depth-first, files before their parent
    /// folders) for deletion on next boot. Windows processes these entries in order
    /// at startup, so files must be listed before the directories that contain them.
    /// </summary>
    public static (bool Success, int ScheduledCount, string? Error) ScheduleDirectoryForDeletion(string path)
    {
        var scheduled = 0;

        try
        {
            if (Directory.Exists(path))
            {
                foreach (var subDir in Directory.GetDirectories(path))
                {
                    var (_, count, error) = ScheduleDirectoryForDeletion(subDir);
                    scheduled += count;
                    if (error is not null)
                        return (false, scheduled, error);
                }

                foreach (var file in Directory.GetFiles(path))
                {
                    if (!ScheduleSingle(file))
                        return (false, scheduled, $"Could not schedule {file} (Win32 error {Marshal.GetLastWin32Error()}).");
                    scheduled++;
                }

                if (!ScheduleSingle(path))
                    return (false, scheduled, $"Could not schedule folder {path} itself (Win32 error {Marshal.GetLastWin32Error()}).");
                scheduled++;
            }
            else if (File.Exists(path))
            {
                if (!ScheduleSingle(path))
                    return (false, scheduled, $"Could not schedule {path} (Win32 error {Marshal.GetLastWin32Error()}).");
                scheduled++;
            }

            return (true, scheduled, null);
        }
        catch (Exception ex)
        {
            return (false, scheduled, ex.Message);
        }
    }

    private static bool ScheduleSingle(string path) =>
        MoveFileEx(path, null, MOVEFILE_DELAY_UNTIL_REBOOT);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, uint dwFlags);
}

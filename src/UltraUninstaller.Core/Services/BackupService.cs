using System.Diagnostics;
using System.Runtime.Versioning;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.Core.Services;

public sealed record BackupOutcome(bool Success, string? Path, string? Message);

/// <summary>
/// Everything destructive in UltraUninstaller (registry deletion, force-removal of files,
/// registry cleaning) goes through this first. Nothing here is optional per-feature —
/// it is the one safety net shared by every module, exactly so it only has to be
/// built (and trusted) once.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BackupService
{
    private readonly string _backupRootFolder;

    public BackupService(string? backupRootFolder = null)
    {
        _backupRootFolder = backupRootFolder
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UltraUninstaller", "Backups");
    }

    /// <summary>
    /// Exports a full registry key (e.g. an app's Uninstall entry, or a key about to be
    /// removed by the registry cleaner) to a .reg file that can be double-clicked to restore it.
    /// Uses reg.exe directly rather than walking the key by hand, because reg.exe's export
    /// format is exactly what's needed to merge it back in with zero custom restore code.
    /// </summary>
    public async Task<BackupOutcome> ExportRegistryKeyAsync(string fullRegistryPath, string label)
    {
        try
        {
            Directory.CreateDirectory(_backupRootFolder);
            var fileName = $"{SanitizeForFileName(label)}_{DateTime.Now:yyyyMMdd_HHmmss}.reg";
            var fullFilePath = Path.Combine(_backupRootFolder, fileName);

            var result = await ProcessRunner.RunAsync("reg.exe", $"export \"{fullRegistryPath}\" \"{fullFilePath}\" /y", TimeSpan.FromMinutes(2));

            if (result.ExitCode != 0 || !File.Exists(fullFilePath))
                return new BackupOutcome(false, null, result.CombinedOutput ?? L.T("reg.exe export failed."));

            return new BackupOutcome(true, fullFilePath, null);
        }
        catch (Exception ex)
        {
            return new BackupOutcome(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Copies a folder that's about to be force-deleted into the backup area first,
    /// so "force removal" is always reversible by hand even without a restore point.
    /// Only used for folders under a sane size threshold — this is a safety net for
    /// leftover app folders, not a general-purpose backup tool.
    /// </summary>
    public async Task<BackupOutcome> BackUpFolderAsync(string folderPath, string label, long maxSizeBytes = 500_000_000)
    {
        try
        {
            if (!Directory.Exists(folderPath))
                return new BackupOutcome(true, null, L.T("Folder did not exist, nothing to back up."));

            var size = await Task.Run(() => GetDirectorySize(folderPath));
            if (size > maxSizeBytes)
            {
                return new BackupOutcome(false, null,
                    L.F("Folder is larger than the {0} MB safety limit for automatic backup; a System Restore point is relied on instead.", maxSizeBytes / 1_000_000));
            }

            Directory.CreateDirectory(_backupRootFolder);
            var destination = Path.Combine(_backupRootFolder, $"{SanitizeForFileName(label)}_{DateTime.Now:yyyyMMdd_HHmmss}");

            await Task.Run(() => CopyDirectory(folderPath, destination));

            return new BackupOutcome(true, destination, null);
        }
        catch (Exception ex)
        {
            return new BackupOutcome(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Creates a Windows System Restore point via PowerShell's Checkpoint-Computer.
    /// This is the broadest safety net (covers registry + system files at once) and is
    /// attempted before any Force Removal (Layer 3) uninstall or registry-cleaner batch.
    /// Windows limits automatic restore-point creation frequency to roughly once per
    /// 24 hours by default, so failure here is reported but never blocks the user —
    /// the per-item backups above still apply.
    /// </summary>
    public async Task<BackupOutcome> TryCreateRestorePointAsync(string description)
    {
        try
        {
            // PowerShell's Checkpoint-Computer only WARNS (exit code 0) when it skips the
            // restore point - e.g. System Protection is off or one was made in the last
            // 24 hours - so ask it explicitly for a non-zero exit code in that case,
            // otherwise "restore point created" would be announced when none exists.
            var script = "$WarningPreference='Stop'; $ErrorActionPreference='Stop'; " +
                         $"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType 'APPLICATION_UNINSTALL'";
            var result = await ProcessRunner.RunAsync("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"", TimeSpan.FromMinutes(5));

            if (result.ExitCode != 0)
            {
                return new BackupOutcome(false, null,
                    string.IsNullOrWhiteSpace(result.StandardError)
                        ? L.T("Restore point could not be created (System Protection may be off, or one was already made recently).")
                        : result.StandardError.Trim());
            }

            return new BackupOutcome(true, null, L.T("Restore point created."));
        }
        catch (Exception ex)
        {
            return new BackupOutcome(false, null, ex.Message);
        }
    }

    private static long GetDirectorySize(string path)
    {
        // Walk folder by folder, skipping junctions/symlinks: following them can loop
        // forever or wander into a different part of the disk.
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(current))
                {
                    try { total += new FileInfo(file).Length; }
                    catch { /* Skip files we can't stat (locked, permissions). */ }
                }

                foreach (var dir in Directory.EnumerateDirectories(current))
                {
                    if (!IsReparsePoint(dir))
                        pending.Push(dir);
                }
            }
            catch
            {
                // Unreadable folder - skip it rather than fail the whole backup.
            }
        }
        return total;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            try { File.Copy(file, destFile, overwrite: true); }
            catch { /* Best-effort: a locked file shouldn't abort the whole backup. */ }
        }
        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            if (IsReparsePoint(subDir))
                continue; // Don't follow junctions/symlinks - see GetDirectorySize.

            try { CopyDirectory(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir))); }
            catch { /* Best-effort: an unreadable subfolder shouldn't abort the whole backup. */ }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    private static string SanitizeForFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(input.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}

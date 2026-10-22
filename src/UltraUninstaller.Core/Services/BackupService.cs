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

            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"export \"{fullRegistryPath}\" \"{fullFilePath}\" /y",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return new BackupOutcome(false, null, L.T("Could not start reg.exe."));

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                return new BackupOutcome(false, null, string.IsNullOrWhiteSpace(error) ? L.T("reg.exe export failed.") : error);
            }

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
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType 'APPLICATION_UNINSTALL'\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return new BackupOutcome(false, null, L.T("Could not start PowerShell."));

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync();
                return new BackupOutcome(false, null,
                    string.IsNullOrWhiteSpace(error)
                        ? L.T("Restore point could not be created (System Protection may be off, or one was already made recently).")
                        : error);
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
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch { /* Skip files we can't stat (locked, permissions) rather than fail the whole scan. */ }
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
            CopyDirectory(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
        }
    }

    private static string SanitizeForFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(input.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}

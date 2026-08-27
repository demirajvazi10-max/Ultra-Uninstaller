using System.Diagnostics;
using System.Runtime.Versioning;
using UltraUninstaller.Core.Models;

namespace UltraUninstaller.Core.Services;

/// <summary>
/// Tries increasingly aggressive ways to remove a folder that a normal delete can't
/// touch. Built specifically around a common real cause of "I cannot delete this no
/// matter what": an old WinPE/WIM/VHD image that was mounted (via DISM, or a tool
/// built on it) and never cleanly unmounted, leaving a folder that Windows Explorer
/// shows as ordinary but which the filesystem still treats as an active mount.
///
///   Layer 1: normal Recycle Bin delete (fast path, works for the vast majority of folders).
///   Layer 2: detach an orphaned Windows image mount at this exact path, then a
///            system-wide mount-point cleanup if that fails, then retry delete.
///   Layer 3: take ownership + reset permissions, then retry delete.
///   Layer 4: permanent delete, bypassing the Recycle Bin.
///   Layer 5: schedule for deletion at the next Windows restart (last resort - always works
///            eventually, since Windows performs these before anything else can lock the files again).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ForceDeleteService
{
    public async Task<ForceDeleteResult> ForceDeleteAsync(string path, Action<OperationStep>? onStep = null)
    {
        var steps = new List<OperationStep>();

        void Report(string description, OperationStepStatus status, string? detail = null)
        {
            var step = new OperationStep { Description = description, Status = status, Detail = detail };
            steps.Add(step);
            onStep?.Invoke(step);
        }

        bool TryRecycleBin()
        {
            try
            {
                var outcome = ShellRecycleBin.SendToRecycleBin(path);
                return outcome.Success;
            }
            catch
            {
                return false; // Treat any unexpected failure here as "this layer didn't work" - never let it abort the whole pipeline.
            }
        }

        // --- Layer 1: normal delete -------------------------------------------------
        Report($"Trying a normal delete of {path}", OperationStepStatus.Started);
        if (TryRecycleBin())
        {
            Report("Deleted normally - no force needed.", OperationStepStatus.Succeeded);
            return new ForceDeleteResult { Success = true, LayerUsed = ForceDeleteLayer.RecycleBin, Steps = steps };
        }
        Report("Normal delete failed.", OperationStepStatus.Failed);

        // --- Layer 2: orphaned Windows image mount point -----------------------------
        Report("Checking for an orphaned Windows image mount (WinPE/WIM/VHD) at this folder", OperationStepStatus.Started);
        var unmountOutcome = await TryUnmountImageAsync(path);
        Report(unmountOutcome.Success ? "Detached the image mount." : "No specific mount found here, or it could not be detached directly.",
            unmountOutcome.Success ? OperationStepStatus.Succeeded : OperationStepStatus.Skipped, unmountOutcome.Message);

        if (!unmountOutcome.Success)
        {
            Report("Running a system-wide cleanup of orphaned Windows image mount points (this can take a minute)", OperationStepStatus.Started);
            var cleanupOutcome = await RunCleanupMountpointsAsync();
            Report(cleanupOutcome.Success ? "Mount point cleanup finished." : "Mount point cleanup did not report success.",
                cleanupOutcome.Success ? OperationStepStatus.Succeeded : OperationStepStatus.Skipped, cleanupOutcome.Message);
        }

        if (TryRecycleBin())
        {
            Report("Deleted after clearing the orphaned image mount.", OperationStepStatus.Succeeded);
            return new ForceDeleteResult { Success = true, LayerUsed = ForceDeleteLayer.ImageMountCleanup, Steps = steps };
        }

        // --- Layer 3: ownership + permissions -----------------------------------------
        Report("Taking ownership and resetting permissions on this folder", OperationStepStatus.Started);
        var ownershipOutcome = await TakeOwnershipAndResetPermissionsAsync(path);
        Report(ownershipOutcome.Success ? "Ownership and permissions reset." : "Could not fully reset ownership/permissions.",
            ownershipOutcome.Success ? OperationStepStatus.Succeeded : OperationStepStatus.Failed, ownershipOutcome.Message);

        if (TryRecycleBin())
        {
            Report("Deleted after resetting ownership and permissions.", OperationStepStatus.Succeeded);
            return new ForceDeleteResult { Success = true, LayerUsed = ForceDeleteLayer.OwnershipReset, Steps = steps };
        }

        // --- Layer 4: permanent delete, bypassing the Recycle Bin ---------------------
        Report("Attempting a permanent delete (this bypasses the Recycle Bin)", OperationStepStatus.Started);
        try
        {
            await Task.Run(() => Directory.Delete(path, recursive: true));
            Report("Permanently deleted.", OperationStepStatus.Succeeded);
            return new ForceDeleteResult { Success = true, LayerUsed = ForceDeleteLayer.PermanentDelete, Steps = steps };
        }
        catch (Exception ex)
        {
            Report("Permanent delete failed.", OperationStepStatus.Failed, ex.Message);
        }

        // --- Layer 5: schedule for deletion at next restart ---------------------------
        Report("Scheduling this folder for deletion the next time Windows restarts", OperationStepStatus.Started);
        var (scheduleSuccess, scheduledCount, scheduleError) = await Task.Run(() => RebootDeleteScheduler.ScheduleDirectoryForDeletion(path));

        if (scheduleSuccess)
        {
            Report($"Scheduled ({scheduledCount} item(s)). It will be gone after your next restart.", OperationStepStatus.Succeeded);
            return new ForceDeleteResult { Success = true, LayerUsed = ForceDeleteLayer.ScheduledOnReboot, Steps = steps };
        }

        Report("Could not even schedule it for deletion on restart.", OperationStepStatus.Failed, scheduleError);

        return new ForceDeleteResult
        {
            Success = false,
            LayerUsed = ForceDeleteLayer.Failed,
            Steps = steps,
            FailureReason = "Every method failed, including scheduling for restart. This folder may be protected by security software or an active driver.",
        };
    }

    /// <summary>
    /// Tries to detach a Windows image mounted at exactly this path via DISM's
    /// Unmount-Image /Discard - the precise, targeted fix for "this is a WinPE/WIM
    /// mount folder I forgot to clean up".
    /// </summary>
    private static async Task<(bool Success, string? Message)> TryUnmountImageAsync(string path)
    {
        var result = await RunProcessAsync("dism.exe", $"/Unmount-Image /MountDir:\"{path}\" /Discard", TimeSpan.FromMinutes(2));
        return (result.ExitCode == 0, result.Output);
    }

    /// <summary>
    /// System-wide cleanup of ALL orphaned Windows image mount points - the standard
    /// fix when a specific mount is too old/orphaned for DISM to still recognize it
    /// as trackable, which is common for mounts left over "from who knows when".
    /// </summary>
    private static async Task<(bool Success, string? Message)> RunCleanupMountpointsAsync()
    {
        var result = await RunProcessAsync("dism.exe", "/Cleanup-Mountpoints", TimeSpan.FromMinutes(3));
        return (result.ExitCode == 0, result.Output);
    }

    private static async Task<(bool Success, string? Message)> TakeOwnershipAndResetPermissionsAsync(string path)
    {
        var takeownResult = await RunProcessAsync("takeown.exe", $"/F \"{path}\" /R /D Y", TimeSpan.FromMinutes(2));
        var icaclsResult = await RunProcessAsync("icacls.exe", $"\"{path}\" /reset /T /C /Q", TimeSpan.FromMinutes(2));

        var success = takeownResult.ExitCode == 0 && icaclsResult.ExitCode == 0;
        var message = string.Join(" | ", new[] { takeownResult.Output, icaclsResult.Output }.Where(m => !string.IsNullOrWhiteSpace(m)));
        return (success, string.IsNullOrWhiteSpace(message) ? null : message);
    }

    private static async Task<(int ExitCode, string? Output)> RunProcessAsync(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return (-1, $"Could not start {fileName}.");

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (-1, $"{fileName} did not finish within {timeout.TotalSeconds:0} seconds and was stopped.");
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            var combined = string.Join(" ", new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

            return (process.ExitCode, string.IsNullOrWhiteSpace(combined) ? null : combined);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}

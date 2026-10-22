using System.Diagnostics;
using System.Runtime.Versioning;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.Core.Services;

public sealed record SilentRunOutcome(bool Success, int? ExitCode, string? ErrorOutput);

/// <summary>
/// Actually launches a silent uninstall command and waits for it — with no visible
/// window at all (CreateNoWindow + UseShellExecute=false), which is the whole point:
/// the vendor's own inaccessible GUI never appears.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SilentUninstallRunner
{
    /// <summary>
    /// Runs the given command silently. A non-zero exit code is reported but not
    /// automatically treated as failure by the caller — some installers (notably
    /// older NSIS ones) return non-zero on a clean uninstall, so the orchestrator
    /// re-checks the registry afterward rather than trusting the exit code alone.
    /// </summary>
    public async Task<SilentRunOutcome> RunAsync(UninstallCommand command, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.Arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return new SilentRunOutcome(false, null, L.T("Process failed to start."));

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return new SilentRunOutcome(false, null, L.F("Uninstaller did not finish within {0:0} seconds and was stopped.", timeout.TotalSeconds));
            }

            var stderr = await process.StandardError.ReadToEndAsync();
            return new SilentRunOutcome(process.ExitCode == 0, process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? null : stderr);
        }
        catch (Exception ex)
        {
            return new SilentRunOutcome(false, null, ex.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* Best-effort; nothing more we can do if this fails. */ }
    }
}

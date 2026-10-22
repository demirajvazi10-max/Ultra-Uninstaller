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
        // Output is read while the uninstaller runs (see ProcessRunner) - reading it only
        // after exit could deadlock a chatty uninstaller until the timeout killed it.
        var result = await ProcessRunner.RunAsync(command.FileName, command.Arguments, timeout);

        if (result.TimedOut)
            return new SilentRunOutcome(false, null, L.F("Uninstaller did not finish within {0:0} seconds and was stopped.", timeout.TotalSeconds));

        if (result.ExitCode == -1 && string.IsNullOrEmpty(result.StandardOutput) && !string.IsNullOrWhiteSpace(result.StandardError))
            return new SilentRunOutcome(false, null, result.StandardError);

        return new SilentRunOutcome(result.ExitCode == 0, result.ExitCode,
            string.IsNullOrWhiteSpace(result.StandardError) ? null : result.StandardError.Trim());
    }
}

using System.Diagnostics;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.Core.Services;

public sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    /// <summary>Stdout and stderr together, trimmed - handy for showing an error to the user.</summary>
    public string? CombinedOutput
    {
        get
        {
            var combined = string.Join(" ", new[] { StandardOutput, StandardError }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            return string.IsNullOrWhiteSpace(combined) ? null : combined;
        }
    }
}

/// <summary>
/// Runs a command-line tool with no window and captures its output.
///
/// Output is read WHILE the process runs, not after it exits. Reading only after exit
/// (what earlier versions did) deadlocks as soon as a tool writes more than the pipe
/// buffer holds: the tool blocks waiting for someone to read, we block waiting for it
/// to exit, and the timeout finally kills it. That silently broke things like
/// "takeown /R" on big folders and listing all scheduled tasks with schtasks.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessRunResult> RunAsync(string fileName, string arguments, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return new ProcessRunResult(-1, string.Empty, ex.Message, TimedOut: false);
        }

        if (process is null)
            return new ProcessRunResult(-1, string.Empty, L.F("Could not start {0}.", fileName), TimedOut: false);

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new ProcessRunResult(-1, string.Empty,
                    L.F("{0} did not finish within {1:0} seconds and was stopped.", fileName, timeout.TotalSeconds), TimedOut: true);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessRunResult(process.ExitCode, stdout, stderr, TimedOut: false);
        }
    }
}

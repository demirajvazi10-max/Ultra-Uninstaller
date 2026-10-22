using System.Runtime.Versioning;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.Core.Services;

/// <summary>
/// Options that control how far the orchestrator is allowed to go automatically.
/// UI code should always let the user see/confirm before ForceRemoval is attempted —
/// AllowForceRemoval defaults to false so a caller has to opt in deliberately.
/// </summary>
public sealed class ForceUninstallOptions
{
    public TimeSpan SilentUninstallTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public bool AllowForceRemoval { get; init; } = false;
    public bool CreateRestorePointBeforeForceRemoval { get; init; } = true;
}

/// <summary>
/// Runs the full Force Uninstall pipeline for one InstalledApp:
///   1. Native silent (QuietUninstallString, or UninstallString + inferred silent switch)
///   2. MSI /quiet (folded into step 1 for MSI apps, kept as separate layer for reporting)
///   3. Force removal (files + registry + shortcuts + AppData), only if explicitly allowed
///      and only after a restore point / backup attempt.
///
/// Every step is reported through the steps list as it happens so a caller (the WPF
/// layer) can announce it live via an accessible status region, not just show a
/// progress bar.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ForceUninstallOrchestrator
{
    private readonly SilentUninstallRunner _silentRunner;
    private readonly ForceRemovalService _forceRemoval;
    private readonly BackupService _backup;
    private readonly InstalledAppScanner _scanner;

    public ForceUninstallOrchestrator(
        SilentUninstallRunner? silentRunner = null,
        ForceRemovalService? forceRemoval = null,
        BackupService? backup = null,
        InstalledAppScanner? scanner = null)
    {
        _silentRunner = silentRunner ?? new SilentUninstallRunner();
        _forceRemoval = forceRemoval ?? new ForceRemovalService();
        _backup = backup ?? new BackupService();
        _scanner = scanner ?? new InstalledAppScanner();
    }

    /// <param name="onStep">Called synchronously as each step happens — wire this to an accessible live-region announcement in the UI.</param>
    public async Task<UninstallResult> UninstallAsync(InstalledApp app, ForceUninstallOptions options, Action<UninstallStep>? onStep = null)
    {
        var steps = new List<UninstallStep>();
        string? backupPath = null;

        void Report(string description, UninstallStepStatus status, string? detail = null)
        {
            var step = new UninstallStep { Description = description, Status = status, Detail = detail };
            steps.Add(step);
            onStep?.Invoke(step);
        }

        // --- Layer 1 & 2: native silent uninstall -----------------------------------
        Report(L.F("Attempting silent uninstall of {0}", app.DisplayName), UninstallStepStatus.Started);

        var command = InstallerTypeDetector.BuildSilentCommand(app);
        if (command is not null)
        {
            var started = DateTime.UtcNow;
            var runOutcome = await _silentRunner.RunAsync(command, options.SilentUninstallTimeout);

            // Some legitimate uninstallers exit non-zero even on success, so re-check
            // the registry directly instead of trusting the exit code alone.
            //
            // Many uninstallers (NSIS, Inno Setup and others) copy themselves to Temp,
            // start that copy and exit immediately, so "the process exited" does not mean
            // "the uninstall is finished". Checking only once, right away, reported a
            // still-running uninstall as failed - and with force removal allowed, deleted
            // files underneath it. So keep checking until the entry disappears or the
            // remaining time runs out.
            var stillPresent = IsStillInstalled(app);
            if (stillPresent && runOutcome.ExitCode is not null)
            {
                var deadline = started + options.SilentUninstallTimeout;
                while (stillPresent && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    stillPresent = IsStillInstalled(app);
                }
            }

            if (!stillPresent)
            {
                Report(L.F("{0} was removed silently.", app.DisplayName), UninstallStepStatus.Succeeded,
                    runOutcome.Success ? null : L.T("Exit code was non-zero, but the app is confirmed gone from the registry."));

                var layer = command.Kind == InstallerKind.Msi ? UninstallLayer.MsiQuiet : UninstallLayer.NativeSilent;
                return new UninstallResult { Success = true, LayerUsed = layer, Steps = steps, BackupPath = backupPath };
            }

            Report(L.T("Silent uninstall did not remove the app."), UninstallStepStatus.Failed, runOutcome.ErrorOutput);
        }
        else
        {
            Report(L.T("No usable uninstall command was found in the registry for this app."), UninstallStepStatus.Skipped);
        }

        // --- Layer 3: force removal, only if explicitly allowed ---------------------
        if (!options.AllowForceRemoval)
        {
            return new UninstallResult
            {
                Success = false,
                LayerUsed = UninstallLayer.Failed,
                Steps = steps,
                FailureReason = L.T("Silent uninstall failed and Force Removal was not enabled for this attempt."),
                BackupPath = backupPath,
            };
        }

        if (options.CreateRestorePointBeforeForceRemoval)
        {
            Report(L.T("Creating a System Restore point before force removal"), UninstallStepStatus.Started);
            var restoreOutcome = await _backup.TryCreateRestorePointAsync(L.F("Before UltraUninstaller force-removed {0}", app.DisplayName));
            Report(restoreOutcome.Success ? L.T("Restore point created.") : L.T("Restore point could not be created."),
                restoreOutcome.Success ? UninstallStepStatus.Succeeded : UninstallStepStatus.Failed,
                restoreOutcome.Message);
        }

        if (!string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            Report(L.T("Backing up install folder before deleting it"), UninstallStepStatus.Started);
            var folderBackup = await _backup.BackUpFolderAsync(app.InstallLocation!, app.DisplayName);
            backupPath = folderBackup.Path;
            Report(folderBackup.Success ? L.T("Install folder backed up.") : L.T("Install folder backup skipped."),
                folderBackup.Success ? UninstallStepStatus.Succeeded : UninstallStepStatus.Skipped,
                folderBackup.Message);
        }

        Report(L.T("Backing up registry entry"), UninstallStepStatus.Started);
        var regBackup = await _backup.ExportRegistryKeyAsync(app.FullRegistryPath, app.DisplayName);
        Report(regBackup.Success ? L.T("Registry entry backed up.") : L.T("Registry backup failed."),
            regBackup.Success ? UninstallStepStatus.Succeeded : UninstallStepStatus.Failed,
            regBackup.Message);

        Report(L.T("Deleting install folder"), UninstallStepStatus.Started);
        var folderOk = _forceRemoval.RemoveInstallFolder(app, out var folderError);
        Report(folderOk ? L.T("Install folder deleted.") : L.T("Could not delete install folder."),
            folderOk ? UninstallStepStatus.Succeeded : UninstallStepStatus.Failed, folderError);

        Report(L.T("Removing shortcuts"), UninstallStepStatus.Started);
        var removedShortcuts = _forceRemoval.RemoveShortcuts(app);
        Report(L.F("Removed {0}.", L.N(removedShortcuts.Count, "{0} shortcut", "{0} shortcuts")), UninstallStepStatus.Succeeded);

        Report(L.T("Removing leftover data folders"), UninstallStepStatus.Started);
        var removedData = _forceRemoval.RemoveAppDataRemnants(app);
        Report(L.F("Removed {0}.", L.N(removedData.Count, "{0} leftover data folder", "{0} leftover data folders")), UninstallStepStatus.Succeeded);

        Report(L.T("Removing registry entry"), UninstallStepStatus.Started);
        var regOk = _forceRemoval.RemoveUninstallRegistryEntry(app, out var regError);
        Report(regOk ? L.T("Registry entry removed.") : L.T("Could not remove registry entry."),
            regOk ? UninstallStepStatus.Succeeded : UninstallStepStatus.Failed, regError);

        var success = folderOk && regOk;

        return new UninstallResult
        {
            Success = success,
            LayerUsed = success ? UninstallLayer.ForceRemoval : UninstallLayer.Failed,
            Steps = steps,
            FailureReason = success ? null : L.T("One or more force-removal steps failed. See steps for detail."),
            BackupPath = backupPath,
        };
    }

    private bool IsStillInstalled(InstalledApp app) =>
        _scanner.ScanInstalledApps().Any(a => a.RegistryKeyName == app.RegistryKeyName && a.Scope == app.Scope);
}

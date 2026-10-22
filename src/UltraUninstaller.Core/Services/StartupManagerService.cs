using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.Core.Services;

public sealed record StartupToggleOutcome(bool Success, string? Error);

/// <summary>
/// Scans every common place something can launch itself at boot or logon - not just
/// the registry Run keys most basic startup managers stop at - and lets the user
/// enable/disable each one.
///
/// Registry Run entries and Startup-folder shortcuts are enabled/disabled through the
/// same "StartupApproved" switch Task Manager and Settings → Apps → Startup use, so a
/// change made here shows up there too (and the other way round). Earlier versions
/// renamed the Run value instead - but Windows runs every value in a Run key whatever
/// its name, so that "disable" never actually stopped anything; those old renamed
/// values are converted automatically on the next scan. Scheduled tasks use schtasks,
/// and services are switched between Automatic and Manual, with a small record of
/// which services UltraUninstaller itself set to Manual so they stay in the list and
/// can be switched back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupManagerService
{
    /// <summary>Legacy (pre-1.1) disable marker - kept only so old entries can be recognized and converted.</summary>
    private const string DisabledValuePrefix = "UltraUninstallerDisabled_";

    /// <summary>Legacy (pre-1.1) holding folder for disabled Startup-folder shortcuts - still honored and cleaned up.</summary>
    private const string DisabledShortcutFolderName = "Disabled by UltraUninstaller";

    private const string StartupApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    private const string OwnServiceRecordPath = @"SOFTWARE\UltraUninstaller\ServicesSetToManual";

    private static readonly (RegistryHive Hive, RegistryView View, RegistryScope Scope)[] RunKeyLocations =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, RegistryScope.LocalMachine64),
        (RegistryHive.LocalMachine, RegistryView.Registry32, RegistryScope.LocalMachine32OnWow64),
        (RegistryHive.CurrentUser, RegistryView.Registry64, RegistryScope.CurrentUser),
    };

    private const string RunKeySubPath64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeySubPathWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ServicesKeyPath = @"SYSTEM\CurrentControlSet\Services";

    public async Task<IReadOnlyList<StartupItem>> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var items = new List<StartupItem>();

        progress?.Report(L.T("Checking Run registry entries"));
        items.AddRange(await Task.Run(ScanRegistryRun, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(L.T("Checking Startup folder shortcuts"));
        items.AddRange(await Task.Run(ScanStartupFolder, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(L.T("Checking scheduled tasks that run at logon (this can take a moment)"));
        items.AddRange(await ScanScheduledTasksAsync(progress, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(L.T("Checking services set to start automatically"));
        items.AddRange(await Task.Run(ScanAutoStartServices, cancellationToken));

        return items
            .OrderBy(i => i.Source)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<StartupToggleOutcome> SetEnabledAsync(StartupItem item, bool enable)
    {
        try
        {
            return item.Source switch
            {
                StartupSource.RegistryRun => await Task.Run(() => ToggleRegistryRun(item, enable)),
                StartupSource.StartupFolder => await Task.Run(() => ToggleStartupFolder(item, enable)),
                StartupSource.ScheduledTask => await ToggleScheduledTaskAsync(item, enable),
                StartupSource.AutoStartService => await Task.Run(() => ToggleService(item, enable)),
                _ => new StartupToggleOutcome(false, L.T("Unknown startup source.")),
            };
        }
        catch (Exception ex)
        {
            return new StartupToggleOutcome(false, ex.Message);
        }
    }

    // ================= Registry Run =================

    private static IReadOnlyList<StartupItem> ScanRegistryRun()
    {
        var items = new List<StartupItem>();

        foreach (var (hive, view, scope) in RunKeyLocations)
        {
            var subPath = scope == RegistryScope.LocalMachine32OnWow64 ? RunKeySubPathWow : RunKeySubPath64;

            using var baseKey = RegistryKey.OpenBaseKey(hive, view);

            // Old versions "disabled" by renaming the value, which never actually worked.
            // Convert those now: put the real name back and switch it off properly.
            MigrateLegacyRenamedRunValues(baseKey, subPath, scope);

            using var runKey = baseKey.OpenSubKey(subPath);
            if (runKey is null)
                continue;

            foreach (var valueName in runKey.GetValueNames())
            {
                if (runKey.GetValue(valueName) is not string rawCommand || string.IsNullOrWhiteSpace(rawCommand))
                    continue;

                // If a legacy value couldn't be converted (e.g. no admin rights for HKLM),
                // it is still RUNNING at every logon - so report it honestly as enabled.
                var displayName = valueName.StartsWith(DisabledValuePrefix, StringComparison.Ordinal)
                    ? valueName[DisabledValuePrefix.Length..]
                    : valueName;
                var isDisabled = !valueName.StartsWith(DisabledValuePrefix, StringComparison.Ordinal)
                                 && IsApprovedDisabled(ApprovedHive(scope), ApprovedRunSubKey(scope), valueName);

                var (exePath, _) = InstallerTypeDetector.SplitCommandLine(rawCommand);
                var fileInfo = TryGetFileInfo(exePath);

                items.Add(new StartupItem
                {
                    Source = StartupSource.RegistryRun,
                    Name = displayName,
                    Description = fileInfo.Description,
                    Publisher = fileInfo.Publisher,
                    FilePath = string.IsNullOrWhiteSpace(exePath) ? null : exePath,
                    IsSigned = fileInfo.IsSigned,
                    IsInUnusualLocation = fileInfo.IsInUnusualLocation,
                    IsEnabled = !isDisabled,
                    Identifier = valueName, // the ACTUAL current value name (may carry the disabled prefix)
                    Scope = scope,
                });
            }
        }

        return items;
    }

    private static StartupToggleOutcome ToggleRegistryRun(StartupItem item, bool enable)
    {
        if (item.Scope is not RegistryScope scope)
            return new StartupToggleOutcome(false, L.T("Missing registry scope."));

        var subPath = scope == RegistryScope.LocalMachine32OnWow64 ? RunKeySubPathWow : RunKeySubPath64;
        var (hive, view) = scope switch
        {
            RegistryScope.LocalMachine64 => (RegistryHive.LocalMachine, RegistryView.Registry64),
            RegistryScope.LocalMachine32OnWow64 => (RegistryHive.LocalMachine, RegistryView.Registry32),
            RegistryScope.CurrentUser => (RegistryHive.CurrentUser, RegistryView.Registry64),
            _ => throw new ArgumentOutOfRangeException(),
        };

        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var runKey = baseKey.OpenSubKey(subPath, writable: true);
        if (runKey is null)
            return new StartupToggleOutcome(false, L.T("Run key not found."));

        var valueName = item.Identifier;

        // A legacy renamed value that couldn't be converted during the scan: put its real name back first.
        if (valueName.StartsWith(DisabledValuePrefix, StringComparison.Ordinal))
        {
            var data = runKey.GetValue(valueName);
            if (data is null)
                return new StartupToggleOutcome(false, L.T("Value no longer exists (already changed elsewhere?)."));

            var originalName = valueName[DisabledValuePrefix.Length..];
            runKey.SetValue(originalName, data);
            runKey.DeleteValue(valueName, throwOnMissingValue: false);
            valueName = originalName;
        }
        else if (runKey.GetValue(valueName) is null)
        {
            return new StartupToggleOutcome(false, L.T("Value no longer exists (already changed elsewhere?)."));
        }

        SetApproved(ApprovedHive(scope), ApprovedRunSubKey(scope), valueName, enable);
        return new StartupToggleOutcome(true, null);
    }

    // ================= Startup folder =================

    private static IReadOnlyList<StartupItem> ScanStartupFolder()
    {
        var items = new List<StartupItem>();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        };

        foreach (var root in roots.Where(Directory.Exists).Distinct())
        {
            var approvedHive = IsCommonStartupFolder(root) ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;

            // Shortcuts directly in the Startup folder (Windows does not recurse into subfolders).
            // Whether each one actually runs is decided by its StartupApproved switch.
            foreach (var shortcut in SafeGetFiles(root, "*.lnk"))
            {
                var disabled = IsApprovedDisabled(approvedHive, "StartupFolder", Path.GetFileName(shortcut));
                items.Add(BuildStartupFolderItem(shortcut, isEnabled: !disabled));
            }

            // Legacy (pre-1.1) disabled items: shortcuts moved into our old holding subfolder.
            // These genuinely don't run, so they're listed as disabled and enabling moves them back.
            var disabledFolder = Path.Combine(root, DisabledShortcutFolderName);
            if (Directory.Exists(disabledFolder))
            {
                foreach (var shortcut in SafeGetFiles(disabledFolder, "*.lnk"))
                    items.Add(BuildStartupFolderItem(shortcut, isEnabled: false));
            }
        }

        return items;
    }

    private static StartupItem BuildStartupFolderItem(string shortcutPath, bool isEnabled)
    {
        var targetPath = TryResolveShortcutTarget(shortcutPath);
        var fileInfo = TryGetFileInfo(targetPath);
        var name = Path.GetFileNameWithoutExtension(shortcutPath);

        return new StartupItem
        {
            Source = StartupSource.StartupFolder,
            Name = name,
            Description = fileInfo.Description,
            Publisher = fileInfo.Publisher,
            FilePath = targetPath ?? shortcutPath,
            IsSigned = fileInfo.IsSigned,
            IsInUnusualLocation = fileInfo.IsInUnusualLocation,
            IsEnabled = isEnabled,
            Identifier = shortcutPath, // current full path to the .lnk file, wherever it currently sits
        };
    }

    private static StartupToggleOutcome ToggleStartupFolder(StartupItem item, bool enable)
    {
        var currentPath = item.Identifier;
        if (!File.Exists(currentPath))
            return new StartupToggleOutcome(false, L.T("Shortcut file no longer exists."));

        var startupRoot = Path.GetDirectoryName(Path.GetDirectoryName(currentPath)) is { } grandParent
                           && Path.GetFileName(Path.GetDirectoryName(currentPath)) == DisabledShortcutFolderName
            ? grandParent
            : Path.GetDirectoryName(currentPath)!;

        var fileName = Path.GetFileName(currentPath);
        var isInLegacyHoldingFolder = !string.Equals(Path.GetDirectoryName(currentPath), startupRoot, StringComparison.OrdinalIgnoreCase);

        if (isInLegacyHoldingFolder)
        {
            // Bring it back into the real Startup folder, then let StartupApproved decide.
            var destination = Path.Combine(startupRoot, fileName);
            File.Move(currentPath, destination, overwrite: false);
            TryDeleteEmptyFolder(Path.GetDirectoryName(currentPath)!);
        }

        var approvedHive = IsCommonStartupFolder(startupRoot) ? RegistryHive.LocalMachine : RegistryHive.CurrentUser;
        SetApproved(approvedHive, "StartupFolder", fileName, enable);
        return new StartupToggleOutcome(true, null);
    }

    // ================= Scheduled Tasks =================

    private async Task<IReadOnlyList<StartupItem>> ScanScheduledTasksAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var items = new List<StartupItem>();

        var listResult = await RunProcessAsync("schtasks.exe", "/Query /FO CSV /NH", TimeSpan.FromSeconds(30));
        if (listResult.ExitCode != 0 || listResult.Output is null)
            return items; // schtasks not available or failed - treat as "nothing found" rather than an error, this source is a bonus, not core.

        var taskNames = listResult.Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseFirstCsvField)
            .Where(name => !string.IsNullOrWhiteSpace(name) && name != "TaskName")
            .Distinct()
            .ToList();

        for (var i = 0; i < taskNames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var taskName = taskNames[i];
            progress?.Report(L.F("Checking scheduled task {0} of {1}: {2}", i + 1, taskNames.Count, taskName));

            var xmlResult = await RunProcessAsync("schtasks.exe", $"/Query /TN \"{taskName}\" /XML", TimeSpan.FromSeconds(10));
            if (xmlResult.ExitCode != 0 || xmlResult.Output is null)
                continue;

            if (!xmlResult.Output.Contains("<LogonTrigger", StringComparison.OrdinalIgnoreCase))
                continue; // Only tasks that actually trigger at logon count as "startup" items.

            var isEnabled = !xmlResult.Output.Contains("<Enabled>false</Enabled>", StringComparison.OrdinalIgnoreCase);
            var command = ExtractXmlValue(xmlResult.Output, "Command");
            var fileInfo = TryGetFileInfo(command);

            items.Add(new StartupItem
            {
                Source = StartupSource.ScheduledTask,
                Name = taskName.TrimStart('\\'),
                Description = ExtractXmlValue(xmlResult.Output, "Description"),
                Publisher = ExtractXmlValue(xmlResult.Output, "Author") ?? fileInfo.Publisher,
                FilePath = command,
                IsSigned = fileInfo.IsSigned,
                IsInUnusualLocation = fileInfo.IsInUnusualLocation,
                IsEnabled = isEnabled,
                Identifier = taskName,
            });
        }

        return items;
    }

    private static async Task<StartupToggleOutcome> ToggleScheduledTaskAsync(StartupItem item, bool enable)
    {
        var flag = enable ? "/Enable" : "/Disable";
        var result = await RunProcessAsync("schtasks.exe", $"/Change /TN \"{item.Identifier}\" {flag}", TimeSpan.FromSeconds(15));
        return result.ExitCode == 0
            ? new StartupToggleOutcome(true, null)
            : new StartupToggleOutcome(false, string.IsNullOrWhiteSpace(result.Output)
                ? L.F("schtasks exited with code {0}.", result.ExitCode)
                : result.Output.Trim());
    }

    // ================= Auto-start Services =================

    private static IReadOnlyList<StartupItem> ScanAutoStartServices()
    {
        var items = new List<StartupItem>();

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var servicesKey = baseKey.OpenSubKey(ServicesKeyPath);
        if (servicesKey is null)
            return items;

        var ownManualRecord = ReadOwnServiceRecord(baseKey);

        foreach (var serviceName in servicesKey.GetSubKeyNames())
        {
            using var serviceKey = servicesKey.OpenSubKey(serviceName);
            if (serviceKey is null)
                continue;

            var start = serviceKey.GetValue("Start") as int?;
            var type = serviceKey.GetValue("Type") as int?;

            // Only Win32 services (not drivers) that are set to Automatic (2) are "startup impact" candidates,
            // plus the ones UltraUninstaller itself switched to Manual (3), so they stay listed and can be switched back.
            const int SERVICE_WIN32_OWN_PROCESS = 0x10;
            const int SERVICE_WIN32_SHARE_PROCESS = 0x20;
            if (type is null || (type & (SERVICE_WIN32_OWN_PROCESS | SERVICE_WIN32_SHARE_PROCESS)) == 0)
                continue;

            var setToManualByUs = start == 3 && ownManualRecord.Contains(serviceName);
            if (start != 2 && !setToManualByUs)
                continue;

            var displayName = serviceKey.GetValue("DisplayName") as string ?? serviceName;
            var description = serviceKey.GetValue("Description") as string;
            var imagePath = serviceKey.GetValue("ImagePath") as string;
            var (exePath, _) = string.IsNullOrWhiteSpace(imagePath) ? (null, null) : InstallerTypeDetector.SplitCommandLine(imagePath);
            var fileInfo = TryGetFileInfo(exePath);

            items.Add(new StartupItem
            {
                Source = StartupSource.AutoStartService,
                Name = displayName,
                Description = description ?? fileInfo.Description,
                Publisher = fileInfo.Publisher,
                FilePath = exePath,
                IsSigned = fileInfo.IsSigned,
                IsInUnusualLocation = fileInfo.IsInUnusualLocation,
                IsEnabled = start == 2,
                Identifier = serviceName,
            });
        }

        return items;
    }

    private static StartupToggleOutcome ToggleService(StartupItem item, bool enable)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var serviceKey = baseKey.OpenSubKey($@"{ServicesKeyPath}\{item.Identifier}", writable: true);
        if (serviceKey is null)
            return new StartupToggleOutcome(false, L.T("Service registry key no longer exists."));

        // Automatic (2) <-> Manual (3). We deliberately never write Disabled (4) from here -
        // Manual is the standard, safe way to stop something auto-starting while leaving it
        // able to start on demand if something else genuinely needs it.
        serviceKey.SetValue("Start", enable ? 2 : 3, RegistryValueKind.DWord);

        // Remember which services we switched to Manual, so the next scan still lists them (as disabled).
        using var recordKey = baseKey.CreateSubKey(OwnServiceRecordPath, writable: true);
        if (enable)
            recordKey.DeleteValue(item.Identifier, throwOnMissingValue: false);
        else
            recordKey.SetValue(item.Identifier, 1, RegistryValueKind.DWord);

        return new StartupToggleOutcome(true, null);
    }

    // ================= StartupApproved (the switch Task Manager uses) =================
    //
    // Each entry is a 12-byte REG_BINARY value named after the Run value (or the shortcut's
    // file name): first byte 02 = enabled, 03 = disabled (odd = disabled in general), the
    // last 8 bytes are the FILETIME when it was disabled. A missing value means enabled.

    private static RegistryHive ApprovedHive(RegistryScope scope) =>
        scope == RegistryScope.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;

    private static string ApprovedRunSubKey(RegistryScope scope) =>
        scope == RegistryScope.LocalMachine32OnWow64 ? "Run32" : "Run";

    private static bool IsApprovedDisabled(RegistryHive hive, string subKey, string valueName)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey($@"{StartupApprovedPath}\{subKey}");
            return key?.GetValue(valueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
        }
        catch
        {
            return false;
        }
    }

    private static void SetApproved(RegistryHive hive, string subKey, string valueName, bool enable)
    {
        var data = new byte[12];
        if (enable)
        {
            data[0] = 0x02;
        }
        else
        {
            data[0] = 0x03;
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        }

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey($@"{StartupApprovedPath}\{subKey}", writable: true);
        key.SetValue(valueName, data, RegistryValueKind.Binary);
    }

    private static void MigrateLegacyRenamedRunValues(RegistryKey baseKey, string subPath, RegistryScope scope)
    {
        try
        {
            using var runKey = baseKey.OpenSubKey(subPath, writable: true);
            if (runKey is null)
                return;

            foreach (var legacyName in runKey.GetValueNames().Where(n => n.StartsWith(DisabledValuePrefix, StringComparison.Ordinal)))
            {
                var originalName = legacyName[DisabledValuePrefix.Length..];
                var data = runKey.GetValue(legacyName);
                if (data is null || string.IsNullOrEmpty(originalName))
                    continue;

                if (runKey.GetValue(originalName) is null)
                    runKey.SetValue(originalName, data, runKey.GetValueKind(legacyName));

                runKey.DeleteValue(legacyName, throwOnMissingValue: false);
                SetApproved(ApprovedHive(scope), ApprovedRunSubKey(scope), originalName, enable: false);
            }
        }
        catch
        {
            // No write access (e.g. HKLM without admin rights) - the entry is listed as enabled
            // (which is the truth) and gets converted when the user toggles it.
        }
    }

    private static bool IsCommonStartupFolder(string folder) =>
        string.Equals(Path.TrimEndingDirectorySeparator(folder),
                      Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)),
                      StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteEmptyFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch
        {
            // Cosmetic only.
        }
    }

    private static HashSet<string> ReadOwnServiceRecord(RegistryKey localMachine)
    {
        try
        {
            using var key = localMachine.OpenSubKey(OwnServiceRecordPath);
            return key is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(key.GetValueNames(), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // ================= Shared helpers =================

    private sealed record FileInfoSummary(string? Description, string? Publisher, bool? IsSigned, bool IsInUnusualLocation);

    private static FileInfoSummary TryGetFileInfo(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return new FileInfoSummary(null, null, null, false);

        filePath = Environment.ExpandEnvironmentVariables(filePath);
        if (!File.Exists(filePath))
            return new FileInfoSummary(null, null, null, false);

        string? description = null;
        string? publisher = null;
        bool? isSigned = null;

        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(filePath);
            description = string.IsNullOrWhiteSpace(versionInfo.FileDescription) ? null : versionInfo.FileDescription;
            publisher = string.IsNullOrWhiteSpace(versionInfo.CompanyName) ? null : versionInfo.CompanyName;
        }
        catch
        {
            // No/unreadable version info - not every executable has one, that's normal.
        }

        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
            isSigned = cert is not null;
        }
        catch
        {
            isSigned = false; // Genuinely unsigned, or we couldn't verify - treat the same for display purposes.
        }

        var lowerPath = filePath.ToLowerInvariant();
        var isUnusual = lowerPath.Contains(@"\temp\") || lowerPath.Contains(@"\downloads\") ||
                         lowerPath.Contains(@"\appdata\local\temp\");

        return new FileInfoSummary(description, publisher, isSigned, isUnusual);
    }

    private static string? TryResolveShortcutTarget(string shortcutPath)
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
            return null; // Best-effort - the shortcut file name alone is still shown even if we can't resolve its target.
        }
    }

    private static IEnumerable<string> SafeGetFiles(string path, string pattern)
    {
        try { return Directory.GetFiles(path, pattern); }
        catch { return Array.Empty<string>(); }
    }

    private static string ParseFirstCsvField(string csvLine)
    {
        if (string.IsNullOrEmpty(csvLine))
            return string.Empty;

        var trimmed = csvLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            return endQuote > 0 ? trimmed[1..endQuote] : trimmed.Trim('"');
        }

        var comma = trimmed.IndexOf(',');
        return comma > 0 ? trimmed[..comma] : trimmed;
    }

    private static string? ExtractXmlValue(string xml, string tagName)
    {
        var openTag = $"<{tagName}>";
        var closeTag = $"</{tagName}>";
        var start = xml.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += openTag.Length;
        var end = xml.IndexOf(closeTag, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            return null;

        var value = xml[start..end].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : System.Net.WebUtility.HtmlDecode(value);
    }

    private static async Task<(int ExitCode, string? Output)> RunProcessAsync(string fileName, string arguments, TimeSpan timeout)
    {
        var result = await ProcessRunner.RunAsync(fileName, arguments, timeout);
        return result.TimedOut ? (-1, null) : (result.ExitCode, result.ExitCode == 0 ? result.StandardOutput : result.CombinedOutput);
    }
}

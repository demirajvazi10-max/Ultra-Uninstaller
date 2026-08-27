using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using UltraUninstaller.Core.Models;

namespace UltraUninstaller.Core.Services;

public sealed record StartupToggleOutcome(bool Success, string? Error);

/// <summary>
/// Scans every common place something can launch itself at boot or logon - not just
/// the registry Run keys most basic startup managers stop at - and lets the user
/// enable/disable each one. Every disable is fully reversible and driven entirely by
/// UltraUninstaller's own bookkeeping (a rename prefix for registry values, a holding
/// subfolder for Startup-folder shortcuts, native commands for tasks/services) rather
/// than by writing to Windows' own undocumented internal state (e.g. the
/// "StartupApproved" registry format Task Manager uses, which has no official public
/// specification) - correctness and reversibility matter more here than mirroring
/// Task Manager's own UI exactly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupManagerService
{
    private const string DisabledValuePrefix = "UltraUninstallerDisabled_";
    private const string DisabledShortcutFolderName = "Disabled by UltraUninstaller";

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

        progress?.Report("Checking Run registry entries");
        items.AddRange(await Task.Run(ScanRegistryRun, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report("Checking Startup folder shortcuts");
        items.AddRange(await Task.Run(ScanStartupFolder, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report("Checking scheduled tasks that run at logon (this can take a moment)");
        items.AddRange(await ScanScheduledTasksAsync(progress, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report("Checking services set to start automatically");
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
                _ => new StartupToggleOutcome(false, "Unknown startup source."),
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
            using var runKey = baseKey.OpenSubKey(subPath);
            if (runKey is null)
                continue;

            foreach (var valueName in runKey.GetValueNames())
            {
                if (runKey.GetValue(valueName) is not string rawCommand || string.IsNullOrWhiteSpace(rawCommand))
                    continue;

                var isDisabled = valueName.StartsWith(DisabledValuePrefix, StringComparison.Ordinal);
                var displayName = isDisabled ? valueName[DisabledValuePrefix.Length..] : valueName;

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
            return new StartupToggleOutcome(false, "Missing registry scope.");

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
            return new StartupToggleOutcome(false, "Run key not found.");

        var currentValueName = item.Identifier;
        var isCurrentlyDisabled = currentValueName.StartsWith(DisabledValuePrefix, StringComparison.Ordinal);

        if (enable == !isCurrentlyDisabled)
            return new StartupToggleOutcome(true, null); // Already in the requested state.

        var data = runKey.GetValue(currentValueName);
        if (data is null)
            return new StartupToggleOutcome(false, "Value no longer exists (already changed elsewhere?).");

        var newValueName = enable ? item.Name : DisabledValuePrefix + item.Name;

        runKey.SetValue(newValueName, data);
        runKey.DeleteValue(currentValueName, throwOnMissingValue: false);

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
            // Enabled items: .lnk files directly in the Startup folder (Windows does not recurse into subfolders).
            foreach (var shortcut in SafeGetFiles(root, "*.lnk"))
                items.Add(BuildStartupFolderItem(shortcut, isEnabled: true));

            // Disabled items: shortcuts we've moved into our own holding subfolder.
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
            return new StartupToggleOutcome(false, "Shortcut file no longer exists.");

        var startupRoot = Path.GetDirectoryName(Path.GetDirectoryName(currentPath)) is { } grandParent
                           && Path.GetFileName(Path.GetDirectoryName(currentPath)) == DisabledShortcutFolderName
            ? grandParent
            : Path.GetDirectoryName(currentPath)!;

        var fileName = Path.GetFileName(currentPath);

        if (enable)
        {
            var destination = Path.Combine(startupRoot, fileName);
            File.Move(currentPath, destination, overwrite: false);
        }
        else
        {
            var disabledFolder = Path.Combine(startupRoot, DisabledShortcutFolderName);
            Directory.CreateDirectory(disabledFolder);
            var destination = Path.Combine(disabledFolder, fileName);
            File.Move(currentPath, destination, overwrite: false);
        }

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
            progress?.Report($"Checking scheduled task {i + 1} of {taskNames.Count}: {taskName}");

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
            : new StartupToggleOutcome(false, result.Output ?? $"schtasks exited with code {result.ExitCode}.");
    }

    // ================= Auto-start Services =================

    private static IReadOnlyList<StartupItem> ScanAutoStartServices()
    {
        var items = new List<StartupItem>();

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var servicesKey = baseKey.OpenSubKey(ServicesKeyPath);
        if (servicesKey is null)
            return items;

        foreach (var serviceName in servicesKey.GetSubKeyNames())
        {
            using var serviceKey = servicesKey.OpenSubKey(serviceName);
            if (serviceKey is null)
                continue;

            var start = serviceKey.GetValue("Start") as int?;
            var type = serviceKey.GetValue("Type") as int?;

            // Only Win32 services (not drivers) that are set to Automatic (2) are "startup impact" candidates.
            const int SERVICE_WIN32_OWN_PROCESS = 0x10;
            const int SERVICE_WIN32_SHARE_PROCESS = 0x20;
            if (start != 2 || type is null || (type & (SERVICE_WIN32_OWN_PROCESS | SERVICE_WIN32_SHARE_PROCESS)) == 0)
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
                IsEnabled = true, // by definition, since we only listed Start==2 (Automatic) entries
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
            return new StartupToggleOutcome(false, "Service registry key no longer exists.");

        // Automatic (2) <-> Manual (3). We deliberately never write Disabled (4) from here -
        // Manual is the standard, safe way to stop something auto-starting while leaving it
        // able to start on demand if something else genuinely needs it.
        serviceKey.SetValue("Start", enable ? 2 : 3, RegistryValueKind.DWord);
        return new StartupToggleOutcome(true, null);
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
                return (-1, null);

            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (-1, null);
            }

            var stdout = await process.StandardOutput.ReadToEndAsync();
            return (process.ExitCode, stdout);
        }
        catch
        {
            return (-1, null);
        }
    }
}

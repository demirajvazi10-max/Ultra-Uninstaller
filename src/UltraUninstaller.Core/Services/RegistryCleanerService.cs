using System.Runtime.Versioning;
using Microsoft.Win32;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.Core.Services;

public sealed record RegistryRemoveOutcome(bool Success, string? Error, string? BackupPath);

/// <summary>
/// Scans for deliberately narrow, well-understood categories of registry issues -
/// not a general-purpose "fix everything" registry scrubber, and not a padded
/// "thousands of problems found" count of harmless clutter either. Being
/// conservative is intentional: false positives in a registry cleaner break things,
/// so this only flags entries where the answer is unambiguous, and every removal is
/// backed up first via <see cref="BackupService"/> so it can always be undone.
///
///   1. Orphaned Uninstall entries - Programs and Features still lists something
///      whose own uninstaller .exe no longer exists (it was deleted by hand, not
///      through Windows). MSI entries are never flagged here.
///   2. Orphaned Startup entries - a "Run at Windows startup" entry pointing at an
///      .exe that no longer exists.
///   3. Security Review - structural persistence/hijack techniques with well-
///      documented, near-zero legitimate consumer use: Image File Execution
///      Options Debugger redirection, Winlogon Shell/Userinit tampering, and a
///      non-empty AppInit_DLLs. These are real, specific, MITRE ATT&CK-documented
///      techniques, not guesses - but still always framed as "review this", never
///      "this is malware", since a pattern match is not a verdict.
///   4. Suspicious Startup Commands - a Run-at-startup entry whose command line
///      contains a known "living off the land" pattern (encoded PowerShell,
///      mshta/regsvr32/certutil fetching from a URL) - the same reasoning applies.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryCleanerService
{
    private readonly InstalledAppScanner _appScanner;
    private readonly BackupService _backup;

    private static readonly (RegistryHive Hive, RegistryView View, RegistryScope Scope)[] RunKeyLocations =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, RegistryScope.LocalMachine64),
        (RegistryHive.LocalMachine, RegistryView.Registry32, RegistryScope.LocalMachine32OnWow64),
        (RegistryHive.CurrentUser, RegistryView.Registry64, RegistryScope.CurrentUser),
    };

    private const string RunKeySubPath64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunKeySubPathWow = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string IfeoSubPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string WinlogonSubPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string WindowsNtSubPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
    private const string CorrectShell = "explorer.exe";
    private const string CorrectUserinit = @"C:\Windows\system32\userinit.exe,";

    /// <summary>Known "living off the land" abuse patterns: (label, check). Deliberately a short, high-confidence list - not an attempt at a full malware signature database.</summary>
    private static readonly (string Label, Func<string, bool> IsMatch)[] SuspiciousCommandPatterns =
    {
        (L.T("an encoded/obfuscated PowerShell command"), c => c.Contains("powershell", StringComparison.OrdinalIgnoreCase) &&
            (c.Contains("-enc", StringComparison.OrdinalIgnoreCase) || c.Contains("-encodedcommand", StringComparison.OrdinalIgnoreCase))),
        (L.T("mshta.exe fetching content from a web address"), c => c.Contains("mshta", StringComparison.OrdinalIgnoreCase) &&
            (c.Contains("http://", StringComparison.OrdinalIgnoreCase) || c.Contains("https://", StringComparison.OrdinalIgnoreCase))),
        (L.T("regsvr32.exe registering a component from a web address"), c => c.Contains("regsvr32", StringComparison.OrdinalIgnoreCase) &&
            c.Contains("/i:http", StringComparison.OrdinalIgnoreCase)),
        (L.T("certutil.exe used to download or decode a file (a known trick to fetch payloads)"), c => c.Contains("certutil", StringComparison.OrdinalIgnoreCase) &&
            (c.Contains("-urlcache", StringComparison.OrdinalIgnoreCase) || c.Contains("-decode", StringComparison.OrdinalIgnoreCase))),
        (L.T("rundll32.exe pointed at a web address instead of a local file"), c => c.Contains("rundll32", StringComparison.OrdinalIgnoreCase) &&
            (c.Contains("http://", StringComparison.OrdinalIgnoreCase) || c.Contains("https://", StringComparison.OrdinalIgnoreCase))),
    };

    public RegistryCleanerService(InstalledAppScanner? appScanner = null, BackupService? backup = null)
    {
        _appScanner = appScanner ?? new InstalledAppScanner();
        _backup = backup ?? new BackupService();
    }

    public Task<IReadOnlyList<RegistryIssue>> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            progress?.Report(L.T("Checking installed programs for orphaned uninstall entries"));
            var issues = new List<RegistryIssue>();
            issues.AddRange(ScanOrphanedUninstallEntries());
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(L.T("Checking startup registry entries"));
            issues.AddRange(ScanOrphanedStartupEntries());
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(L.T("Checking for known system hijack techniques (Image File Execution Options, Winlogon, AppInit_DLLs)"));
            issues.AddRange(ScanSecurityReviewIssues());
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(L.T("Checking startup commands for known suspicious patterns"));
            issues.AddRange(ScanSuspiciousStartupCommands());

            return (IReadOnlyList<RegistryIssue>)issues
                .OrderBy(i => i.Category)
                .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    /// <summary>
    /// Backs up the affected key, then either removes exactly what the issue
    /// describes, or - for issues where CorrectedValueData is set - resets that
    /// value to its known-safe default instead of deleting it outright.
    /// </summary>
    public async Task<RegistryRemoveOutcome> RemoveAsync(RegistryIssue issue)
    {
        var backupOutcome = await _backup.ExportRegistryKeyAsync(issue.FullRegistryPath, issue.DisplayName);

        try
        {
            var baseKey = OpenBaseKey(issue.Scope);
            using var parentKey = baseKey.OpenSubKey(issue.ParentKeyPath, writable: true);

            if (parentKey is null)
                return new RegistryRemoveOutcome(false, L.T("Registry key no longer exists (already removed?)."), backupOutcome.Path);

            if (issue.ValueNameToDelete is not null && issue.CorrectedValueData is not null)
            {
                // Preserve the original value kind (e.g. REG_EXPAND_SZ) rather than
                // letting SetValue infer REG_SZ from the CLR string type by default -
                // a reset should restore a safe value, not silently change its type.
                RegistryValueKind originalKind;
                try { originalKind = parentKey.GetValueKind(issue.ValueNameToDelete); }
                catch { originalKind = RegistryValueKind.String; }
                parentKey.SetValue(issue.ValueNameToDelete, issue.CorrectedValueData, originalKind);
            }
            else if (issue.Category == RegistryIssueCategory.OrphanedUninstallEntry && issue.SubKeyNameToDelete is not null)
            {
                parentKey.DeleteSubKeyTree(issue.SubKeyNameToDelete, throwOnMissingSubKey: false);
            }
            else if (issue.ValueNameToDelete is not null)
            {
                parentKey.DeleteValue(issue.ValueNameToDelete, throwOnMissingValue: false);
            }
            else
            {
                return new RegistryRemoveOutcome(false, L.T("Issue is missing the information needed to remove it."), backupOutcome.Path);
            }

            return new RegistryRemoveOutcome(true, null, backupOutcome.Path);
        }
        catch (Exception ex)
        {
            return new RegistryRemoveOutcome(false, ex.Message, backupOutcome.Path);
        }
    }

    private IReadOnlyList<RegistryIssue> ScanOrphanedUninstallEntries()
    {
        var issues = new List<RegistryIssue>();

        foreach (var app in _appScanner.ScanInstalledApps())
        {
            if (app.LooksLikeMsi || string.IsNullOrWhiteSpace(app.UninstallString))
                continue; // MSI validity needs a different check (the product database), not attempted here.

            if (app.UninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
                continue;

            var (rawExePath, _) = InstallerTypeDetector.SplitCommandLine(app.UninstallString);

            if (string.IsNullOrWhiteSpace(rawExePath))
                continue; // Couldn't parse a clear path - don't guess, don't flag.

            var exePath = Environment.ExpandEnvironmentVariables(rawExePath);

            // Corroborating evidence: if the app's own InstallLocation still exists and has
            // content, treat that as strong proof the app is still genuinely installed,
            // regardless of what the specific uninstaller path check finds - a mismatched or
            // oddly-formatted UninstallString doesn't mean the program itself is gone.
            if (!string.IsNullOrWhiteSpace(app.InstallLocation) &&
                Directory.Exists(app.InstallLocation) &&
                Directory.EnumerateFileSystemEntries(app.InstallLocation).Any())
                continue;

            if (!IsConfidentlyMissing(exePath))
                continue; // Exists, or we couldn't be sure (e.g. access denied) - never flag on uncertainty.

            var parentPath = app.Scope switch
            {
                RegistryScope.LocalMachine32OnWow64 => @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                _ => @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            issues.Add(new RegistryIssue
            {
                Category = RegistryIssueCategory.OrphanedUninstallEntry,
                DisplayName = app.DisplayName,
                Reason = L.F("Its uninstaller no longer exists on disk: {0}", exePath),
                Scope = app.Scope,
                FullRegistryPath = app.FullRegistryPath,
                ParentKeyPath = parentPath,
                SubKeyNameToDelete = app.RegistryKeyName,
            });
        }

        return issues;
    }

    /// <summary>
    /// True only when we're genuinely confident the file is gone - never on mere
    /// uncertainty. Plain File.Exists() silently swallows every kind of failure
    /// (including access-denied) and returns false for all of them, which is exactly
    /// what caused real installed programs to be misreported as orphaned when
    /// UltraUninstaller wasn't running elevated. This checks explicitly for
    /// "not found" and treats every other outcome - including access denied - as
    /// "cannot confirm, so don't flag it".
    /// </summary>
    private static bool IsConfidentlyMissing(string path)
    {
        if (File.Exists(path))
            return false;

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false; // It opened, so it definitely exists.
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // Exists but we can't read it (or a permissions issue on the folder) - not evidence of absence.
        }
        catch (IOException)
        {
            return false; // E.g. a sharing violation because the file is currently in use - proof it exists.
        }
        catch
        {
            return false; // Any other unexpected failure: fail safe toward NOT flagging.
        }
    }

    private IReadOnlyList<RegistryIssue> ScanOrphanedStartupEntries()
    {
        var issues = new List<RegistryIssue>();

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

                var (rawExePath, _) = InstallerTypeDetector.SplitCommandLine(rawCommand);

                if (string.IsNullOrWhiteSpace(rawExePath) || !rawExePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue; // Couldn't resolve a clear .exe path - don't guess, don't flag.

                var exePath = Environment.ExpandEnvironmentVariables(rawExePath);

                // Only flag entries we can confidently resolve to a specific .exe that's provably gone.
                if (!IsConfidentlyMissing(exePath))
                    continue;

                var hiveName = hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";

                issues.Add(new RegistryIssue
                {
                    Category = RegistryIssueCategory.OrphanedStartupEntry,
                    DisplayName = valueName,
                    Reason = L.F("Startup program no longer exists: {0}", exePath),
                    Scope = scope,
                    FullRegistryPath = $@"{hiveName}\{subPath}",
                    ParentKeyPath = subPath,
                    ValueNameToDelete = valueName,
                });
            }
        }

        return issues;
    }

    /// <summary>
    /// Checks the three well-documented structural hijack techniques: Image File
    /// Execution Options Debugger redirection, Winlogon Shell/Userinit tampering,
    /// and a non-empty AppInit_DLLs. Each has essentially no legitimate use in a
    /// normal consumer Windows install, which is exactly why they're safe to check
    /// unconditionally rather than needing per-app corroboration like the orphan
    /// checks do.
    /// </summary>
    private static IReadOnlyList<RegistryIssue> ScanSecurityReviewIssues()
    {
        var issues = new List<RegistryIssue>();

        // --- Image File Execution Options: Debugger redirection ---
        using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var ifeoKey = baseKey.OpenSubKey(IfeoSubPath))
        {
            if (ifeoKey is not null)
            {
                foreach (var exeName in ifeoKey.GetSubKeyNames())
                {
                    using var exeKey = ifeoKey.OpenSubKey(exeName);
                    var debugger = exeKey?.GetValue("Debugger") as string;
                    if (string.IsNullOrWhiteSpace(debugger))
                        continue;

                    issues.Add(new RegistryIssue
                    {
                        Category = RegistryIssueCategory.SecurityReview,
                        DisplayName = $"Image File Execution Options: {exeName}",
                        Reason = L.F("Windows will launch \"{0}\" instead of {1} whenever it's run. This exact technique (Debugger redirection) is a well-known way to hijack system tools; legitimate software almost never uses it. Review before removing.", debugger, exeName),
                        Scope = RegistryScope.LocalMachine64,
                        FullRegistryPath = $@"HKEY_LOCAL_MACHINE\{IfeoSubPath}\{exeName}",
                        ParentKeyPath = $@"{IfeoSubPath}\{exeName}",
                        ValueNameToDelete = "Debugger",
                    });
                }
            }
        }

        // --- Winlogon: Shell / Userinit tampering ---
        using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var winlogonKey = baseKey.OpenSubKey(WinlogonSubPath))
        {
            var shell = winlogonKey?.GetValue("Shell") as string;
            if (!string.IsNullOrWhiteSpace(shell) && !string.Equals(shell.Trim(), CorrectShell, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new RegistryIssue
                {
                    Category = RegistryIssueCategory.SecurityReview,
                    DisplayName = "Winlogon Shell",
                    Reason = L.F("Windows is set to launch \"{0}\" as your desktop shell instead of the normal explorer.exe. This is a classic technique for replacing or hijacking the desktop. Review before resetting.", shell),
                    Scope = RegistryScope.LocalMachine64,
                    FullRegistryPath = $@"HKEY_LOCAL_MACHINE\{WinlogonSubPath}",
                    ParentKeyPath = WinlogonSubPath,
                    ValueNameToDelete = "Shell",
                    CorrectedValueData = CorrectShell,
                });
            }

            var userinit = winlogonKey?.GetValue("Userinit") as string;
            if (!string.IsNullOrWhiteSpace(userinit) && !string.Equals(userinit.Trim(), CorrectUserinit, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new RegistryIssue
                {
                    Category = RegistryIssueCategory.SecurityReview,
                    DisplayName = "Winlogon Userinit",
                    Reason = L.F("Windows runs \"{0}\" at logon instead of just the normal userinit.exe. Anything appended here runs with your logon - a well-known persistence technique. Review before resetting.", userinit),
                    Scope = RegistryScope.LocalMachine64,
                    FullRegistryPath = $@"HKEY_LOCAL_MACHINE\{WinlogonSubPath}",
                    ParentKeyPath = WinlogonSubPath,
                    ValueNameToDelete = "Userinit",
                    CorrectedValueData = CorrectUserinit,
                });
            }
        }

        // --- AppInit_DLLs ---
        using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
        using (var windowsKey = baseKey.OpenSubKey(WindowsNtSubPath))
        {
            var appInitDlls = windowsKey?.GetValue("AppInit_DLLs") as string;
            if (!string.IsNullOrWhiteSpace(appInitDlls))
            {
                issues.Add(new RegistryIssue
                {
                    Category = RegistryIssueCategory.SecurityReview,
                    DisplayName = "AppInit_DLLs",
                    Reason = L.F("\"{0}\" is set to load into nearly every program that starts on this system. This mechanism is deprecated and almost never used legitimately today - it's a classic DLL injection technique. Review before clearing.", appInitDlls),
                    Scope = RegistryScope.LocalMachine64,
                    FullRegistryPath = $@"HKEY_LOCAL_MACHINE\{WindowsNtSubPath}",
                    ParentKeyPath = WindowsNtSubPath,
                    ValueNameToDelete = "AppInit_DLLs",
                    CorrectedValueData = string.Empty,
                });
            }
        }

        return issues;
    }

    /// <summary>
    /// Checks each Run-at-startup command against a short list of well-documented
    /// "living off the land" abuse patterns (encoded PowerShell, mshta/regsvr32/
    /// certutil fetching from a URL). This is pattern matching, not a malware
    /// signature database - real, specific, and low-noise, but still just a
    /// pattern, so every hit is framed as something to review, not a verdict.
    /// </summary>
    private static IReadOnlyList<RegistryIssue> ScanSuspiciousStartupCommands()
    {
        var issues = new List<RegistryIssue>();

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

                var match = SuspiciousCommandPatterns.FirstOrDefault(p => p.IsMatch(rawCommand));
                if (match.Label is null)
                    continue;

                var hiveName = hive == RegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER";

                issues.Add(new RegistryIssue
                {
                    Category = RegistryIssueCategory.SuspiciousStartupCommand,
                    DisplayName = valueName,
                    Reason = L.F("This startup entry uses {0}: {1}", match.Label, rawCommand),
                    Scope = scope,
                    FullRegistryPath = $@"{hiveName}\{subPath}",
                    ParentKeyPath = subPath,
                    ValueNameToDelete = valueName,
                });
            }
        }

        return issues;
    }

    private static RegistryKey OpenBaseKey(RegistryScope scope) => scope switch
    {
        RegistryScope.LocalMachine64 => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        RegistryScope.LocalMachine32OnWow64 => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32),
        RegistryScope.CurrentUser => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };
}

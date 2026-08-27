using System.Runtime.Versioning;
using UltraUninstaller.Core.Models;

namespace UltraUninstaller.Core.Services;

/// <summary>
/// Combines what Startup Manager and Registry Cleaner already find into one
/// transparent score - not a new detection engine of its own, and not a black-box
/// number. Every point lost is shown with the specific, named reason for it,
/// because a score nobody can explain is exactly the kind of thing this whole
/// project has been deliberately avoiding (see Registry Cleaner's "not a padded
/// problem count" design). Disk space is deliberately left out of the score: there
/// is no honest, already-built way to say a given amount of disk usage is
/// "unhealthy" without a real junk-file classifier, which doesn't exist yet - the
/// Disk Space Analyzer stays a browsing tool, not a health signal, until that's true.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SystemHealthService
{
    private readonly StartupManagerService _startupManager;
    private readonly RegistryCleanerService _registryCleaner;

    private const int BaselineHealthyStartupCount = 5;
    private const int PointsPerExtraStartupItem = 2;
    private const int MaxStartupPenalty = 30;

    private const int PointsPerSecurityReviewIssue = 15;
    private const int MaxSecurityPenalty = 60;

    private const int PointsPerOrphanedEntry = 1;
    private const int MaxOrphanedPenalty = 10;

    public SystemHealthService(StartupManagerService? startupManager = null, RegistryCleanerService? registryCleaner = null)
    {
        _startupManager = startupManager ?? new StartupManagerService();
        _registryCleaner = registryCleaner ?? new RegistryCleanerService();
    }

    public async Task<SystemHealthReport> RunAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report("Checking startup items");
        var startupItems = await _startupManager.ScanAsync(
            new Progress<string>(m => progress?.Report(m)), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report("Checking the registry");
        var registryIssues = await _registryCleaner.ScanAsync(
            new Progress<string>(m => progress?.Report(m)), cancellationToken);

        var enabledStartupCount = startupItems.Count(i => i.IsEnabled);
        var securityIssueCount = registryIssues.Count(i =>
            i.Category is RegistryIssueCategory.SecurityReview or RegistryIssueCategory.SuspiciousStartupCommand);
        var orphanedCount = registryIssues.Count(i =>
            i.Category is RegistryIssueCategory.OrphanedUninstallEntry or RegistryIssueCategory.OrphanedStartupEntry);

        var factors = new List<HealthScoreFactor>();
        var score = 100;

        if (enabledStartupCount > BaselineHealthyStartupCount)
        {
            var extra = enabledStartupCount - BaselineHealthyStartupCount;
            var penalty = Math.Min(extra * PointsPerExtraStartupItem, MaxStartupPenalty);
            score -= penalty;
            factors.Add(new HealthScoreFactor(
                "Startup items",
                penalty,
                $"{enabledStartupCount} things launch at startup or logon ({extra} more than the {BaselineHealthyStartupCount} baseline) - each one adds to your boot time."));
        }

        if (securityIssueCount > 0)
        {
            var penalty = Math.Min(securityIssueCount * PointsPerSecurityReviewIssue, MaxSecurityPenalty);
            score -= penalty;
            factors.Add(new HealthScoreFactor(
                "Security review items",
                penalty,
                $"{securityIssueCount} registry item(s) match a known persistence/hijack pattern and are worth reviewing in Registry Cleaner."));
        }

        if (orphanedCount > 0)
        {
            var penalty = Math.Min(orphanedCount * PointsPerOrphanedEntry, MaxOrphanedPenalty);
            score -= penalty;
            factors.Add(new HealthScoreFactor(
                "Leftover registry entries",
                penalty,
                $"{orphanedCount} orphaned registry entr{(orphanedCount == 1 ? "y" : "ies")} from programs no longer fully installed - mostly cosmetic clutter, not a real slowdown."));
        }

        score = Math.Clamp(score, 0, 100);

        var grade = score switch
        {
            >= 90 => HealthGrade.A,
            >= 75 => HealthGrade.B,
            >= 60 => HealthGrade.C,
            >= 40 => HealthGrade.D,
            _ => HealthGrade.F,
        };

        return new SystemHealthReport
        {
            Score = score,
            Grade = grade,
            Factors = factors,
            EnabledStartupItemCount = enabledStartupCount,
            SecurityReviewIssueCount = securityIssueCount,
            OrphanedRegistryIssueCount = orphanedCount,
        };
    }
}

namespace UltraUninstaller.Core.Models;

/// <summary>
/// One factor that contributed to the health score - always shown alongside the
/// score itself, never hidden behind it. The whole point is that the number is
/// explainable: every point lost has a specific, named reason attached.
/// </summary>
public sealed record HealthScoreFactor(string Label, int PointsDeducted, string Detail);

public enum HealthGrade { A, B, C, D, F }

public sealed class SystemHealthReport
{
    public required int Score { get; init; }
    public required HealthGrade Grade { get; init; }
    public required IReadOnlyList<HealthScoreFactor> Factors { get; init; }

    public required int EnabledStartupItemCount { get; init; }
    public required int SecurityReviewIssueCount { get; init; }
    public required int OrphanedRegistryIssueCount { get; init; }

    public string GradeDisplay => Grade switch
    {
        HealthGrade.A => "A - Looking good",
        HealthGrade.B => "B - A few things worth a look",
        HealthGrade.C => "C - Several things worth cleaning up",
        HealthGrade.D => "D - Quite a bit slowing you down",
        HealthGrade.F => "F - A lot going on here",
        _ => "Unknown",
    };
}

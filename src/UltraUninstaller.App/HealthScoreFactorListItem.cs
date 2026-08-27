using UltraUninstaller.Core.Models;

namespace UltraUninstaller.App;

public sealed class HealthScoreFactorListItem
{
    public HealthScoreFactorListItem(HealthScoreFactor factor)
    {
        Factor = factor;
    }

    public HealthScoreFactor Factor { get; }

    public string Label => Factor.Label;
    public string Detail => Factor.Detail;
    public string PointsDisplay => $"-{Factor.PointsDeducted}";

    /// <summary>
    /// What a screen reader announces for this row - bound via ItemContainerStyle,
    /// since WPF's ListView+GridView doesn't give rows a meaningful accessible Name
    /// on its own and NVDA (unlike JAWS) needs one explicitly set to announce anything.
    /// </summary>
    public string AutomationName => $"{Label}, {PointsDisplay} points. {Detail}";
}

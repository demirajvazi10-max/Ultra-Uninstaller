using UltraUninstaller.Core.Models;

namespace UltraUninstaller.App;

public sealed class RegistryIssueListItem
{
    public RegistryIssueListItem(RegistryIssue issue)
    {
        Issue = issue;
    }

    public RegistryIssue Issue { get; }

    public string DisplayName => Issue.DisplayName;
    public string Reason => Issue.Reason;

    public string CategoryDisplay => Issue.Category switch
    {
        RegistryIssueCategory.OrphanedUninstallEntry => "Orphaned Uninstall entry",
        RegistryIssueCategory.OrphanedStartupEntry => "Orphaned Startup entry",
        RegistryIssueCategory.SecurityReview => "Security review - known hijack technique",
        RegistryIssueCategory.SuspiciousStartupCommand => "Security review - suspicious command",
        _ => "Unknown",
    };

    /// <summary>
    /// What a screen reader announces for this row - bound via ItemContainerStyle,
    /// since WPF's ListView+GridView doesn't give rows a meaningful accessible Name
    /// on its own and NVDA (unlike JAWS) needs one explicitly set to announce anything.
    /// </summary>
    public string AutomationName => $"{CategoryDisplay}: {DisplayName}. {Reason}";
}

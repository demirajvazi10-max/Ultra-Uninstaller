using UltraUninstaller.Core.Models;

namespace UltraUninstaller.App;

public sealed class StartupItemListItem
{
    public StartupItemListItem(StartupItem item)
    {
        Item = item;
    }

    public StartupItem Item { get; }

    public string Name => Item.Name;
    public bool IsEnabled => Item.IsEnabled;

    public string SourceDisplay => Item.Source switch
    {
        StartupSource.RegistryRun => "Registry (Run at startup)",
        StartupSource.StartupFolder => "Startup folder shortcut",
        StartupSource.ScheduledTask => "Scheduled task (runs at logon)",
        StartupSource.AutoStartService => "Service (starts automatically)",
        _ => "Unknown",
    };

    public string StatusDisplay => Item.IsEnabled ? "Enabled" : "Disabled";

    public string TrustDisplay
    {
        get
        {
            if (Item.IsSigned == true)
                return "Signed";

            return Item.IsInUnusualLocation
                ? "Unsigned - unusual location, review before trusting"
                : "Unsigned";
        }
    }

    public string DescriptionDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Item.Description) && !string.IsNullOrWhiteSpace(Item.Publisher))
                return $"{Item.Description} ({Item.Publisher})";
            return Item.Description ?? Item.Publisher ?? Item.FilePath ?? string.Empty;
        }
    }

    /// <summary>
    /// What a screen reader announces for this row - bound via ItemContainerStyle,
    /// since WPF's ListView+GridView doesn't give rows a meaningful accessible Name
    /// on its own and NVDA (unlike JAWS) needs one explicitly set to announce anything.
    /// </summary>
    public string AutomationName => string.Join(", ", new[]
    {
        Name,
        SourceDisplay,
        StatusDisplay,
        TrustDisplay,
        DescriptionDisplay,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

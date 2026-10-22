using UltraUninstaller.Core.Localization;
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
        StartupSource.RegistryRun => L.T("Registry (Run at startup)"),
        StartupSource.StartupFolder => L.T("Startup folder shortcut"),
        StartupSource.ScheduledTask => L.T("Scheduled task (runs at logon)"),
        StartupSource.AutoStartService => L.T("Service (starts automatically)"),
        _ => L.T("Unknown"),
    };

    public string StatusDisplay => Item.IsEnabled ? L.T("Enabled") : L.T("Disabled");

    public string TrustDisplay
    {
        get
        {
            if (Item.IsSigned == true)
                return L.T("Signed");

            return Item.IsInUnusualLocation
                ? L.T("Unsigned - unusual location, review before trusting")
                : L.T("Unsigned");
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

using UltraUninstaller.Core.Models;

namespace UltraUninstaller.App;

/// <summary>
/// Thin wrapper around Core's InstalledApp that adds UI-only formatted fields.
/// Kept separate from the Core model so Core stays free of any presentation concerns.
/// </summary>
public sealed class InstalledAppListItem
{
    public InstalledAppListItem(InstalledApp app)
    {
        App = app;
    }

    public InstalledApp App { get; }

    public string DisplayName => App.DisplayName;
    public string? DisplayVersion => App.DisplayVersion;
    public string? Publisher => App.Publisher;
    public string? InstallLocation => App.InstallLocation;

    public string EstimatedSizeDisplay =>
        App.EstimatedSizeKb is long kb
            ? $"{kb / 1024.0:0.#} MB"
            : "Unknown";

    /// <summary>
    /// What a screen reader announces for this row. WPF's ListView+GridView does not
    /// automatically give each row a meaningful accessible Name - JAWS tends to read
    /// the visible column text anyway, but NVDA strictly needs this set explicitly or
    /// it announces nothing useful for the row. Bound via ItemContainerStyle.
    /// </summary>
    public string AutomationName => string.Join(", ", new[]
    {
        DisplayName,
        !string.IsNullOrWhiteSpace(DisplayVersion) ? $"version {DisplayVersion}" : null,
        !string.IsNullOrWhiteSpace(Publisher) ? $"publisher {Publisher}" : null,
        EstimatedSizeDisplay,
        !string.IsNullOrWhiteSpace(InstallLocation) ? $"installed at {InstallLocation}" : null,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

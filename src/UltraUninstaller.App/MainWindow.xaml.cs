using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Services;

namespace UltraUninstaller.App;

public partial class MainWindow : Window
{
    private readonly InstalledAppScanner _scanner = new();
    private readonly ForceUninstallOrchestrator _orchestrator = new();
    private readonly ShellIntegrationService _shellIntegration = new();

    public MainWindow()
    {
        InitializeComponent();
        LoadAllApps();
        SyncShellIntegrationMenuState();

        // Right-click on an unselected row should select just that row (like Explorer),
        // so the context menu always acts on the item under the mouse pointer.
        AppListView.PreviewMouseRightButtonDown += AppListView_PreviewMouseRightButtonDown;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadAllApps();

    private void SearchButton_Click(object sender, RoutedEventArgs e) => RunSearch();

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            RunSearch();
    }

    private void AppListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UninstallButton.IsEnabled = AppListView.SelectedItems.Count > 0;
    }

    private void AppListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = (DependencyObject)e.OriginalSource;
        while (dep is not null && dep is not ListViewItem)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is ListViewItem { IsSelected: false } item)
        {
            AppListView.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private void RunSearch()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;

        SetStatus(string.IsNullOrEmpty(query) ? "Loading all installed programs." : $"Searching for {query}.");

        var results = string.IsNullOrEmpty(query)
            ? _scanner.ScanInstalledApps()
            : _scanner.FindByName(query);

        AppListView.ItemsSource = results.Select(a => new InstalledAppListItem(a)).ToList();

        SetStatus($"Found {results.Count} program{(results.Count == 1 ? "" : "s")}.");
    }

    private void LoadAllApps()
    {
        SetStatus("Loading installed programs.");
        var results = _scanner.ScanInstalledApps();
        AppListView.ItemsSource = results.Select(a => new InstalledAppListItem(a)).ToList();
        SetStatus($"{results.Count} installed programs loaded.");
    }

    private List<InstalledAppListItem> GetSelectedApps() =>
        AppListView.SelectedItems.Cast<InstalledAppListItem>().ToList();

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedApps();
        if (selected.Count == 0)
            return;

        await UninstallItemsAsync(selected, forceAllowed: AllowForceRemovalCheckBox.IsChecked == true);
    }

    // --- Context menu handlers ---------------------------------------------------

    private async void ContextUninstallSilent_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedApps();
        if (selected.Count == 0)
        {
            SetStatus("No program selected.");
            return;
        }

        await UninstallItemsAsync(selected, forceAllowed: AllowForceRemovalCheckBox.IsChecked == true);
    }

    private async void ContextForceUninstall_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedApps();
        if (selected.Count == 0)
        {
            SetStatus("No program selected.");
            return;
        }

        // Force uninstall still tries the silent method first (Layer 1/2) - "force" here
        // means Layer 3 (manual removal) is allowed to run if that fails, regardless of
        // the checkbox state, not that it skips straight to deleting things.
        await UninstallItemsAsync(selected, forceAllowed: true);
    }

    private void ContextOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedApps();
        if (selected.Count != 1)
        {
            SetStatus("Select exactly one program to open its install folder.");
            return;
        }

        var folder = selected[0].InstallLocation;
        if (string.IsNullOrWhiteSpace(folder) || !System.IO.Directory.Exists(folder))
        {
            SetStatus($"Install folder is not known or no longer exists for {selected[0].DisplayName}.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            SetStatus($"Opened install folder for {selected[0].DisplayName}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open folder: {ex.Message}");
        }
    }

    private void ContextCopyUninstallString_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedApps();
        if (selected.Count != 1)
        {
            SetStatus("Select exactly one program to copy its uninstall string.");
            return;
        }

        var app = selected[0].App;
        var value = app.UninstallString ?? app.QuietUninstallString;

        if (string.IsNullOrWhiteSpace(value))
        {
            SetStatus($"{app.DisplayName} has no uninstall string in the registry.");
            return;
        }

        Clipboard.SetText(value);
        SetStatus($"Copied uninstall string for {app.DisplayName} to the clipboard.");
    }

    private void ContextShowDetails_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedApps();
        if (selected.Count != 1)
        {
            SetStatus("Select exactly one program to show its details.");
            return;
        }

        var app = selected[0].App;
        var details = string.Join(Environment.NewLine, new[]
        {
            $"Name: {app.DisplayName}",
            $"Version: {app.DisplayVersion ?? "(unknown)"}",
            $"Publisher: {app.Publisher ?? "(unknown)"}",
            $"Install location: {app.InstallLocation ?? "(unknown)"}",
            $"Estimated size: {(app.EstimatedSizeKb is long kb ? $"{kb / 1024.0:0.#} MB" : "(unknown)")}",
            $"Install date: {app.InstallDate ?? "(unknown)"}",
            $"Uninstall string: {app.UninstallString ?? "(none)"}",
            $"Quiet uninstall string: {app.QuietUninstallString ?? "(none)"}",
            $"Registry path: {app.FullRegistryPath}",
        });

        MessageBox.Show(details, $"Details - {app.DisplayName}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // --- File menu handlers ---------------------------------------------------------

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "UltraUninstaller\n" +
            "Part of the Ultra Creative Suite, by Demir Ajvazi.\n\n" +
            "An accessible Windows uninstaller and cleaner, built screen-reader-first.",
            "About UltraUninstaller",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    // --- Tools menu handlers -------------------------------------------------------

    private void SystemHealthMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = new HealthDashboardWindow { Owner = this };
        window.Show();
    }

    private void DiskSpaceAnalyzerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = new DiskAnalyzerWindow { Owner = this };
        window.Show();
    }

    private void RegistryCleanerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = new RegistryCleanerWindow { Owner = this };
        window.Show();
    }

    private void StartupManagerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = new StartupManagerWindow { Owner = this };
        window.Show();
    }

    private void JunkCleanerMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var window = new JunkCleanerWindow { Owner = this };
        window.Show();
    }

    private void ShellIntegrationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_shellIntegration.IsRegistered())
            {
                _shellIntegration.Unregister();
                SetStatus("Removed 'Analyze size with UltraUninstaller' from the Explorer right-click menu.");
            }
            else
            {
                var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    SetStatus("Could not determine UltraUninstaller's own path to register.");
                    return;
                }

                _shellIntegration.Register(exePath);
                SetStatus("Added 'Analyze size with UltraUninstaller' to the Explorer right-click menu on folders.");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not change Explorer integration: {ex.Message}");
        }

        SyncShellIntegrationMenuState();
    }

    private void SyncShellIntegrationMenuState()
    {
        try
        {
            ShellIntegrationMenuItem.IsChecked = _shellIntegration.IsRegistered();
        }
        catch
        {
            // Non-fatal — leave the menu item in its last known state if this check fails.
        }
    }

    // --- Shared uninstall pipeline --------------------------------------------------

    private async Task UninstallItemsAsync(List<InstalledAppListItem> selected, bool forceAllowed)
    {
        var names = string.Join(", ", selected.Select(s => s.DisplayName));
        var confirmMessage = selected.Count == 1
            ? $"Uninstall {names}?"
            : $"Uninstall {selected.Count} selected programs: {names}?";

        var confirm = MessageBox.Show(confirmMessage, "Confirm uninstall", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus("Uninstall cancelled.");
            return;
        }

        UninstallButton.IsEnabled = false;
        SearchButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        StepLogList.Items.Clear();

        var options = new ForceUninstallOptions { AllowForceRemoval = forceAllowed };

        foreach (var item in selected)
        {
            SetStatus($"Starting uninstall of {item.DisplayName}.");

            var result = await _orchestrator.UninstallAsync(item.App, options, step =>
            {
                // Steps arrive on a background thread; marshal back to the UI thread
                // both to update the log and to fire the live-region announcement.
                Dispatcher.Invoke(() =>
                {
                    var line = string.IsNullOrEmpty(step.Detail) ? step.Description : $"{step.Description} ({step.Detail})";
                    StepLogList.Items.Add(line);
                    SetStatus(step.Description);
                });
            });

            SetStatus(result.Success
                ? $"{item.DisplayName} was uninstalled successfully using {DescribeLayer(result.LayerUsed)}."
                : $"{item.DisplayName} could not be fully uninstalled. {result.FailureReason}");
        }

        SearchButton.IsEnabled = true;
        RefreshButton.IsEnabled = true;

        LoadAllApps();
    }

    private static string DescribeLayer(UninstallLayer layer) => layer switch
    {
        UninstallLayer.NativeSilent => "its own silent uninstaller",
        UninstallLayer.MsiQuiet => "a quiet Windows Installer removal",
        UninstallLayer.NativeWithInferredSilentSwitch => "a silent switch UltraUninstaller added",
        UninstallLayer.ForceRemoval => "force removal",
        _ => "an unknown method",
    };

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}

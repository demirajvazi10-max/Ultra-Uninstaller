using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltraUninstaller.Core.Localization;
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
        SyncLanguageMenuState();

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

        SetStatus(string.IsNullOrEmpty(query) ? L.T("Loading all installed programs.") : L.F("Searching for {0}.", query));

        var results = string.IsNullOrEmpty(query)
            ? _scanner.ScanInstalledApps()
            : _scanner.FindByName(query);

        AppListView.ItemsSource = results.Select(a => new InstalledAppListItem(a)).ToList();

        SetStatus(L.N(results.Count, "Found {0} program.", "Found {0} programs."));
    }

    private void LoadAllApps()
    {
        SetStatus(L.T("Loading installed programs."));
        var results = _scanner.ScanInstalledApps();
        AppListView.ItemsSource = results.Select(a => new InstalledAppListItem(a)).ToList();
        SetStatus(L.N(results.Count, "{0} installed program loaded.", "{0} installed programs loaded."));
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
            SetStatus(L.T("No program selected."));
            return;
        }

        await UninstallItemsAsync(selected, forceAllowed: AllowForceRemovalCheckBox.IsChecked == true);
    }

    private async void ContextForceUninstall_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedApps();
        if (selected.Count == 0)
        {
            SetStatus(L.T("No program selected."));
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
            SetStatus(L.T("Select exactly one program to open its install folder."));
            return;
        }

        var folder = selected[0].InstallLocation;
        if (string.IsNullOrWhiteSpace(folder) || !System.IO.Directory.Exists(folder))
        {
            SetStatus(L.F("Install folder is not known or no longer exists for {0}.", selected[0].DisplayName));
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            SetStatus(L.F("Opened install folder for {0}.", selected[0].DisplayName));
        }
        catch (Exception ex)
        {
            SetStatus(L.F("Could not open folder: {0}", ex.Message));
        }
    }

    private void ContextCopyUninstallString_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedApps();
        if (selected.Count != 1)
        {
            SetStatus(L.T("Select exactly one program to copy its uninstall string."));
            return;
        }

        var app = selected[0].App;
        var value = app.UninstallString ?? app.QuietUninstallString;

        if (string.IsNullOrWhiteSpace(value))
        {
            SetStatus(L.F("{0} has no uninstall string in the registry.", app.DisplayName));
            return;
        }

        Clipboard.SetText(value);
        SetStatus(L.F("Copied uninstall string for {0} to the clipboard.", app.DisplayName));
    }

    private void ContextShowDetails_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedApps();
        if (selected.Count != 1)
        {
            SetStatus(L.T("Select exactly one program to show its details."));
            return;
        }

        var app = selected[0].App;
        var unknown = L.T("(unknown)");
        var none = L.T("(none)");
        var details = string.Join(Environment.NewLine, new[]
        {
            L.F("Name: {0}", app.DisplayName),
            L.F("Version: {0}", app.DisplayVersion ?? unknown),
            L.F("Publisher: {0}", app.Publisher ?? unknown),
            L.F("Install location: {0}", app.InstallLocation ?? unknown),
            L.F("Estimated size: {0}", app.EstimatedSizeKb is long kb ? string.Format(L.Culture, "{0:0.#} MB", kb / 1024.0) : unknown),
            L.F("Install date: {0}", app.InstallDate ?? unknown),
            L.F("Uninstall string: {0}", app.UninstallString ?? none),
            L.F("Quiet uninstall string: {0}", app.QuietUninstallString ?? none),
            L.F("Registry path: {0}", app.FullRegistryPath),
        });

        MessageBox.Show(details, L.F("Details - {0}", app.DisplayName), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // --- File menu handlers ---------------------------------------------------------

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        new AboutWindow { Owner = this }.ShowDialog();
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
                SetStatus(L.T("Removed 'Analyze size with UltraUninstaller' from the Explorer right-click menu."));
            }
            else
            {
                var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                {
                    SetStatus(L.T("Could not determine UltraUninstaller's own path to register."));
                    return;
                }

                _shellIntegration.Register(exePath);
                SetStatus(L.T("Added 'Analyze size with UltraUninstaller' to the Explorer right-click menu on folders."));
            }
        }
        catch (Exception ex)
        {
            SetStatus(L.F("Could not change Explorer integration: {0}", ex.Message));
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
            ? L.F("Uninstall {0}?", names)
            : L.F("Uninstall {0} selected programs: {1}?", selected.Count, names);

        var confirm = MessageBox.Show(confirmMessage, L.T("Confirm uninstall"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus(L.T("Uninstall cancelled."));
            return;
        }

        UninstallButton.IsEnabled = false;
        SearchButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        StepLogList.Items.Clear();

        var options = new ForceUninstallOptions { AllowForceRemoval = forceAllowed };
        var summaries = new List<string>();

        foreach (var item in selected)
        {
            SetStatus(L.F("Starting uninstall of {0}.", item.DisplayName));

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

            var summary = result.Success
                ? L.F("{0} was uninstalled successfully using {1}.", item.DisplayName, DescribeLayer(result.LayerUsed))
                : L.F("{0} could not be fully uninstalled. {1}", item.DisplayName, result.FailureReason);
            summaries.Add(summary);
            StepLogList.Items.Add(summary);
            SetStatus(summary);
        }

        SearchButton.IsEnabled = true;
        RefreshButton.IsEnabled = true;

        LoadAllApps();

        // Announce the outcome LAST: reloading the list sets its own "N programs loaded"
        // status, which used to replace the result before a screen reader could say it.
        SetStatus(string.Join(" ", summaries));
    }

    private static string DescribeLayer(UninstallLayer layer) => layer switch
    {
        UninstallLayer.NativeSilent => L.T("its own silent uninstaller"),
        UninstallLayer.MsiQuiet => L.T("a quiet Windows Installer removal"),
        UninstallLayer.NativeWithInferredSilentSwitch => L.T("a silent switch UltraUninstaller added"),
        UninstallLayer.ForceRemoval => L.T("force removal"),
        _ => L.T("an unknown method"),
    };

    // --- Language menu ---------------------------------------------------------------

    private void SyncLanguageMenuState()
    {
        EnglishLanguageMenuItem.IsChecked = L.Current == AppLanguage.English;
        SerbianLanguageMenuItem.IsChecked = L.Current == AppLanguage.Serbian;
    }

    private void EnglishLanguageMenuItem_Click(object sender, RoutedEventArgs e) => ChangeLanguage(AppLanguage.English);

    private void SerbianLanguageMenuItem_Click(object sender, RoutedEventArgs e) => ChangeLanguage(AppLanguage.Serbian);

    private void ChangeLanguage(AppLanguage language)
    {
        // Checkable menu items toggle themselves on click; put the real state back first.
        SyncLanguageMenuState();

        if (language == L.Current)
            return;

        try
        {
            LanguageSettings.Save(language);
        }
        catch (Exception ex)
        {
            SetStatus(L.F("Could not save the language choice: {0}", ex.Message));
            return;
        }

        // Asked in the NEW language - the one the user just picked and can presumably read.
        // Every label is built once when a window opens, so a restart is the honest way
        // to switch everything at once.
        var previous = L.Current;
        L.SetLanguage(language);
        var question = L.T("The language will change the next time UltraUninstaller starts. Restart now?");
        var title = L.T("Language");
        L.SetLanguage(previous);

        var answer = MessageBox.Show(question, title, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            SetStatus(L.F("Could not restart automatically: {0}. Please close and open UltraUninstaller again.", ex.Message));
        }
    }

    private void SetStatus(string message)
    {
        LiveAnnouncer.Announce(StatusText, message);
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using UltraUninstaller.Core.Localization;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Services;

namespace UltraUninstaller.App;

public partial class StartupManagerWindow : Window
{
    private readonly StartupManagerService _startupManager = new();
    private readonly ObservableCollection<StartupItemListItem> _items = new();

    public StartupManagerWindow()
    {
        InitializeComponent();
        ResultsListView.ItemsSource = _items;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = ResultsListView.SelectedItems.Count > 0;
        DisableSelectedButton.IsEnabled = hasSelection;
        EnableSelectedButton.IsEnabled = hasSelection;
    }

    private async Task ScanAsync()
    {
        ScanButton.IsEnabled = false;
        _items.Clear();
        StepLogList.Items.Clear();

        var progressWindow = new ScanProgressWindow { Owner = this };
        progressWindow.ReportProgressText(L.T("Starting scan..."));

        var progress = new Progress<string>(message =>
        {
            progressWindow.ReportProgressText(message);
            SetStatus(message);
        });

        var scanTask = _startupManager.ScanAsync(progress, progressWindow.Cts.Token);

        // Same race-safe pattern as Disk Space Analyzer: only close the dialog if we
        // actually opened it (a scan that finishes synchronously has nothing to close).
        var dialogWasShown = false;

        _ = scanTask.ContinueWith(_ =>
        {
            if (dialogWasShown)
            {
                try { progressWindow.Close(); }
                catch { /* Already closing/closed - nothing more to do. */ }
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());

        if (!scanTask.IsCompleted)
        {
            dialogWasShown = true;
            progressWindow.ShowDialog();
        }

        try
        {
            var results = await scanTask;

            foreach (var item in results)
                _items.Add(new StartupItemListItem(item));

            var enabledCount = results.Count(r => r.IsEnabled);
            SetStatus(L.F("Found {0} ({1} enabled, {2} disabled).",
                L.N(results.Count, "{0} startup item", "{0} startup items"), enabledCount, results.Count - enabledCount));
        }
        catch (OperationCanceledException)
        {
            SetStatus(L.T("Scan cancelled."));
        }
        catch (Exception ex)
        {
            SetStatus(L.F("Scan failed: {0}", ex.Message));
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private async void ContextDisableSelected_Click(object sender, RoutedEventArgs e) => await SetSelectedEnabledAsync(false);

    private async void ContextEnableSelected_Click(object sender, RoutedEventArgs e) => await SetSelectedEnabledAsync(true);

    private async void DisableSelectedButton_Click(object sender, RoutedEventArgs e) => await SetSelectedEnabledAsync(false);

    private async void EnableSelectedButton_Click(object sender, RoutedEventArgs e) => await SetSelectedEnabledAsync(true);

    private void ContextShowDetails_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsListView.SelectedItems.Cast<StartupItemListItem>().ToList();
        if (selected.Count != 1)
        {
            SetStatus(L.T("Select exactly one item to show its details."));
            return;
        }

        var item = selected[0].Item;
        var details = string.Join(Environment.NewLine, new[]
        {
            L.F("Name: {0}", item.Name),
            L.F("Source: {0}", selected[0].SourceDisplay),
            L.F("Status: {0}", selected[0].StatusDisplay),
            L.F("Trust: {0}", selected[0].TrustDisplay),
            L.F("Description: {0}", item.Description ?? L.T("(none)")),
            L.F("Publisher: {0}", item.Publisher ?? L.T("(unknown)")),
            L.F("File path: {0}", item.FilePath ?? L.T("(unknown)")),
        });

        MessageBox.Show(details, L.F("Details - {0}", item.Name), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ContextSearchOnline_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsListView.SelectedItems.Cast<StartupItemListItem>().ToList();
        if (selected.Count != 1)
        {
            SetStatus(L.T("Select exactly one item to search online for information about it."));
            return;
        }

        var item = selected[0].Item;
        var searchTerms = string.Join(" ", new[] { item.Name, item.Publisher, "windows startup" }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString(searchTerms);

        try
        {
            // Route through explorer.exe rather than launching the URL directly: when
            // UltraUninstaller is running elevated (as it usually is), a direct
            // ShellExecute of a URL can silently fail to hand off to the browser
            // across the elevation boundary. explorer.exe runs at the normal user
            // integrity level and reliably brokers the handoff.
            Process.Start(new ProcessStartInfo("explorer.exe", url) { UseShellExecute = true });
            SetStatus(L.F("Opened a web search for {0} in your browser.", item.Name));
        }
        catch (Exception ex)
        {
            SetStatus(L.F("Could not open your browser: {0}", ex.Message));
        }
    }

    private async Task SetSelectedEnabledAsync(bool enable)
    {
        var selected = ResultsListView.SelectedItems.Cast<StartupItemListItem>()
            .Where(i => i.IsEnabled != enable) // Skip items already in the requested state.
            .ToList();

        if (selected.Count == 0)
        {
            SetStatus(enable ? L.T("Nothing selected needs to be enabled.") : L.T("Nothing selected needs to be disabled."));
            return;
        }

        var names = string.Join(", ", selected.Select(i => i.Name));
        var confirm = MessageBox.Show(
            (enable
                ? L.F("Enable {0}?", L.N(selected.Count, "{0} item", "{0} items"))
                : L.F("Disable {0}?", L.N(selected.Count, "{0} item", "{0} items"))) + "\n\n" + names + "\n\n" +
            L.T("This can always be reversed the same way."),
            enable ? L.T("Confirm enable") : L.T("Confirm disable"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus(L.T("Cancelled."));
            return;
        }

        DisableSelectedButton.IsEnabled = false;
        EnableSelectedButton.IsEnabled = false;
        StepLogList.Items.Clear();

        foreach (var item in selected)
        {
            SetStatus(enable ? L.F("Enabling {0}...", item.Name) : L.F("Disabling {0}...", item.Name));

            var outcome = await _startupManager.SetEnabledAsync(item.Item, enable);

            StepLogList.Items.Add(outcome.Success
                ? $"{item.Name}: {(enable ? L.T("enabled") : L.T("disabled"))}"
                : L.F("{0}: FAILED - {1}", item.Name, outcome.Error));
        }

        SetStatus(L.T("Done."));
        await ScanAsync();
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}

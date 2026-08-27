using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
        progressWindow.ReportProgressText("Starting scan...");

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
            SetStatus($"Found {results.Count} startup item{(results.Count == 1 ? "" : "s")} " +
                      $"({enabledCount} enabled, {results.Count - enabledCount} disabled).");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Scan cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus($"Scan failed: {ex.Message}");
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
            SetStatus("Select exactly one item to show its details.");
            return;
        }

        var item = selected[0].Item;
        var details = string.Join(Environment.NewLine, new[]
        {
            $"Name: {item.Name}",
            $"Source: {selected[0].SourceDisplay}",
            $"Status: {selected[0].StatusDisplay}",
            $"Trust: {selected[0].TrustDisplay}",
            $"Description: {item.Description ?? "(none)"}",
            $"Publisher: {item.Publisher ?? "(unknown)"}",
            $"File path: {item.FilePath ?? "(unknown)"}",
        });

        MessageBox.Show(details, $"Details - {item.Name}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ContextSearchOnline_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsListView.SelectedItems.Cast<StartupItemListItem>().ToList();
        if (selected.Count != 1)
        {
            SetStatus("Select exactly one item to search online for information about it.");
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
            SetStatus($"Opened a web search for {item.Name} in your browser.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open your browser: {ex.Message}");
        }
    }

    private async Task SetSelectedEnabledAsync(bool enable)
    {
        var selected = ResultsListView.SelectedItems.Cast<StartupItemListItem>()
            .Where(i => i.IsEnabled != enable) // Skip items already in the requested state.
            .ToList();

        if (selected.Count == 0)
        {
            SetStatus($"Nothing selected needs to be {(enable ? "enabled" : "disabled")}.");
            return;
        }

        var names = string.Join(", ", selected.Select(i => i.Name));
        var confirm = MessageBox.Show(
            $"{(enable ? "Enable" : "Disable")} {selected.Count} item(s)?\n\n{names}\n\n" +
            "This can always be reversed the same way.",
            enable ? "Confirm enable" : "Confirm disable",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus("Cancelled.");
            return;
        }

        DisableSelectedButton.IsEnabled = false;
        EnableSelectedButton.IsEnabled = false;
        StepLogList.Items.Clear();

        foreach (var item in selected)
        {
            SetStatus($"{(enable ? "Enabling" : "Disabling")} {item.Name}...");

            var outcome = await _startupManager.SetEnabledAsync(item.Item, enable);

            StepLogList.Items.Add(outcome.Success
                ? $"{item.Name}: {(enable ? "enabled" : "disabled")}"
                : $"{item.Name}: FAILED - {outcome.Error}");
        }

        SetStatus("Done.");
        await ScanAsync();
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}

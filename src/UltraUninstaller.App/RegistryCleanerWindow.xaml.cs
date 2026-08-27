using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Services;

namespace UltraUninstaller.App;

public partial class RegistryCleanerWindow : Window
{
    private readonly RegistryCleanerService _cleaner = new();
    private readonly ObservableCollection<RegistryIssueListItem> _items = new();

    public RegistryCleanerWindow()
    {
        InitializeComponent();
        ResultsListView.ItemsSource = _items;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemoveSelectedButton.IsEnabled = ResultsListView.SelectedItems.Count > 0;
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

        var scanTask = _cleaner.ScanAsync(progress, progressWindow.Cts.Token);

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
            var issues = await scanTask;

            foreach (var issue in issues)
                _items.Add(new RegistryIssueListItem(issue));

            SetStatus(issues.Count == 0
                ? "No issues found. The registry looks clean for the categories this tool checks."
                : $"Found {issues.Count} issue{(issues.Count == 1 ? "" : "s")}. Review them before removing - each is backed up automatically when removed.");
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

    private async void ContextRemoveSelected_Click(object sender, RoutedEventArgs e) => await RemoveSelectedAsync();

    private async void RemoveSelectedButton_Click(object sender, RoutedEventArgs e) => await RemoveSelectedAsync();

    private void ContextShowDetails_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsListView.SelectedItems.Cast<RegistryIssueListItem>().ToList();
        if (selected.Count != 1)
        {
            SetStatus("Select exactly one issue to show its details.");
            return;
        }

        var issue = selected[0].Issue;
        var details = string.Join(Environment.NewLine, new[]
        {
            $"Category: {selected[0].CategoryDisplay}",
            $"Name: {issue.DisplayName}",
            $"Reason: {issue.Reason}",
            $"Registry path: {issue.FullRegistryPath}",
            issue.ValueNameToDelete is not null
                ? issue.CorrectedValueData is not null
                    ? $"Value to reset: {issue.ValueNameToDelete} (back to \"{issue.CorrectedValueData}\")"
                    : $"Value to remove: {issue.ValueNameToDelete}"
                : $"Key to remove: {issue.SubKeyNameToDelete}",
        });

        MessageBox.Show(details, $"Details - {issue.DisplayName}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ContextSearchOnline_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsListView.SelectedItems.Cast<RegistryIssueListItem>().ToList();
        if (selected.Count != 1)
        {
            SetStatus("Select exactly one issue to search online for information about it.");
            return;
        }

        var issue = selected[0].Issue;
        var url = "https://www.bing.com/search?q=" + Uri.EscapeDataString($"{issue.DisplayName} windows program");

        try
        {
            // Route through explorer.exe rather than launching the URL directly: when
            // UltraUninstaller is running elevated (as it usually is), a direct
            // ShellExecute of a URL can silently fail to hand off to the browser
            // across the elevation boundary. explorer.exe runs at the normal user
            // integrity level and reliably brokers the handoff.
            Process.Start(new ProcessStartInfo("explorer.exe", url) { UseShellExecute = true });
            SetStatus($"Opened a web search for {issue.DisplayName} in your browser.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open your browser: {ex.Message}");
        }
    }

    private async Task RemoveSelectedAsync()
    {
        var selected = ResultsListView.SelectedItems.Cast<RegistryIssueListItem>().ToList();
        if (selected.Count == 0)
        {
            SetStatus("Nothing is selected. Select one or more issues first.");
            return;
        }

        var anyResets = selected.Any(i => i.Issue.CorrectedValueData is not null);
        var names = string.Join(", ", selected.Select(i => i.DisplayName));
        var actionWord = anyResets ? "Remove/reset" : "Remove";
        var confirm = MessageBox.Show(
            $"{actionWord} {selected.Count} registry item(s)?\n\n{names}\n\n" +
            "Each one is backed up as a .reg file first, so it can always be restored by double-clicking the backup file. " +
            (anyResets ? "Some of these are reset to Windows' normal safe value rather than deleted outright." : ""),
            "Confirm removal",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus("Cancelled.");
            return;
        }

        RemoveSelectedButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        StepLogList.Items.Clear();

        var removedItems = new List<RegistryIssueListItem>();

        foreach (var item in selected)
        {
            var isReset = item.Issue.CorrectedValueData is not null;
            SetStatus($"{(isReset ? "Resetting" : "Removing")} {item.DisplayName}...");

            var outcome = await _cleaner.RemoveAsync(item.Issue);
            var verb = isReset ? "reset to a safe default" : "removed";

            var line = outcome.Success
                ? $"{item.DisplayName}: {verb}" + (outcome.BackupPath is not null ? $" (backed up to {outcome.BackupPath})" : "")
                : $"{item.DisplayName}: FAILED - {outcome.Error}";

            StepLogList.Items.Add(line);
            SetStatus(outcome.Success ? $"{item.DisplayName} {verb}." : $"{item.DisplayName} could not be changed: {outcome.Error}");

            if (outcome.Success)
                removedItems.Add(item);
        }

        foreach (var removed in removedItems)
            _items.Remove(removed);

        ScanButton.IsEnabled = true;

        SetStatus($"Done. {removedItems.Count} of {selected.Count} item(s) changed.");
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}

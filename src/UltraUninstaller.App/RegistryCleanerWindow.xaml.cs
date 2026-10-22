using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using UltraUninstaller.Core.Localization;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Services;

namespace UltraUninstaller.App;

public partial class RegistryCleanerWindow : Window
{
    private readonly RegistryCleanerService _cleaner = new();
    private readonly ObservableCollection<RegistryIssueListItem> _items = new();

    /// <summary>Everything the last scan found, including ignored items (the list shows a filtered view).</summary>
    private List<RegistryIssue> _allIssues = new();

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
        progressWindow.ReportProgressText(L.T("Starting scan..."));

        var progress = new Progress<string>(message =>
        {
            progressWindow.ReportProgressText(message);
            SetStatus(message);
        });

        var scanTask = _cleaner.ScanAsync(progress, progressWindow.Cts.Token, includeIgnored: true);

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
            _allIssues = (await scanTask).ToList();
            RefreshList();
            SetStatus(BuildScanSummary());
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

    private async void ContextRemoveSelected_Click(object sender, RoutedEventArgs e) => await RemoveSelectedAsync();

    private async void RemoveSelectedButton_Click(object sender, RoutedEventArgs e) => await RemoveSelectedAsync();

    private void ContextShowDetails_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsListView.SelectedItems.Cast<RegistryIssueListItem>().ToList();
        if (selected.Count != 1)
        {
            SetStatus(L.T("Select exactly one issue to show its details."));
            return;
        }

        var issue = selected[0].Issue;
        var details = string.Join(Environment.NewLine, new[]
        {
            L.F("Category: {0}", selected[0].CategoryDisplay),
            L.F("Name: {0}", issue.DisplayName),
            L.F("Reason: {0}", issue.Reason),
            L.F("Registry path: {0}", issue.FullRegistryPath),
            issue.ValueNameToDelete is not null
                ? issue.CorrectedValueData is not null
                    ? L.F("Value to reset: {0} (back to \"{1}\")", issue.ValueNameToDelete, issue.CorrectedValueData)
                    : L.F("Value to remove: {0}", issue.ValueNameToDelete)
                : L.F("Key to remove: {0}", issue.SubKeyNameToDelete),
        });

        MessageBox.Show(details, L.F("Details - {0}", issue.DisplayName), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ContextSearchOnline_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsListView.SelectedItems.Cast<RegistryIssueListItem>().ToList();
        if (selected.Count != 1)
        {
            SetStatus(L.T("Select exactly one issue to search online for information about it."));
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
            SetStatus(L.F("Opened a web search for {0} in your browser.", issue.DisplayName));
        }
        catch (Exception ex)
        {
            SetStatus(L.F("Could not open your browser: {0}", ex.Message));
        }
    }

    private async Task RemoveSelectedAsync()
    {
        var selected = ResultsListView.SelectedItems.Cast<RegistryIssueListItem>().ToList();
        if (selected.Count == 0)
        {
            SetStatus(L.T("Nothing is selected. Select one or more issues first."));
            return;
        }

        var anyResets = selected.Any(i => i.Issue.CorrectedValueData is not null);
        var names = string.Join(", ", selected.Select(i => i.DisplayName));
        var countText = L.N(selected.Count, "{0} registry item", "{0} registry items");
        var question = anyResets ? L.F("Remove or reset {0}?", countText) : L.F("Remove {0}?", countText);
        var confirm = MessageBox.Show(
            question + "\n\n" + names + "\n\n" +
            L.T("Each one is backed up as a .reg file first, so it can always be restored by double-clicking the backup file.") +
            (anyResets ? " " + L.T("Some of these are reset to Windows' normal safe value rather than deleted outright.") : ""),
            L.T("Confirm removal"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus(L.T("Cancelled."));
            return;
        }

        RemoveSelectedButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        StepLogList.Items.Clear();

        var removedItems = new List<RegistryIssueListItem>();

        foreach (var item in selected)
        {
            var isReset = item.Issue.CorrectedValueData is not null;
            SetStatus(isReset ? L.F("Resetting {0}...", item.DisplayName) : L.F("Removing {0}...", item.DisplayName));

            var outcome = await _cleaner.RemoveAsync(item.Issue);
            var verb = isReset ? L.T("reset to a safe default") : L.T("removed");

            var line = outcome.Success
                ? $"{item.DisplayName}: {verb}" + (outcome.BackupPath is not null ? " " + L.F("(backed up to {0})", outcome.BackupPath) : "")
                : L.F("{0}: FAILED - {1}", item.DisplayName, outcome.Error);

            StepLogList.Items.Add(line);
            SetStatus(outcome.Success ? $"{item.DisplayName}: {verb}." : L.F("{0} could not be changed: {1}", item.DisplayName, outcome.Error));

            if (outcome.Success)
                removedItems.Add(item);
        }

        foreach (var removed in removedItems)
        {
            _items.Remove(removed);
            _allIssues.Remove(removed.Issue);
        }

        ScanButton.IsEnabled = true;

        SetStatus(L.F("Done. {0} of {1} changed.", removedItems.Count, L.N(selected.Count, "{0} item", "{0} items")));
    }

    // --- Ignoring items ------------------------------------------------------------------

    private void RefreshList()
    {
        _items.Clear();
        foreach (var issue in _allIssues.Where(i => ShowIgnoredCheckBox.IsChecked == true || !i.IsIgnored))
            _items.Add(new RegistryIssueListItem(issue));
    }

    private string BuildScanSummary()
    {
        var active = _allIssues.Count(i => !i.IsIgnored);
        var ignored = _allIssues.Count - active;

        var summary = active == 0
            ? L.T("No issues found. The registry looks clean for the categories this tool checks.")
            : L.F("Found {0}. Review them before removing - each is backed up automatically when removed.", L.N(active, "{0} issue", "{0} issues"));

        if (ignored > 0)
        {
            summary += " " + (ShowIgnoredCheckBox.IsChecked == true
                ? L.F("{0} you chose to ignore are also shown, marked as ignored.", L.N(ignored, "{0} item", "{0} items"))
                : L.F("{0} you chose to ignore are hidden; check Show ignored items to see them.", L.N(ignored, "{0} item", "{0} items")));
        }

        return summary;
    }

    private void ShowIgnoredCheckBox_Click(object sender, RoutedEventArgs e)
    {
        RefreshList();
        SetStatus(BuildScanSummary());
    }

    private void ContextIgnoreSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsListView.SelectedItems.Cast<RegistryIssueListItem>().Where(i => !i.Issue.IsIgnored).ToList();
        if (selected.Count == 0)
        {
            SetStatus(L.T("Nothing to ignore. Select one or more items that are not ignored yet."));
            return;
        }

        try
        {
            foreach (var item in selected)
            {
                IgnoredIssuesStore.Ignore(item.Issue);
                item.Issue.IsIgnored = true;
            }
        }
        catch (Exception ex)
        {
            SetStatus(L.F("Could not save the ignore list: {0}", ex.Message));
            return;
        }

        RefreshList();
        SetStatus(L.F("{0} will not be reported again, unless the entry changes. Nothing was removed from the registry.",
            L.N(selected.Count, "{0} item", "{0} items")));
    }

    private void ContextStopIgnoringSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsListView.SelectedItems.Cast<RegistryIssueListItem>().Where(i => i.Issue.IsIgnored).ToList();
        if (selected.Count == 0)
        {
            SetStatus(L.T("None of the selected items are ignored. Check Show ignored items to see ignored ones."));
            return;
        }

        try
        {
            foreach (var item in selected)
            {
                IgnoredIssuesStore.StopIgnoring(item.Issue);
                item.Issue.IsIgnored = false;
            }
        }
        catch (Exception ex)
        {
            SetStatus(L.F("Could not save the ignore list: {0}", ex.Message));
            return;
        }

        RefreshList();
        SetStatus(L.F("{0} will be reported again.", L.N(selected.Count, "{0} item", "{0} items")));
    }

    private void SetStatus(string message)
    {
        LiveAnnouncer.Announce(StatusText, message);
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using UltraUninstaller.Core.Localization;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Services;

namespace UltraUninstaller.App;

public partial class JunkCleanerWindow : Window
{
    private readonly JunkCleanerService _cleaner = new();
    private readonly ObservableCollection<JunkCategoryListItem> _items = new();

    public JunkCleanerWindow()
    {
        InitializeComponent();
        ResultsListView.ItemsSource = _items;
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CleanSelectedButton.IsEnabled = ResultsListView.SelectedItems.Count > 0;
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
            var categories = await scanTask;

            foreach (var category in categories)
                _items.Add(new JunkCategoryListItem(category));

            var totalBytes = categories.Sum(c => c.SizeBytes);
            var totalGb = totalBytes / 1024.0 / 1024.0 / 1024.0;
            var totalDisplay = totalGb >= 1
                ? string.Format(L.Culture, "{0:0.##} GB", totalGb)
                : string.Format(L.Culture, "{0:0.#} MB", totalBytes / 1024.0 / 1024.0);

            SetStatus(categories.Count == 0
                ? L.T("Nothing found. All the locations this tool checks are already clean.")
                : L.F("Found {0}, about {1} total. Review and select what to clean.", L.N(categories.Count, "{0} category", "{0} categories"), totalDisplay));
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

    private async void ContextCleanSelected_Click(object sender, RoutedEventArgs e) => await CleanSelectedAsync();

    private async void CleanSelectedButton_Click(object sender, RoutedEventArgs e) => await CleanSelectedAsync();

    private void ContextShowDetails_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsListView.SelectedItems.Cast<JunkCategoryListItem>().ToList();
        if (selected.Count != 1)
        {
            SetStatus(L.T("Select exactly one category to show its details."));
            return;
        }

        var category = selected[0].Result;
        var pathsList = category.TargetPaths.Count > 0
            ? string.Join(Environment.NewLine, category.TargetPaths)
            : L.T("(no individual folders - handled through the Recycle Bin)");

        var details = string.Join(Environment.NewLine, new[]
        {
            L.F("Category: {0}", category.DisplayName),
            L.F("Size: {0}", selected[0].SizeDisplay),
            L.F("Items: {0}", category.FileCount),
            category.Description,
            "",
            L.T("Locations:"),
            pathsList,
        });

        MessageBox.Show(details, L.F("Details - {0}", category.DisplayName), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task CleanSelectedAsync()
    {
        var selected = ResultsListView.SelectedItems.Cast<JunkCategoryListItem>().ToList();
        if (selected.Count == 0)
        {
            SetStatus(L.T("Nothing is selected. Select one or more categories first."));
            return;
        }

        var includesRecycleBin = selected.Any(i => i.Result.Kind == JunkCategoryKind.RecycleBin);
        var names = string.Join(", ", selected.Select(i => i.DisplayName));
        var confirm = MessageBox.Show(
            L.F("Clean {0}?", L.N(selected.Count, "{0} category", "{0} categories")) + "\n\n" + names + "\n\n" +
            L.T("These are files Windows or your browsers rebuild automatically - this is not the same as uninstalling anything.") +
            (includesRecycleBin ? "\n\n" + L.T("The Recycle Bin is included: emptying it is permanent and cannot be undone.") : ""),
            L.T("Confirm cleaning"),
            MessageBoxButton.YesNo,
            includesRecycleBin ? MessageBoxImage.Warning : MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus(L.T("Cancelled."));
            return;
        }

        CleanSelectedButton.IsEnabled = false;
        ScanButton.IsEnabled = false;
        StepLogList.Items.Clear();

        var progress = new Progress<string>(SetStatus);
        var results = await _cleaner.CleanAsync(selected.Select(i => i.Result), progress);

        var cleanedItems = new List<JunkCategoryListItem>();
        long totalFreed = 0;

        foreach (var outcome in results)
        {
            var line = outcome.Error is not null
                ? L.F("{0}: FAILED - {1}", outcome.DisplayName, outcome.Error)
                : L.F("{0}: removed {1}, freed {2:0.#} MB", outcome.DisplayName, L.N(outcome.FilesDeleted, "{0} item", "{0} items"), outcome.BytesFreed / 1024.0 / 1024.0) +
                  (outcome.FilesSkipped > 0 ? " " + L.F("({0} skipped - in use)", outcome.FilesSkipped) : "");

            StepLogList.Items.Add(line);

            if (outcome.Error is null)
            {
                totalFreed += outcome.BytesFreed;
                var match = selected.FirstOrDefault(i => i.Result.Kind == outcome.Kind);
                if (match is not null)
                    cleanedItems.Add(match);
            }
        }

        foreach (var cleaned in cleanedItems)
            _items.Remove(cleaned);

        ScanButton.IsEnabled = true;

        var freedGb = totalFreed / 1024.0 / 1024.0 / 1024.0;
        var freedDisplay = freedGb >= 1
            ? string.Format(L.Culture, "{0:0.##} GB", freedGb)
            : string.Format(L.Culture, "{0:0.#} MB", totalFreed / 1024.0 / 1024.0);
        SetStatus(L.F("Done. Freed about {0} across {1} of {2}.", freedDisplay, cleanedItems.Count, L.N(selected.Count, "{0} category", "{0} categories")));
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}

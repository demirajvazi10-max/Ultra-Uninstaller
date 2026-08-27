using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
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
            var categories = await scanTask;

            foreach (var category in categories)
                _items.Add(new JunkCategoryListItem(category));

            var totalBytes = categories.Sum(c => c.SizeBytes);
            var totalGb = totalBytes / 1024.0 / 1024.0 / 1024.0;
            var totalDisplay = totalGb >= 1 ? $"{totalGb:0.##} GB" : $"{totalBytes / 1024.0 / 1024.0:0.#} MB";

            SetStatus(categories.Count == 0
                ? "Nothing found. All the locations this tool checks are already clean."
                : $"Found {categories.Count} categor{(categories.Count == 1 ? "y" : "ies")}, about {totalDisplay} total. Review and select what to clean.");
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

    private async void ContextCleanSelected_Click(object sender, RoutedEventArgs e) => await CleanSelectedAsync();

    private async void CleanSelectedButton_Click(object sender, RoutedEventArgs e) => await CleanSelectedAsync();

    private void ContextShowDetails_Click(object sender, RoutedEventArgs e)
    {
        var selected = ResultsListView.SelectedItems.Cast<JunkCategoryListItem>().ToList();
        if (selected.Count != 1)
        {
            SetStatus("Select exactly one category to show its details.");
            return;
        }

        var category = selected[0].Result;
        var pathsList = category.TargetPaths.Count > 0
            ? string.Join(Environment.NewLine, category.TargetPaths)
            : "(no individual folders - handled through the Recycle Bin)";

        var details = string.Join(Environment.NewLine, new[]
        {
            $"Category: {category.DisplayName}",
            $"Size: {selected[0].SizeDisplay}",
            $"Items: {category.FileCount}",
            $"{category.Description}",
            "",
            "Locations:",
            pathsList,
        });

        MessageBox.Show(details, $"Details - {category.DisplayName}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task CleanSelectedAsync()
    {
        var selected = ResultsListView.SelectedItems.Cast<JunkCategoryListItem>().ToList();
        if (selected.Count == 0)
        {
            SetStatus("Nothing is selected. Select one or more categories first.");
            return;
        }

        var includesRecycleBin = selected.Any(i => i.Result.Kind == JunkCategoryKind.RecycleBin);
        var names = string.Join(", ", selected.Select(i => i.DisplayName));
        var confirm = MessageBox.Show(
            $"Clean {selected.Count} categor{(selected.Count == 1 ? "y" : "ies")}?\n\n{names}\n\n" +
            "These are files Windows or your browsers rebuild automatically - this is not the same as uninstalling anything." +
            (includesRecycleBin ? "\n\nThe Recycle Bin is included: emptying it is permanent and cannot be undone." : ""),
            "Confirm cleaning",
            MessageBoxButton.YesNo,
            includesRecycleBin ? MessageBoxImage.Warning : MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus("Cancelled.");
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
                ? $"{outcome.DisplayName}: FAILED - {outcome.Error}"
                : $"{outcome.DisplayName}: removed {outcome.FilesDeleted} item(s), freed {outcome.BytesFreed / 1024.0 / 1024.0:0.#} MB" +
                  (outcome.FilesSkipped > 0 ? $" ({outcome.FilesSkipped} skipped - in use)" : "");

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
        var freedDisplay = freedGb >= 1 ? $"{freedGb:0.##} GB" : $"{totalFreed / 1024.0 / 1024.0:0.#} MB";
        SetStatus($"Done. Freed about {freedDisplay} across {cleanedItems.Count} of {selected.Count} categor{(selected.Count == 1 ? "y" : "ies")}.");
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}

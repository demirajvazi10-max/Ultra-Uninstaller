using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UltraUninstaller.Core.Localization;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Services;

namespace UltraUninstaller.App;

public partial class DiskAnalyzerWindow : Window
{
    private readonly DiskSpaceAnalyzerService _analyzer = new();
    private readonly ForceDeleteService _forceDelete = new();
    private readonly ObservableCollection<FolderSizeItem> _items = new();
    private bool _scanInProgress;

    /// <param name="initialPath">
    /// When provided (e.g. launched via the Explorer right-click entry), the window
    /// scans this folder immediately. When null (opened from the Tools menu), the
    /// window waits for the user to press Scan — it never starts scanning on its own.
    /// </param>
    public DiskAnalyzerWindow(string? initialPath = null)
    {
        InitializeComponent();
        ResultsListView.ItemsSource = _items;
        ResultsListView.PreviewMouseRightButtonDown += ResultsListView_PreviewMouseRightButtonDown;

        PathBox.Text = initialPath ?? Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

        if (initialPath is not null)
        {
            Loaded += async (_, _) => await ScanCurrentPathAsync();
        }
        else
        {
            SetStatus(L.F("Ready. Path is set to {0}. Press Scan or Enter to begin.", PathBox.Text));
        }
    }

    private void ResultsListView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = (DependencyObject)e.OriginalSource;
        while (dep is not null && dep is not ListViewItem)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is ListViewItem { IsSelected: false } item)
        {
            ResultsListView.SelectedItems.Clear();
            item.IsSelected = true;
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanCurrentPathAsync();

    private async void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await ScanCurrentPathAsync();
    }

    private async void UpButton_Click(object sender, RoutedEventArgs e)
    {
        var current = PathBox.Text.Trim();
        var parent = Directory.GetParent(current);
        if (parent is null)
        {
            SetStatus(L.T("Already at the top of the drive."));
            return;
        }

        PathBox.Text = parent.FullName;
        await ScanCurrentPathAsync();
    }

    private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = ResultsListView.SelectedItems.Count > 0;
        DeleteSelectedButton.IsEnabled = hasSelection;
        ForceDeleteSelectedButton.IsEnabled = hasSelection;
    }

    private async void ResultsListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            await DrillIntoCurrentRowAsync();
    }

    private async void ResultsListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await DrillIntoCurrentRowAsync();
    }

    private async void ContextOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        await DrillIntoCurrentRowAsync();
    }

    private async void ContextDeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedAsync();
    }

    private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedAsync();
    }

    private async void ContextForceDeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        await ForceDeleteSelectedAsync();
    }

    private async void ForceDeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        await ForceDeleteSelectedAsync();
    }

    private async Task DrillIntoCurrentRowAsync()
    {
        // Only drill down for a single, unambiguous selection - with several rows
        // selected, Enter/double-click/"Open" doesn't have one clear target.
        if (ResultsListView.SelectedItems.Count != 1)
            return;

        if (ResultsListView.SelectedItems[0] is not FolderSizeItem item || !item.IsDrillable)
            return;

        PathBox.Text = item.FullPath;
        await ScanCurrentPathAsync();
    }

    /// <summary>
    /// Runs a scan while a modal "Scanning..." dialog (progress bar + Cancel) is shown.
    /// The dialog is only ever closed from here, once the scan Task has actually
    /// finished (success, cancellation, or failure) — never by the dialog itself —
    /// so there's no window of time where the dialog and the real state disagree.
    /// </summary>
    private async Task ScanCurrentPathAsync()
    {
        if (_scanInProgress)
            return; // A previous scan (e.g. from a fast double Enter) is still wrapping up; ignore re-entry rather than overlap two scans.

        var path = PathBox.Text.Trim();

        if (!Directory.Exists(path))
        {
            SetStatus(L.F("Folder not found: {0}", path));
            return;
        }

        _scanInProgress = true;
        ScanButton.IsEnabled = false;
        UpButton.IsEnabled = false;
        _items.Clear();
        SetStatus(L.F("Scanning {0}...", path));

        var progressWindow = new ScanProgressWindow { Owner = this };

        var progress = new Progress<ScanProgress>(p =>
        {
            // Progress<T> already marshals to this (UI) thread's context, so it's
            // safe to touch the window directly here.
            progressWindow.ReportProgress(p);
        });

        var scanTask = _analyzer.ScanAsync(path, progress, progressWindow.Cts.Token);

        // Whether we actually opened the modal dialog. A scan over an empty folder
        // can complete synchronously (no subfolders means the method never hits a
        // real await), in which case there's nothing to show and nothing to close.
        // Both this flag and the continuation below run on the same UI-thread
        // dispatcher, so there is no race between setting it and the continuation
        // reading it — the continuation cannot run until we yield control (either
        // by returning or by starting the nested pump in ShowDialog()).
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

            foreach (var entry in results)
                _items.Add(new FolderSizeItem(entry));

            SetStatus(results.Count == 0
                ? L.F("No subfolders found in {0}.", path)
                : L.F("Scan complete. {0} found in {1}, largest first.", L.N(results.Count, "{0} item", "{0} items"), path));
        }
        catch (OperationCanceledException)
        {
            SetStatus(L.T("Scan cancelled."));
        }
        catch (UnauthorizedAccessException)
        {
            SetStatus(L.F("Access denied while scanning {0}. Try running UltraUninstaller as administrator.", path));
        }
        catch (Exception ex)
        {
            SetStatus(L.F("Scan failed: {0}", ex.Message));
        }
        finally
        {
            ScanButton.IsEnabled = true;
            UpButton.IsEnabled = true;
            _scanInProgress = false;
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var selected = ResultsListView.SelectedItems.Cast<FolderSizeItem>().ToList();
        if (selected.Count == 0)
        {
            SetStatus(L.T("Nothing is selected. Select one or more rows first."));
            return;
        }

        var totalGb = selected.Sum(i => i.SizeGb);
        var names = string.Join(", ", selected.Select(i => i.Name));
        var confirm = MessageBox.Show(
            L.F("Send {0} to the Recycle Bin, totaling about {1:0.##} GB?", L.N(selected.Count, "{0} item", "{0} items"), totalGb) + "\n\n" + names,
            L.T("Confirm delete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus(L.T("Delete cancelled."));
            return;
        }

        DeleteSelectedButton.IsEnabled = false;
        SetStatus(L.F("Deleting {0} to the Recycle Bin.", L.N(selected.Count, "{0} item", "{0} items")));

        var outcomes = await _analyzer.DeleteToRecycleBinAsync(selected.Select(i => i.FullPath));

        var succeeded = outcomes.Count(o => o.Success);
        var failed = outcomes.Where(o => !o.Success).ToList();

        SetStatus(failed.Count == 0
            ? L.F("{0} sent to the Recycle Bin.", L.N(succeeded, "{0} item", "{0} items"))
            : L.F("{0} deleted, {1} failed: {2}", L.N(succeeded, "{0} item", "{0} items"), failed.Count, string.Join("; ", failed.Select(f => $"{Path.GetFileName(f.Path)} - {f.Error}"))));

        await ScanCurrentPathAsync();
    }

    private async Task ForceDeleteSelectedAsync()
    {
        var selected = ResultsListView.SelectedItems.Cast<FolderSizeItem>().ToList();
        if (selected.Count == 0)
        {
            SetStatus(L.T("Nothing is selected. Select one or more rows first."));
            return;
        }

        var names = string.Join(", ", selected.Select(i => i.Name));
        var confirm = MessageBox.Show(
            L.F("Force delete {0}?", L.N(selected.Count, "{0} item", "{0} items")) + "\n\n" + names + "\n\n" +
            L.T("This tries progressively stronger methods for folders that a normal " +
                "delete can't remove - including detaching orphaned Windows image mounts " +
                "(WinPE/WIM/VHD), resetting ownership and permissions, a permanent delete " +
                "that bypasses the Recycle Bin, and as a last resort, scheduling deletion " +
                "for your next restart. This can take a while and some steps need " +
                "administrator rights."),
            L.T("Confirm force delete"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
        {
            SetStatus(L.T("Force delete cancelled."));
            return;
        }

        DeleteSelectedButton.IsEnabled = false;
        ForceDeleteSelectedButton.IsEnabled = false;
        StepLogList.Items.Clear();

        foreach (var item in selected)
        {
            SetStatus(L.F("Force deleting {0}...", item.Name));

            var result = await _forceDelete.ForceDeleteAsync(item.FullPath, step =>
            {
                Dispatcher.Invoke(() =>
                {
                    var line = string.IsNullOrEmpty(step.Detail) ? step.Description : $"{step.Description} ({step.Detail})";
                    StepLogList.Items.Add($"{item.Name}: {line}");
                    SetStatus(step.Description);
                });
            });

            SetStatus(result.Success
                ? L.F("{0} was removed ({1}).", item.Name, DescribeForceLayer(result.LayerUsed))
                : L.F("{0} could not be removed. {1}", item.Name, result.FailureReason));
        }

        await ScanCurrentPathAsync();
    }

    private static string DescribeForceLayer(ForceDeleteLayer layer) => layer switch
    {
        ForceDeleteLayer.RecycleBin => L.T("normal delete"),
        ForceDeleteLayer.ImageMountCleanup => L.T("after clearing an orphaned image mount"),
        ForceDeleteLayer.OwnershipReset => L.T("after resetting ownership and permissions"),
        ForceDeleteLayer.PermanentDelete => L.T("permanent delete"),
        ForceDeleteLayer.ScheduledOnReboot => L.T("scheduled for deletion on your next restart"),
        _ => L.T("an unknown method"),
    };

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}

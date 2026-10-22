using System.Windows;
using System.Windows.Input;
using UltraUninstaller.Core.Localization;
using UltraUninstaller.Core.Models;

namespace UltraUninstaller.App;

/// <summary>
/// Modal "Scanning..." dialog: a determinate progress bar, a live-announced status
/// line, and a Cancel button/Escape key. Deliberately does NOT close itself when
/// Cancel is pressed — it only requests cancellation and waits. The caller (whoever
/// started the scan) is responsible for closing this window once the underlying
/// scan Task has actually finished, whether that's because it completed, was
/// cancelled, or failed — so the dialog never disappears out of sync with reality.
/// </summary>
public partial class ScanProgressWindow : Window
{
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>True once the user has asked to cancel, so the caller can avoid re-cancelling or double-reporting.</summary>
    public bool CancelWasRequested { get; private set; }

    public ScanProgressWindow()
    {
        InitializeComponent();
    }

    /// <summary>Safe to call from any thread that has access to Dispatcher.Invoke already applied by the caller.</summary>
    public void ReportProgress(ScanProgress progress)
    {
        ScanProgressBar.IsIndeterminate = false;

        var percent = progress.FoldersTotal == 0
            ? 100
            : (int)Math.Round(100.0 * progress.FoldersCompleted / progress.FoldersTotal);

        ScanProgressBar.Value = percent;
        AnnounceThrottled(L.F("Measuring {0} ({1} of {2})", progress.CurrentFolderName, progress.FoldersCompleted, progress.FoldersTotal));
    }

    /// <summary>
    /// For scans that don't have a stable "X of Y" count up front (Startup Manager,
    /// Registry Cleaner) - switches the bar to an indeterminate "still working" state
    /// and just narrates each step, rather than showing a determinate percentage that
    /// would have to be a lie.
    /// </summary>
    public void ReportProgressText(string message)
    {
        ScanProgressBar.IsIndeterminate = true;
        AnnounceThrottled(message);
    }

    private DateTime _lastAnnouncement = DateTime.MinValue;

    /// <summary>
    /// Progress can arrive many times a second (e.g. "Checking scheduled task 37 of 210").
    /// The text always updates, but the screen reader is only told about it every couple
    /// of seconds, so it reads steady progress instead of cutting itself off constantly.
    /// </summary>
    private void AnnounceThrottled(string message)
    {
        if (DateTime.UtcNow - _lastAnnouncement < TimeSpan.FromSeconds(2))
        {
            StatusText.Text = message;
            return;
        }

        _lastAnnouncement = DateTime.UtcNow;
        LiveAnnouncer.Announce(StatusText, message);
    }

    public void ShowCancelling()
    {
        LiveAnnouncer.Announce(StatusText, L.T("Cancelling scan..."));
        CancelButton.IsEnabled = false;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        RequestCancel();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            RequestCancel();
    }

    private void RequestCancel()
    {
        if (CancelWasRequested)
            return;

        CancelWasRequested = true;
        ShowCancelling();
        Cts.Cancel();
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using UltraUninstaller.Core.Models;
using UltraUninstaller.Core.Services;

namespace UltraUninstaller.App;

public partial class HealthDashboardWindow : Window
{
    private readonly SystemHealthService _healthService = new();
    private readonly ObservableCollection<HealthScoreFactorListItem> _factors = new();

    public HealthDashboardWindow()
    {
        InitializeComponent();
        FactorsListView.ItemsSource = _factors;
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e) => await RunAsync();

    private void OpenStartupManagerButton_Click(object sender, RoutedEventArgs e)
    {
        new StartupManagerWindow { Owner = this }.Show();
    }

    private void OpenRegistryCleanerButton_Click(object sender, RoutedEventArgs e)
    {
        new RegistryCleanerWindow { Owner = this }.Show();
    }

    private async Task RunAsync()
    {
        RunButton.IsEnabled = false;
        _factors.Clear();
        ScoreText.Text = "--";
        GradeText.Text = "Checking...";

        var progressWindow = new ScanProgressWindow { Owner = this };
        progressWindow.ReportProgressText("Starting health check...");

        var progress = new Progress<string>(message =>
        {
            progressWindow.ReportProgressText(message);
            SetStatus(message);
        });

        var runTask = _healthService.RunAsync(progress, progressWindow.Cts.Token);

        var dialogWasShown = false;

        _ = runTask.ContinueWith(_ =>
        {
            if (dialogWasShown)
            {
                try { progressWindow.Close(); }
                catch { /* Already closing/closed - nothing more to do. */ }
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());

        if (!runTask.IsCompleted)
        {
            dialogWasShown = true;
            progressWindow.ShowDialog();
        }

        try
        {
            var report = await runTask;
            DisplayReport(report);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Health check cancelled.");
            ScoreText.Text = "--";
            GradeText.Text = "Cancelled - press Run Health Check to try again.";
        }
        catch (Exception ex)
        {
            SetStatus($"Health check failed: {ex.Message}");
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    private void DisplayReport(SystemHealthReport report)
    {
        ScoreText.Text = report.Score.ToString();
        GradeText.Text = report.GradeDisplay;

        ScoreText.Foreground = report.Grade switch
        {
            HealthGrade.A or HealthGrade.B => (Brush)FindResource("SuccessBrush"),
            HealthGrade.C => (Brush)FindResource("WarningBrush"),
            _ => (Brush)FindResource("DangerBrush"),
        };

        _factors.Clear();
        foreach (var factor in report.Factors)
            _factors.Add(new HealthScoreFactorListItem(factor));

        SetStatus(report.Factors.Count == 0
            ? $"Health check complete. Score {report.Score} out of 100, grade {report.Grade}. No issues found in the areas this checks."
            : $"Health check complete. Score {report.Score} out of 100, grade {report.Grade}. {report.Factors.Count} factor(s) affected the score - see the list below.");
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }
}

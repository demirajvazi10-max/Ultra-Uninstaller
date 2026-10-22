using System.Windows;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Pick the language before any window is created, so every label, menu and
        // screen reader name comes up in that language from the very first frame.
        L.SetLanguage(LanguageSettings.Load());

        // Last-resort safety net: a raw .NET crash dialog is unreadable/unusable
        // with a screen reader (it's not a normal accessible Window). If something
        // unexpected ever does slip past a local try/catch, show one clear message
        // instead of letting the process die on the user mid-task.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                L.F("UltraUninstaller ran into a problem and had to stop the current action:\n\n{0}\n\nYou can keep using the app.", args.Exception.Message),
                L.T("UltraUninstaller - unexpected error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        var analyzePath = TryGetAnalyzePath(e.Args);

        if (analyzePath is not null)
        {
            // Launched from the Explorer right-click entry ("Analyze size with
            // UltraUninstaller"): go straight to the Disk Space Analyzer for that
            // folder instead of the full program list, since that's the one thing
            // the user asked for.
            var analyzerWindow = new DiskAnalyzerWindow(analyzePath);
            MainWindow = analyzerWindow;
            analyzerWindow.Show();
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static string? TryGetAnalyzePath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--analyze", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
        }

        return null;
    }
}

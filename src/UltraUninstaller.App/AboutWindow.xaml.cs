using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.App;

/// <summary>
/// About UltraUninstaller: author credit, license, and the ways to support development
/// (GitHub Sponsors, Ko-fi, PayPal), same layout as the About window in Ultra Video Editor.
/// </summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version is null ? string.Empty : $"{version.Major}.{version.Minor}.{version.Build}";

        InfoText.Text = string.Join(Environment.NewLine, new[]
        {
            $"UltraUninstaller {versionText}".Trim(),
            "",
            L.T("An accessible Windows uninstaller and cleaner, made to work equally for blind and sighted people."),
            L.T("Created by Demir Ajvazi"),
            L.T("Part of the Ultra Creative Suite, open source (GPL-3.0)."),
            "",
            "© 2026 Demir Ajvazi",
            "",
            L.T("UltraUninstaller is free. If it helps you, you can support its further development:"),
        });

        Loaded += (_, _) => InfoText.Focus();
    }

    private void LinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url })
            return;

        try
        {
            // UltraUninstaller usually runs elevated, and a direct ShellExecute of a URL from an
            // elevated process can silently fail to reach the browser. explorer.exe runs at the
            // normal user level and hands the link over reliably (same fix as "Search online").
            Process.Start(new ProcessStartInfo("explorer.exe", url) { UseShellExecute = true });
            LiveAnnouncer.Announce(StatusText, L.T("Opening the page in your browser."));
        }
        catch (Exception)
        {
            LiveAnnouncer.Announce(StatusText, L.F("The page could not be opened. The address is: {0}", url));
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Close();
    }
}

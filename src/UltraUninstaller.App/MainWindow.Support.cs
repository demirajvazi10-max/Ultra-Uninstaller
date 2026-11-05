using System.Windows;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.App;

public partial class MainWindow
{
    /// <summary>File → How to support this project: step-by-step guide (payment card, no PayPal needed).</summary>
    private void SupportMenuItem_Click(object sender, RoutedEventArgs e)
        => UltraSupport.SupportInfo.Show(this, "UltraUninstaller", L.Current == AppLanguage.Serbian);
}

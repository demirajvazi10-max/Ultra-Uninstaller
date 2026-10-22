using System.Globalization;

namespace UltraUninstaller.Core.Localization;

/// <summary>
/// Remembers the language the user picked from the Language menu.
/// Stored as a one-word text file ("en" or "sr") under %AppData%\UltraUninstaller,
/// so it survives updates and works the same for installed and portable copies.
/// </summary>
public static class LanguageSettings
{
    private static string SettingsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UltraUninstaller");

    private static string SettingsFile => Path.Combine(SettingsFolder, "language.txt");

    /// <summary>
    /// The saved choice if there is one, otherwise a guess from the Windows display
    /// language: Serbian (and the closely related Croatian, Bosnian and Montenegrin)
    /// get Serbian, everyone else gets English.
    /// </summary>
    public static AppLanguage Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var saved = File.ReadAllText(SettingsFile).Trim().ToLowerInvariant();
                if (saved == "sr")
                    return AppLanguage.Serbian;
                if (saved == "en")
                    return AppLanguage.English;
            }
        }
        catch
        {
            // Unreadable settings file - fall through to the automatic choice.
        }

        return Detect(CultureInfo.CurrentUICulture);
    }

    public static AppLanguage Detect(CultureInfo culture)
    {
        var code = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        return code is "sr" or "hr" or "bs" or "cnr"
            ? AppLanguage.Serbian
            : AppLanguage.English;
    }

    public static void Save(AppLanguage language)
    {
        Directory.CreateDirectory(SettingsFolder);
        File.WriteAllText(SettingsFile, language == AppLanguage.Serbian ? "sr" : "en");
    }
}

using System.Globalization;

namespace UltraUninstaller.Core.Localization;

public enum AppLanguage
{
    English,
    Serbian,
}

/// <summary>
/// Tiny translation layer for the whole app (Core and UI).
///
/// The English text itself is the lookup key: code and XAML keep reading naturally,
/// and any string that has no translation yet simply falls back to English instead
/// of showing a blank or a raw resource key to the user (or to their screen reader).
///
/// The language is chosen once at startup (saved choice, otherwise the Windows display
/// language) and changing it asks for a restart, so nothing here needs to notify
/// already-open windows.
/// </summary>
public static class L
{
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo SerbianCulture = CultureInfo.GetCultureInfo("sr-Latn-RS");

    public static AppLanguage Current { get; private set; } = AppLanguage.English;

    /// <summary>Culture used for numbers inside translated text (Serbian uses a decimal comma).</summary>
    public static CultureInfo Culture => Current == AppLanguage.Serbian ? SerbianCulture : EnglishCulture;

    public static void SetLanguage(AppLanguage language) => Current = language;

    /// <summary>Translate a fixed piece of text.</summary>
    public static string T(string english)
    {
        if (Current == AppLanguage.Serbian && SerbianStrings.Map.TryGetValue(english, out var translated))
            return translated;

        return english;
    }

    /// <summary>Translate a text with {0}, {1}... placeholders, then fill them in.</summary>
    public static string F(string englishFormat, params object?[] args)
    {
        var format = T(englishFormat);
        try
        {
            return string.Format(Culture, format, args);
        }
        catch (FormatException)
        {
            // A broken translation must never crash the app - fall back to English.
            return string.Format(EnglishCulture, englishFormat, args);
        }
    }

    /// <summary>
    /// Count-dependent text, e.g. N(3, "{0} item", "{0} items").
    /// English picks one/other. Serbian has three forms (1 stavka, 2-4 stavke, 5+ stavki),
    /// stored in the translation of <paramref name="englishOther"/> as "one|few|many".
    /// </summary>
    public static string N(long count, string englishOne, string englishOther)
    {
        if (Current == AppLanguage.Serbian && SerbianStrings.Map.TryGetValue(englishOther, out var forms))
        {
            var parts = forms.Split('|');
            if (parts.Length == 3)
                return SafeFormat(parts[SerbianPluralIndex(count)], englishOther, count);
        }

        return string.Format(EnglishCulture, count == 1 ? englishOne : englishOther, count);
    }

    private static string SafeFormat(string format, string englishFallback, long count)
    {
        try
        {
            return string.Format(Culture, format, count);
        }
        catch (FormatException)
        {
            return string.Format(EnglishCulture, englishFallback, count);
        }
    }

    /// <summary>0 = one (1, 21, 31...), 1 = few (2-4, 22-24...), 2 = many (everything else, incl. 11-14).</summary>
    private static int SerbianPluralIndex(long count)
    {
        var n = Math.Abs(count);
        var lastTwo = n % 100;
        var last = n % 10;

        if (last == 1 && lastTwo != 11)
            return 0;
        if (last is >= 2 and <= 4 && (lastTwo < 12 || lastTwo > 14))
            return 1;
        return 2;
    }
}

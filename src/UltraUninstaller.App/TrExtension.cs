using System.Windows.Markup;
using UltraUninstaller.Core.Localization;

namespace UltraUninstaller.App;

/// <summary>
/// XAML helper: Header="{l:Tr '_File'}" shows the translation of "_File" for the
/// current language (or the English text itself if there's no translation).
/// Used for every visible label and every AutomationProperties.Name, so sighted
/// users and screen reader users always get the same language.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string text)
    {
        Text = text;
    }

    [ConstructorArgument("text")]
    public string Text { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}

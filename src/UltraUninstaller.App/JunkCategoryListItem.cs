using UltraUninstaller.Core.Localization;
using UltraUninstaller.Core.Models;

namespace UltraUninstaller.App;

public sealed class JunkCategoryListItem
{
    public JunkCategoryListItem(JunkCategoryResult result)
    {
        Result = result;
    }

    public JunkCategoryResult Result { get; }

    public string DisplayName => Result.DisplayName;
    public string Description => Result.Description;
    public int FileCount => Result.FileCount;

    public string SizeDisplay
    {
        get
        {
            var value = Result.SizeGb >= 1
                ? string.Format(L.Culture, "{0:0.##} GB", Result.SizeGb)
                : string.Format(L.Culture, "{0:0.#} MB", Result.SizeMb);
            return Result.SizeIsApproximate ? L.F("{0} (at least - some files couldn't be measured)", value) : value;
        }
    }

    /// <summary>
    /// What a screen reader announces for this row - bound via ItemContainerStyle,
    /// since WPF's ListView+GridView doesn't give rows a meaningful accessible Name
    /// on its own and NVDA (unlike JAWS) needs one explicitly set to announce anything.
    /// </summary>
    public string AutomationName => $"{DisplayName}, {SizeDisplay}, {L.N(FileCount, "{0} item", "{0} items")}. {Description}";
}

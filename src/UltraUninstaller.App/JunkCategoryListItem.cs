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
            var value = Result.SizeGb >= 1 ? $"{Result.SizeGb:0.##} GB" : $"{Result.SizeMb:0.#} MB";
            return Result.SizeIsApproximate ? $"{value} (at least - some files couldn't be measured)" : value;
        }
    }

    /// <summary>
    /// What a screen reader announces for this row - bound via ItemContainerStyle,
    /// since WPF's ListView+GridView doesn't give rows a meaningful accessible Name
    /// on its own and NVDA (unlike JAWS) needs one explicitly set to announce anything.
    /// </summary>
    public string AutomationName => $"{DisplayName}, {SizeDisplay}, {FileCount} item{(FileCount == 1 ? "" : "s")}. {Description}";
}

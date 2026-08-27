using UltraUninstaller.Core.Models;

namespace UltraUninstaller.App;

/// <summary>
/// Wraps a Core FolderSizeEntry with a UI-friendly formatted size string.
/// Kept separate from the Core model so Core stays free of any presentation concerns.
/// </summary>
public sealed class FolderSizeItem
{
    public FolderSizeItem(FolderSizeEntry entry)
    {
        Entry = entry;
    }

    public FolderSizeEntry Entry { get; }

    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public double SizeGb => Entry.SizeGb;

    public string SizeDisplay
    {
        get
        {
            var value = Entry.SizeGb >= 1 ? $"{Entry.SizeGb:0.##} GB" : $"{Entry.SizeMb:0.#} MB";
            return Entry.SizeIsApproximate ? $"{value} (at least - some subfolders were inaccessible)" : value;
        }
    }

    /// <summary>Loose files at this level (not a real subfolder) can't be drilled into.</summary>
    public bool IsDrillable => Entry.Name != "(files directly in this folder)";

    /// <summary>
    /// What a screen reader announces for this row - bound via ItemContainerStyle,
    /// since WPF's ListView+GridView doesn't give rows a meaningful accessible Name
    /// on its own and NVDA (unlike JAWS) needs one explicitly set to announce anything.
    /// </summary>
    public string AutomationName => $"{Name}, {SizeDisplay}";
}

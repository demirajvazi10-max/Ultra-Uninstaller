using UltraUninstaller.Core.Localization;
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

    /// <summary>
    /// Core names the "loose files" pseudo-row with a fixed English marker (it's also how
    /// IsDrillable recognizes it), so only the displayed text is translated here.
    /// </summary>
    public string Name => IsDrillable ? Entry.Name : L.T(LooseFilesMarker);

    private const string LooseFilesMarker = "(files directly in this folder)";
    public string FullPath => Entry.FullPath;
    public double SizeGb => Entry.SizeGb;

    public string SizeDisplay
    {
        get
        {
            var value = Entry.SizeGb >= 1
                ? string.Format(L.Culture, "{0:0.##} GB", Entry.SizeGb)
                : string.Format(L.Culture, "{0:0.#} MB", Entry.SizeMb);
            return Entry.SizeIsApproximate ? L.F("{0} (at least - some subfolders were inaccessible)", value) : value;
        }
    }

    /// <summary>Loose files at this level (not a real subfolder) can't be drilled into.</summary>
    public bool IsDrillable => Entry.Name != LooseFilesMarker;

    /// <summary>
    /// What a screen reader announces for this row - bound via ItemContainerStyle,
    /// since WPF's ListView+GridView doesn't give rows a meaningful accessible Name
    /// on its own and NVDA (unlike JAWS) needs one explicitly set to announce anything.
    /// </summary>
    public string AutomationName => $"{Name}, {SizeDisplay}";
}

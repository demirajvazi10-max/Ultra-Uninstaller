using UltraUninstaller.Core.Models;

namespace UltraUninstaller.App;

public sealed class RegistryNodeListItem
{
    public RegistryNodeListItem(RegistryNode node)
    {
        Node = node;
    }

    public RegistryNode Node { get; }

    public string Name => Node.Name;
    public bool IsSubKey => Node.Kind == RegistryNodeKind.SubKey;

    public string KindDisplay => Node.Kind == RegistryNodeKind.SubKey ? "Key" : $"Value ({Node.ValueKindDisplay})";

    public string DetailsDisplay => Node.Kind == RegistryNodeKind.SubKey
        ? $"{Node.ChildSubKeyCount ?? 0} subkey(s), {Node.ChildValueCount ?? 0} value(s)"
        : Node.DataPreview ?? string.Empty;
}

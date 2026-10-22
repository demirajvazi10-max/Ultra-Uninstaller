using UltraUninstaller.Core.Localization;
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

    public string KindDisplay => Node.Kind == RegistryNodeKind.SubKey ? L.T("Key") : L.F("Value ({0})", Node.ValueKindDisplay);

    public string DetailsDisplay => Node.Kind == RegistryNodeKind.SubKey
        ? $"{L.N(Node.ChildSubKeyCount ?? 0, "{0} subkey", "{0} subkeys")}, {L.N(Node.ChildValueCount ?? 0, "{0} value", "{0} values")}"
        : Node.DataPreview ?? string.Empty;
}

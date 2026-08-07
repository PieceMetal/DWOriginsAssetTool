using System.Collections.ObjectModel;

namespace DWOriginsAssetTool.Core;

public sealed class BoneTreeNode
{
    public BoneTreeNode(int localId, int globalId, int parentLocalId)
    {
        LocalId = localId;
        GlobalId = globalId;
        ParentLocalId = parentLocalId;
    }

    public int LocalId { get; }
    public int GlobalId { get; }
    public int ParentLocalId { get; }
    public string Name => $"bone_{GlobalId}";
    public string Badge => $"L{LocalId}";
    public ObservableCollection<BoneTreeNode> Children { get; } = [];
}

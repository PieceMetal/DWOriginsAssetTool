using System.Collections.ObjectModel;

namespace DWOriginsAssetTool.Core;

public sealed class ResourceTreeNode
{
    private readonly IReadOnlyList<AssetEntry>? _assets;
    private readonly int _depth;
    private bool _loaded;

    private ResourceTreeNode(string name, string relativePath, IReadOnlyList<AssetEntry>? assets,
        int depth, AssetEntry? asset)
    {
        Name = name;
        RelativePath = relativePath;
        _assets = assets;
        _depth = depth;
        Asset = asset;
        if (asset is null && assets?.Count > 0) Children.Add(LoadingNode);
    }

    public string Name { get; }
    public string RelativePath { get; }
    public AssetEntry? Asset { get; }
    public bool IsFolder => Asset is null;
    public string Icon => IsFolder ? "▸" : Asset!.Kind switch
    {
        AssetKind.Model => "◇", AssetKind.Animation => "▶", AssetKind.Texture => "▧",
        AssetKind.Skeleton => "⌘", AssetKind.Package => "▣", _ => "·"
    };
    public string Detail => IsFolder ? $"{_assets?.Count ?? 0:N0}" : Asset!.SizeText;
    public string CameraBadge => Asset is null ? "" : Asset.IsActionCamera ? "动画镜头文件"
        : Asset.Kind == AssetKind.Animation && !string.IsNullOrWhiteSpace(Asset.ActionCameraResourceId)
            ? "动画镜头" : "";
    public bool HasCameraBadge => CameraBadge.Length > 0;
    public ObservableCollection<ResourceTreeNode> Children { get; } = [];

    private static ResourceTreeNode LoadingNode => new("正在加载…", "", null, 0, null) { _loaded = true };

    public static IReadOnlyList<ResourceTreeNode> CreateRoots(IEnumerable<AssetEntry> assets)
    {
        return assets.GroupBy(x => LogicalParts(x)[0], StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => RootOrder(x.Key)).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ResourceTreeNode(group.Key, group.Key, group.ToList(), 1, null))
            .ToList();
    }

    public void LoadChildren()
    {
        if (_loaded || Asset is not null || _assets is null) return;
        _loaded = true;
        Children.Clear();
        var direct = new List<AssetEntry>();
        var folders = new Dictionary<string, List<AssetEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in _assets)
        {
            var parts = LogicalParts(asset);
            if (parts.Length <= _depth + 1) { direct.Add(asset); continue; }
            var segment = parts[_depth];
            if (!folders.TryGetValue(segment, out var list)) folders[segment] = list = [];
            list.Add(asset);
        }
        foreach (var folder in folders.OrderBy(x => FolderOrder(x.Key)).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            Children.Add(new(folder.Key, Path.Combine(RelativePath, folder.Key), folder.Value, _depth + 1, null));
        foreach (var asset in direct.OrderBy(x => x.TreeDisplayName, StringComparer.OrdinalIgnoreCase))
            Children.Add(ForAsset(asset));
    }

    private static ResourceTreeNode ForAsset(AssetEntry asset) =>
        new(asset.TreeDisplayName, asset.RelativePath, null, int.MaxValue, asset) { _loaded = true };

    private static string[] LogicalParts(AssetEntry asset)
    {
        var physical = Parts(asset.RelativePath);
        var file = asset.TreeDisplayName;

        if (asset.Kind == AssetKind.Animation)
        {
            if (!string.IsNullOrWhiteSpace(asset.GameActionCategory))
            {
                var owners = asset.GameActionOwners is { Count: 1 }
                    ? CharacterWeaponCatalog.FormatFolder(asset.GameActionOwners[0]) : "多个动作表共享";
                return ["动画", asset.GameActionCategory!, owners, file];
            }
            return ["动画", "未恢复动作表归属", file];
        }

        var owner = asset.OwnerName ?? OwnerFromPath(physical) ?? OwnerFromOfficialName(asset.OfficialName);
        if (!string.IsNullOrWhiteSpace(owner) && IsFormalOwner(owner))
        {
            var category = OwnerCategory(owner);
            var ownerFolder = OfficialNameAnnotations.Format(owner);
            return category == "角色"
                ? [category, CharacterGroup(owner), ownerFolder, KindFolder(asset.Kind), file]
                : [category, ownerFolder, KindFolder(asset.Kind), file];
        }

        if (physical.FirstOrDefault()?.Equals("_unresolved", StringComparison.OrdinalIgnoreCase) == true)
            return ["未识别资源", KindFolder(asset.Kind), file];

        return ["游戏目录", .. physical.Take(Math.Max(0, physical.Length - 1)), file];
    }

    private static string? OwnerFromPath(IReadOnlyList<string> parts)
    {
        if (parts.Count >= 2 && parts[0] is "character" or "item" or "stage" or "event" or "named")
            return parts[1];
        return parts.FirstOrDefault(IsFormalOwner);
    }

    private static string? OwnerFromOfficialName(string? official)
    {
        if (string.IsNullOrWhiteSpace(official)) return null;
        var length = 0;
        while (length < official.Length && (char.IsLetterOrDigit(official[length]) || official[length] is '_' or '-')) length++;
        return length > 0 ? official[..length] : null;
    }

    private static string OwnerCategory(string owner) =>
        owner.StartsWith("HW", StringComparison.OrdinalIgnoreCase) || owner.StartsWith("W", StringComparison.OrdinalIgnoreCase)
            ? "武器" : owner.StartsWith("H", StringComparison.OrdinalIgnoreCase) ? "角色" :
            owner.StartsWith("S", StringComparison.OrdinalIgnoreCase) ? "场景/关卡" :
            owner.StartsWith("E", StringComparison.OrdinalIgnoreCase) ? "事件" : "已命名资源";

    private static string CharacterGroup(string owner) => owner.StartsWith("H0100_", StringComparison.OrdinalIgnoreCase)
        ? "紫鸾" : owner.StartsWith("H02", StringComparison.OrdinalIgnoreCase) ? "小兵" : "武将/角色";

    private static bool IsFormalOwner(string value) => value.StartsWith("HW", StringComparison.OrdinalIgnoreCase) ||
        value.Length > 1 && (value[0] is 'H' or 'W' or 'S' or 'E') && char.IsDigit(value[1]);

    private static string KindFolder(AssetKind kind) => kind switch
    {
        AssetKind.Model => "模型", AssetKind.Animation => "动画", AssetKind.Skeleton => "骨骼",
        AssetKind.Texture => "贴图", AssetKind.Material => "材质", AssetKind.Collision => "碰撞",
        AssetKind.Movie => "影片", AssetKind.Package => "资源包", _ => "其他"
    };

    private static int RootOrder(string name) => name switch
    {
        "角色" => 0, "动画" => 1, "武器" => 2,
        "场景/关卡" => 4, "事件" => 5, "公共资源" => 6,
        "游戏目录" => 7, "未识别资源" => 8, _ => 20
    };

    private static int FolderOrder(string name) => name switch
    {
        "紫鸾" => 0, "武将/角色" => 1, "小兵" => 2,
        "模型" => 4, "动画" => 5, "骨骼" => 6, "贴图" => 7, "材质" => 8, _ => 20
    };

    private static string[] Parts(string path) => path.Split(
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
}

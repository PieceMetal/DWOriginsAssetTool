namespace DWOriginsAssetTool.Core;

/// <summary>
/// Game-authored equipment convention recovered from the KIDS weapon objects:
/// CharacterEditor stores identity local transforms and HW _L/_R variants select
/// the corresponding character equipment slot. The accessory model's local 1
/// node is the equipment root; geometry/deform descendants remain independent.
/// </summary>
public static class AccessoryAutoFitCatalog
{
    public sealed record Fit(int SourceLocalBoneId, int TargetGlobalBoneId, string Evidence);

    public static Fit? Find(AssetEntry asset, G1MInfo accessory,
        IReadOnlyDictionary<uint, IReadOnlyList<string>> officialObjects)
    {
        var owner = asset.OwnerName ?? PathParts(asset.RelativePath)
            .FirstOrDefault(part => part.StartsWith("HW", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(owner)) return null;
        var inGameConfiguration = officialObjects.Values.Any(names =>
            names.Contains(owner, StringComparer.OrdinalIgnoreCase));
        if (!inGameConfiguration) return null;

        var source = accessory.Bones.Count > 1 ? 1 : accessory.Bones.Count > 0 ? 0 : -1;
        // Shipped weapon objects encode handed variants in their canonical IDs.
        // Unmarked single weapons use the primary/right equipment slot.
        var target = owner.Contains("_L", StringComparison.OrdinalIgnoreCase) ? 103 : 104;
        return new(source, target,
            $"KIDS {owner} → CharacterEditor 装备模型；默认位置 0/0/0、旋转 0/0/0、缩放 1；{(target == 103 ? "左手" : "右手")}装备槽 bone_{target}");
    }

    private static string[] PathParts(string value) => value.Split(
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
}

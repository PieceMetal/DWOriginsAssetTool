namespace DWOriginsAssetTool.Core;

/// <summary>
/// Associates each shipped character action set with its signature weapon set.
/// Weapon model variants sharing the same HW prefix use the same animation set.
/// </summary>
public static class CharacterWeaponCatalog
{
    private static readonly IReadOnlyDictionary<string, string> WeaponPrefixes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["H0100_"] = "HW0002_", // 紫鸾默认武器
            ["H0000_"] = "HW0006_", ["H0002_"] = "HW0001_",
            ["H0004_"] = "HW0003_", ["H0005_"] = "HW0004_",
            ["H0008_"] = "HW0000_", ["H0009_"] = "HW0007_", ["H0010_"] = "HW0008_",
            ["H0011_"] = "HW0032_", // 诸葛亮专用羽扇动作集
            ["H0013_"] = "HW0011_", // 貂蝉双持绳镖
            ["H0014_"] = "HW0009_", ["H0020_"] = "HW0035_", ["H0021_"] = "HW0005_",
            ["H0029_"] = "HW0002_", ["H0030_"] = "HW0046_", ["H0046_"] = "HW0033_",
            ["H0047_"] = "HW0010_", ["H0048_"] = "HW0011_"
        };

    public static string? WeaponPrefixForCharacter(string? characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId)) return null;
        return WeaponPrefixes.FirstOrDefault(pair =>
            characterId.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    public static string? WeaponPrefixFromModel(string? weaponOwner)
    {
        if (string.IsNullOrWhiteSpace(weaponOwner) || !weaponOwner.StartsWith("HW", StringComparison.OrdinalIgnoreCase)) return null;
        var separator = weaponOwner.IndexOf('_');
        return separator >= 0 ? weaponOwner[..(separator + 1)] : weaponOwner;
    }

    public static bool UsesWeapon(string? characterId, string? weaponPrefix) =>
        !string.IsNullOrWhiteSpace(weaponPrefix) &&
        string.Equals(WeaponPrefixForCharacter(characterId), weaponPrefix, StringComparison.OrdinalIgnoreCase);

    public static string Format(string characterId)
    {
        var character = OfficialNameAnnotations.Get(characterId) ?? characterId;
        var prefix = WeaponPrefixForCharacter(characterId);
        var weapon = OfficialNameAnnotations.Get(prefix);
        return string.IsNullOrWhiteSpace(weapon) ? character : $"{character}·{weapon}";
    }

    public static string FormatFolder(string characterId)
    {
        var note = Format(characterId);
        return string.Equals(note, characterId, StringComparison.OrdinalIgnoreCase)
            ? characterId
            : $"{characterId}（{note}）";
    }
}

namespace DWOriginsAssetTool.Core;

/// <summary>
/// Human-readable notes for the stable object identifiers shipped in the game.
/// These notes are display metadata only: KIDS names remain the canonical ids.
/// </summary>
public static class OfficialNameAnnotations
{
    private static readonly (string Prefix, string Note)[] CharacterPrefixes =
    [
        ("H0000_", "夏侯惇"), ("H0001_", "典韦"), ("H0002_", "张辽"),
        ("H0003_", "曹操"), ("H0004_", "周瑜"), ("H0005_", "孙尚香"),
        ("H0006_", "甘宁"), ("H0007_", "孙坚"), ("H0008_", "赵云"),
        ("H0009_", "关羽"), ("H0010_", "张飞"), ("H0011_", "诸葛亮"),
        ("H0012_", "刘备"), ("H0013_", "貂蝉"), ("H0014_", "吕布"),
        ("H0015_", "许褚"), ("H0016_", "夏侯渊"), ("H0017_", "徐晃"),
        ("H0018_", "张郃"), ("H0019_", "太史慈"), ("H0020_", "吕蒙"),
        ("H0021_", "黄盖"), ("H0022_", "周泰"), ("H0023_", "凌统"),
        ("H0024_", "孙策"), ("H0025_", "孙权"), ("H0026_", "庞统"),
        ("H0027_", "董卓"), ("H0028_", "袁绍"), ("H0029_", "张角"),
        ("H0030_", "甄姬"), ("H0031_", "月英"), ("H0032_", "贾诩"),
        ("H0033_", "郭嘉"), ("H0034_", "徐庶"), ("H0035_", "乐进"),
        ("H0036_", "李典"), ("H0037_", "鲁肃"), ("H0038_", "关统"),
        ("H0039_", "于禁"), ("H0040_", "陈宫"), ("H0041_", "荀彧"),
        ("H0042_", "荀攸"), ("H0043_", "程普"), ("H0044_", "周仓"),
        ("H0045_", "袁术"), ("H0046_", "华雄"), ("H0047_", "黄忠"),
        ("H0048_", "貂蝉（同行角色）"), ("H0100_", "紫鸾"),
        ("H0110_", "张角"), ("H2029_", "张角"),
        ("H0300_", "马"), ("H0301_", "马（轻型）"), ("H0302_", "的卢"),
        ("H0303_", "爪黄飞电"), ("H0304_", "绝影"), ("H0305_", "赤兔马"),
        ("H0306_", "驮马"), ("H0307_", "赤兔马（金色）")
    ];

    private static readonly (string Prefix, string Note)[] WeaponPrefixes =
    [
        ("HW0000_", "枪"), ("HW0001_", "双戟"), ("HW0002_", "剑"),
        ("HW0003_", "棍"), ("HW0004_", "飞圈（双环）"), ("HW0005_", "手甲"),
        ("HW0006_", "朴刀"), ("HW0007_", "偃月刀"), ("HW0008_", "矛"),
        ("HW0009_", "方天戟"), ("HW0010_", "弓"), ("HW0011_", "绳镖"),
        ("HW0030_", "九环刀"), ("HW0031_", "双棒"), ("HW0032_", "羽扇"),
        ("HW0034_", "大斧"), ("HW0035_", "戟"), ("HW0036_", "古锭刀"),
        ("HW0039_", "锡杖"), ("HW0040_", "短戟"), ("HW0041_", "三尖刀"),
        ("HW0042_", "刀"), ("HW0043_", "细剑"), ("HW0046_", "镖"),
        ("HW0300_", "小兵剑"), ("HW0301_", "小兵枪"), ("HW0302_", "小兵弓"),
        ("HW0303_", "箭"), ("HW0304_", "盾"), ("HW0305_", "军旗"),
        ("HW0306_", "大盾"), ("HW0307_", "锡杖"), ("HW0308_", "锤"),
        ("HW0309_", "火把"), ("HW0310_", "油壶"), ("HW0322_", "钓竿")
    ];

    public static string? Get(string? officialName)
    {
        if (string.IsNullOrWhiteSpace(officialName)) return null;
        foreach (var (prefix, note) in CharacterPrefixes)
            if (officialName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return note;
        foreach (var (prefix, note) in WeaponPrefixes)
            if (officialName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return note;

        if (officialName.StartsWith("H02", StringComparison.OrdinalIgnoreCase) &&
            officialName.Contains("Soldier_H", StringComparison.OrdinalIgnoreCase)) return "重装小兵";
        if (officialName.StartsWith("H02", StringComparison.OrdinalIgnoreCase) &&
            officialName.Contains("Soldier_M", StringComparison.OrdinalIgnoreCase)) return "中装小兵";
        if (officialName.StartsWith("H02", StringComparison.OrdinalIgnoreCase) &&
            officialName.Contains("Soldier_L", StringComparison.OrdinalIgnoreCase)) return "轻装小兵";
        if (officialName.StartsWith("H02", StringComparison.OrdinalIgnoreCase) &&
            officialName.Contains("People", StringComparison.OrdinalIgnoreCase)) return "平民";
        if (officialName.StartsWith("H0260_", StringComparison.OrdinalIgnoreCase)) return "人群";
        return null;
    }

    public static string Format(string officialName)
    {
        var note = Get(officialName);
        return note is null ? officialName : $"{officialName}（{note}）";
    }
}

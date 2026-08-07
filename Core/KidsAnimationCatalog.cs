namespace DWOriginsAssetTool.Core;

/// <summary>
/// Restores animation ownership from the shipped KIDS object graph.
/// No exported FBX/model grouping participates in this catalog.
/// </summary>
public static class KidsAnimationCatalog
{
    public sealed record Binding(string ResourceId, IReadOnlyList<string> Owners,
        string Category, int Distance, string Evidence);

    private sealed record Record(uint Hash, uint[] Values, string Database);

    public static Dictionary<string, Binding> Build(string gameRoot)
    {
        var assetRoot = Path.Combine(gameRoot, "File", "ASSET", "FDataPackage", "Dev", "fdata_out");
        var kidsRoot = Path.Combine(assetRoot, "KIDSSystemResource", "kidsobjdb");
        if (!Directory.Exists(kidsRoot)) return new(StringComparer.OrdinalIgnoreCase);

        var animationIds = Directory.EnumerateFiles(assetRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".g1a", StringComparison.OrdinalIgnoreCase) ||
                           Path.GetExtension(path).Equals(".g2a", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(id => uint.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out _))
            .Select(id => Convert.ToUInt32(id, 16)).ToHashSet();

        // CE1CommonResource is the canonical aggregate used by the game. The
        // Field_Common and per-owner wrapper databases duplicate these records.
        // Reading the aggregate avoids treating duplicates as extra ownership.
        var databasePaths = new[]
        {
            Path.Combine(kidsRoot, "CE1CommonResource.motor.kidsobjdb"),
            Path.Combine(kidsRoot, "CE1CommonResource.motor.kidssingletondb")
        }.Where(File.Exists).ToArray();
        var records = databasePaths.SelectMany(ReadRecords).ToArray();
        var recordHashes = records.Select(x => x.Hash).ToHashSet();

        var parents = new Dictionary<uint, HashSet<uint>>();
        var direct = new Dictionary<uint, HashSet<uint>>();
        foreach (var record in records)
        {
            foreach (var value in record.Values.Distinct())
            {
                if (value != record.Hash && recordHashes.Contains(value))
                    Add(parents, value, record.Hash);
                if (animationIds.Contains(value)) Add(direct, value, record.Hash);
            }
        }

        var officialNames = KidsNameCatalogReader.BuildOfficialObjectNames(gameRoot);
        var result = new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase);
        foreach (var animationId in animationIds)
        {
            if (!direct.TryGetValue(animationId, out var starts)) continue;
            var owners = FindNearestOwners(starts, parents, officialNames);
            if (owners.Names.Count == 0) continue;
            var names = owners.Names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            var id = $"{animationId:x8}";
            result[id] = new(id, names, CategoryFor(names), owners.Distance,
                $"KIDS:{id} → {string.Join(" + ", names)} ({owners.Distance} object links)");
        }
        return result;
    }

    private static IEnumerable<Record> ReadRecords(string path)
    {
        var data = File.ReadAllBytes(path);
        var starts = new List<int>();
        for (var offset = 0; offset <= data.Length - 8;)
        {
            var relative = data.AsSpan(offset).IndexOf("IDOK0000"u8);
            if (relative < 0) break;
            offset += relative; starts.Add(offset); offset += 8;
        }
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1] : data.Length;
            if (start + 16 > end) continue;
            var values = new uint[(end - start - 16) / 4];
            for (var j = 0; j < values.Length; j++)
                values[j] = BitConverter.ToUInt32(data, start + 16 + j * 4);
            yield return new(BitConverter.ToUInt32(data, start + 12), values, Path.GetFileName(path));
        }
    }

    private static (HashSet<string> Names, int Distance) FindNearestOwners(IEnumerable<uint> starts,
        IReadOnlyDictionary<uint, HashSet<uint>> parents,
        IReadOnlyDictionary<uint, IReadOnlyList<string>> officialNames)
    {
        var visited = new HashSet<uint>();
        var level = starts.Distinct().ToArray();
        for (var distance = 0; distance <= 6 && level.Length > 0; distance++)
        {
            var names = level.Where(officialNames.ContainsKey)
                .SelectMany(x => officialNames[x]).Where(IsActionOwner)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Count > 0) return (names, distance);
            foreach (var hash in level) visited.Add(hash);
            level = level.Where(parents.ContainsKey).SelectMany(x => parents[x])
                .Where(x => !visited.Contains(x)).Distinct().ToArray();
        }
        return (new(StringComparer.OrdinalIgnoreCase), -1);
    }

    private static bool IsActionOwner(string name) =>
        name.StartsWith('H') || name.StartsWith("DSheet_", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("ACT", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Action", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Motion", StringComparison.OrdinalIgnoreCase);

    public static string CategoryFor(IReadOnlyCollection<string> owners)
    {
        if (owners.Count > 1) return "共享动作表";
        var owner = owners.FirstOrDefault() ?? "";
        if (owner.Length > 1 && owner[0] == 'H' && char.IsDigit(owner[1])) return "角色/武将动作表";
        if (owner.StartsWith("DSheet_700_", StringComparison.OrdinalIgnoreCase)) return "事件动作表";
        if (owner.StartsWith("DSheet_701_", StringComparison.OrdinalIgnoreCase)) return "大地图动作表";
        if (owner.Contains("ACT", StringComparison.OrdinalIgnoreCase)) return "ACT 动作表";
        return "游戏动作表";
    }

    private static void Add(Dictionary<uint, HashSet<uint>> map, uint key, uint value)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = [];
        set.Add(value);
    }
}

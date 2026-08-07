using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DWOriginsAssetTool.Core;

public sealed record GameNameEntry(string OfficialName, string Category, string InternalId,
    string SourceDatabase, string Evidence, int SourceCount = 1)
{
    public string? Annotation => OfficialNameAnnotations.Get(OfficialName);
    public string SearchText => $"{OfficialName} {Annotation} {Category} {InternalId} {SourceDatabase}";
}

public sealed record GameNameCatalog(IReadOnlyList<GameNameEntry> Entries, int DatabaseCount,
    long ScannedBytes, DateTimeOffset CreatedAt, IReadOnlyList<GameResourceBinding>? Bindings = null);

public sealed record GameResourceBinding(string OfficialName, string ResourceKind, string ResourceId,
    string RelativePath, string EvidenceChain);

/// <summary>
/// Extracts the name dictionaries deliberately left in KIDS object/scene databases.
/// These strings are editor/runtime identifiers from the shipped game, not guessed labels.
/// </summary>
public static partial class KidsNameCatalogReader
{
    private const int MaxTokenLength = 240;

    public static Task<GameNameCatalog> BuildAsync(string gameRoot,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken token = default) => Task.Run(() => Build(gameRoot, progress, token), token);

    public static GameNameCatalog Build(string gameRoot,
        IProgress<(int Done, int Total, string File)>? progress = null,
        CancellationToken token = default)
    {
        var root = Path.Combine(gameRoot, "File", "ASSET", "FDataPackage", "Dev", "fdata_out", "KIDSSystemResource", "kidsobjdb");
        if (!Directory.Exists(root)) return new([], 0, 0, DateTimeOffset.Now);

        var files = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(IsKidsDatabase).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToArray();
        var occurrences = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.Ordinal);
        long scannedBytes = 0; var done = 0;

        Parallel.ForEach(files, new ParallelOptions { CancellationToken = token }, file =>
        {
            var bytes = File.ReadAllBytes(file);
            Interlocked.Add(ref scannedBytes, bytes.LongLength);
            foreach (var name in ExtractOfficialNames(bytes))
            {
                var sources = occurrences.GetOrAdd(name, _ => new(StringComparer.OrdinalIgnoreCase));
                sources.TryAdd(Path.GetFileName(file), 0);
            }
            var current = Interlocked.Increment(ref done);
            if (current % 25 == 0 || current == files.Length)
                progress?.Report((current, files.Length, Path.GetFileName(file)));
        });

        var entries = occurrences.Select(pair =>
        {
            var sources = pair.Value.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            return new GameNameEntry(pair.Key, Classify(pair.Key), ExtractInternalId(pair.Key),
                sources[0], "游戏随包 KIDS 数据库明文", sources.Length);
        }).OrderBy(x => CategoryOrder(x.Category)).ThenBy(x => x.OfficialName, StringComparer.OrdinalIgnoreCase).ToList();
        var bindings = BuildResourceBindings(gameRoot, root, entries, token);
        return new(entries, files.Length, scannedBytes, DateTimeOffset.Now, bindings);
    }

    public static async Task SaveJsonAsync(GameNameCatalog catalog, string path, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(catalog,
            new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), token);
    }

    public static IReadOnlyDictionary<uint, IReadOnlyList<string>> BuildOfficialWrapperNames(string gameRoot)
    {
        var kidsRoot = Path.Combine(gameRoot, "File", "ASSET", "FDataPackage", "Dev", "fdata_out",
            "KIDSSystemResource", "kidsobjdb");
        var project = Path.Combine(kidsRoot, "ProjectCommonResource.motor.kidsscndb");
        if (!File.Exists(project)) return new Dictionary<uint, IReadOnlyList<string>>();
        var data = File.ReadAllBytes(project);
        if (!TryReadPrimaryNameTable(data, out var names, out _, out _))
            return new Dictionary<uint, IReadOnlyList<string>>();
        var heroIndex = names.FindIndex(x => x == "H0100_Hero0");
        if (heroIndex < 0) return new Dictionary<uint, IReadOnlyList<string>>();
        var databaseIds = Directory.EnumerateFiles(kidsRoot, "*.kidsobjdb")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(x => uint.TryParse(x, System.Globalization.NumberStyles.HexNumber, null, out _))
            .Select(x => Convert.ToUInt32(x, 16)).ToHashSet();
        var wrapperStart = FindAlignedReferenceArray(data, names.Count, heroIndex, databaseIds, 0x1b85e948);
        if (wrapperStart < 0) return new Dictionary<uint, IReadOnlyList<string>>();
        return Enumerable.Range(0, names.Count)
            .Select(i => (Id: BitConverter.ToUInt32(data, wrapperStart + i * 4), Name: names[i]))
            .Where(x => databaseIds.Contains(x.Id))
            .GroupBy(x => x.Id)
            .ToDictionary(x => x.Key,
                x => (IReadOnlyList<string>)x.Select(y => y.Name).Distinct(StringComparer.Ordinal).ToArray());
    }

    public static IReadOnlyDictionary<uint, IReadOnlyList<string>> BuildOfficialObjectNames(string gameRoot)
    {
        var project = Path.Combine(gameRoot, "File", "ASSET", "FDataPackage", "Dev", "fdata_out",
            "KIDSSystemResource", "kidsobjdb", "ProjectCommonResource.motor.kidsscndb");
        if (!File.Exists(project)) return new Dictionary<uint, IReadOnlyList<string>>();
        var data = File.ReadAllBytes(project);
        if (!TryReadPrimaryNameTable(data, out var names, out var hashes, out _))
            return new Dictionary<uint, IReadOnlyList<string>>();
        return Enumerable.Range(0, Math.Min(names.Count, hashes.Length))
            .GroupBy(i => hashes[i])
            .ToDictionary(x => x.Key,
                x => (IReadOnlyList<string>)x.Select(i => names[i]).Distinct(StringComparer.Ordinal).ToArray());
    }

    internal static IReadOnlyList<string> ExtractOfficialNames(byte[] data)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var start = -1;
        for (var i = 0; i <= data.Length; i++)
        {
            var printable = i < data.Length && data[i] is >= 0x20 and <= 0x7e;
            if (printable) { if (start < 0) start = i; continue; }
            if (start >= 0)
            {
                var length = i - start;
                if (length is >= 4 and <= MaxTokenLength)
                {
                    var value = Encoding.ASCII.GetString(data, start, length).Trim();
                    if (IsOfficialName(value)) seen.Add(value);
                }
                start = -1;
            }
        }
        return seen.ToList();
    }

    private static bool IsOfficialName(string value)
    {
        if (value is "IDOK0000" or "_DOK0000" or "Const" || value.StartsWith("IDOK0000", StringComparison.Ordinal)) return false;
        if (value.Any(c => c is < ' ' or > '~')) return false;
        var letters = value.Count(char.IsLetter);
        if (letters < 2) return false;
        var useful = value.Count(c => char.IsLetterOrDigit(c) || c is '_' or ':' or '.' or '/' or '-');
        if (useful < value.Length * .82) return false;
        // KIDS names use identifiers, paths, namespaces and numbered content IDs.
        return value.Contains('_') || value.Contains("::", StringComparison.Ordinal) ||
               value.Contains('.') || value.Contains('/') ||
               ContentIdRegex().IsMatch(value) || CamelCaseRegex().IsMatch(value);
    }

    private static string Classify(string name)
    {
        if (HeroRegex().IsMatch(name)) return "紫鸾";
        if (SoldierRegex().IsMatch(name)) return "小兵";
        if (OfficerRegex().IsMatch(name)) return "武将/角色";
        if (name.StartsWith('S') && StageRegex().IsMatch(name)) return "关卡";
        if (name.Contains("motion", StringComparison.OrdinalIgnoreCase) || name.Contains("animation", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ACT_", StringComparison.OrdinalIgnoreCase)) return "动作/动画";
        if (name.Contains("model", StringComparison.OrdinalIgnoreCase)) return "模型";
        if (name.Contains("weapon", StringComparison.OrdinalIgnoreCase) || name.StartsWith('W') && ContentIdRegex().IsMatch(name)) return "武器";
        if (name.Contains("effect", StringComparison.OrdinalIgnoreCase)) return "特效";
        if (name.Contains("texture", StringComparison.OrdinalIgnoreCase) || name.Contains("shader", StringComparison.OrdinalIgnoreCase)) return "渲染";
        if (name.Contains("sound", StringComparison.OrdinalIgnoreCase) || name.Contains("voice", StringComparison.OrdinalIgnoreCase)) return "声音";
        if (name.Contains("screen", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ui_", StringComparison.OrdinalIgnoreCase)) return "界面";
        if (name.Contains("::", StringComparison.Ordinal)) return "引擎符号";
        return "游戏对象";
    }

    private static string ExtractInternalId(string name)
    {
        var match = InternalIdRegex().Match(name);
        return match.Success ? match.Value : "";
    }

    private static int CategoryOrder(string category) => category switch
    {
        "紫鸾" => 0, "武将/角色" => 1, "小兵" => 2, "动作/动画" => 3,
        "模型" => 4, "武器" => 5, "关卡" => 6, _ => 20
    };

    private static bool IsKidsDatabase(string path) => Path.GetExtension(path).StartsWith(".kids", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<GameResourceBinding> BuildResourceBindings(string gameRoot, string kidsRoot,
        IReadOnlyList<GameNameEntry> entries, CancellationToken token)
    {
        var project = Path.Combine(kidsRoot, "ProjectCommonResource.motor.kidsscndb");
        if (!File.Exists(project)) return [];
        var data = File.ReadAllBytes(project);
        if (!TryReadPrimaryNameTable(data, out var names, out var hashes, out var stringStart)) return [];
        var heroIndex = names.FindIndex(x => x == "H0100_Hero0");
        if (heroIndex < 0) return [];

        var databasePaths = Directory.EnumerateFiles(kidsRoot, "*.kidsobjdb")
            .Select(x => (Path: x, Name: Path.GetFileNameWithoutExtension(x)))
            .Where(x => uint.TryParse(x.Name, System.Globalization.NumberStyles.HexNumber, null, out _))
            .ToDictionary(x => Convert.ToUInt32(x.Name, 16), x => x.Path);
        var databaseIds = databasePaths.Keys.ToHashSet();
        // The shipped scene DB stores this table at a two-byte alignment and several nearby
        // shifted windows happen to contain more values that look like database IDs. Anchor the
        // table with the independently verified Hero0 wrapper instead of choosing a false window.
        var wrapperStart = FindAlignedReferenceArray(data, names.Count, heroIndex, databaseIds, 0x1b85e948);
        if (wrapperStart < 0) return [];

        var assetRoot = Path.Combine(gameRoot, "File", "ASSET", "FDataPackage", "Dev", "fdata_out");
        var assets = BuildAssetIdMap(assetRoot);
        var bindings = new List<GameResourceBinding>();
        var referencedObjectDatabases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var wrapperId = BitConverter.ToUInt32(data, wrapperStart + i * 4);
            var wrapper = Path.Combine(kidsRoot, $"{wrapperId:x8}.kidsobjdb");
            if (!File.Exists(wrapper)) continue;
            var visited = new HashSet<uint>();
            CollectReferencedAssets(names[i], wrapperId, wrapper, databasePaths, assets, visited, bindings, 0);
            CollectReferencedDatabasePaths(wrapperId, databasePaths, referencedObjectDatabases, new HashSet<uint>(), 0);
        }
        AddObjectGraphBindings(names, hashes, kidsRoot, referencedObjectDatabases, assets, bindings, token);
        return bindings.DistinctBy(x => (x.OfficialName, x.ResourceKind, x.ResourceId)).
            OrderBy(x => x.OfficialName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ResourceKind).ThenBy(x => x.ResourceId).ToList();
    }

    private static void CollectReferencedDatabasePaths(uint databaseId,
        IReadOnlyDictionary<uint, string> databasePaths, HashSet<string> output,
        HashSet<uint> visited, int depth)
    {
        if (depth > 2 || !visited.Add(databaseId) || !databasePaths.TryGetValue(databaseId, out var path)) return;
        output.Add(path);
        var bytes = File.ReadAllBytes(path);
        for (var offset = 0; offset + 4 <= bytes.Length; offset += 4)
        {
            var child = BitConverter.ToUInt32(bytes, offset);
            if (databasePaths.ContainsKey(child))
                CollectReferencedDatabasePaths(child, databasePaths, output, visited, depth + 1);
        }
    }

    private sealed record ObjectRecord(string Database, uint ObjectHash, byte[] Data);

    /// <summary>
    /// Resolves character component models stored as in-database KIDS objects. These references
    /// are object hashes (not filenames), which is why simply looking for {hash}.kidsobjdb misses
    /// the soldier models.
    /// </summary>
    private static void AddObjectGraphBindings(IReadOnlyList<string> names, IReadOnlyList<uint> hashes,
        string kidsRoot, IReadOnlyCollection<string> referencedObjectDatabases,
        IReadOnlyDictionary<uint, (string Kind, string Path)> assets,
        List<GameResourceBinding> output, CancellationToken token)
    {
        var sourcePaths = new[]
        {
            Path.Combine(kidsRoot, "CE1CommonResource.motor.kidsobjdb"),
            Path.Combine(kidsRoot, "CharacterEditor.kidssingletondb"),
            Path.Combine(kidsRoot, "MaterialEditor.kidssingletondb")
        }.Concat(referencedObjectDatabases).Distinct(StringComparer.OrdinalIgnoreCase);
        var records = new Dictionary<uint, List<ObjectRecord>>();
        foreach (var path in sourcePaths)
        {
            if (!File.Exists(path)) continue;
            var sourceName = Path.GetFileName(path);
            var bytes = File.ReadAllBytes(path);
            var starts = new List<int>();
            for (var offset = 0; offset + 16 <= bytes.Length;)
            {
                offset = IndexOf(bytes, "IDOK0000"u8, offset);
                if (offset < 0) break;
                starts.Add(offset); offset += 8;
            }
            for (var i = 0; i < starts.Count; i++)
            {
                var start = starts[i];
                var end = i + 1 < starts.Count ? starts[i + 1] : bytes.Length;
                var hash = BitConverter.ToUInt32(bytes, start + 12);
                var record = new byte[end - start];
                Buffer.BlockCopy(bytes, start, record, 0, record.Length);
                if (!records.TryGetValue(hash, out var list)) records[hash] = list = [];
                list.Add(new(sourceName, hash, record));
            }
        }

        for (var i = 0; i < names.Count && i < hashes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (!names[i].StartsWith('H')) continue;
            var queue = new Queue<(uint Hash, int Depth, string Chain)>();
            var visited = new HashSet<uint>();
            queue.Enqueue((hashes[i], 0, $"{names[i]} [{hashes[i]:x8}]"));
            while (queue.Count > 0)
            {
                var (hash, depth, chain) = queue.Dequeue();
                if (depth > 4 || !visited.Add(hash) || !records.TryGetValue(hash, out var matches)) continue;
                foreach (var record in matches)
                {
                    var recordChain = $"{chain} → {record.Database}:{hash:x8}";
                    for (var offset = 0; offset + 4 <= record.Data.Length; offset += 4)
                    {
                        var value = BitConverter.ToUInt32(record.Data, offset);
                        // CharacterEditor model records keep their G1M resource in the typed field
                        // at +0x2a0 (record sizes vary by character type). Restricting to that field avoids
                        // treating unrelated integers as asset IDs merely because filenames are hashes.
                        if (record.Database.Equals("CharacterEditor.kidssingletondb", StringComparison.OrdinalIgnoreCase) &&
                            record.Data.Length >= 0x2a4 && offset == 0x2a0 &&
                            assets.TryGetValue(value, out var asset) && asset.Kind == "模型")
                            output.Add(new(names[i], asset.Kind, $"{value:x8}", asset.Path,
                                $"{recordChain} +0x{offset:x} → {value:x8}"));
                        if (depth < 4 && records.ContainsKey(value) && !visited.Contains(value))
                            queue.Enqueue((value, depth + 1, recordChain));
                    }
                }
            }
        }
    }

    private static int IndexOf(byte[] source, ReadOnlySpan<byte> value, int start)
    {
        var relative = source.AsSpan(start).IndexOf(value);
        return relative < 0 ? -1 : start + relative;
    }

    private static void CollectReferencedAssets(string officialName, uint databaseId, string path, IReadOnlyDictionary<uint, string> databasePaths,
        IReadOnlyDictionary<uint, (string Kind, string Path)> assets, HashSet<uint> visited,
        List<GameResourceBinding> output, int depth)
    {
        if (depth > 2 || !visited.Add(databaseId)) return;
        var bytes = File.ReadAllBytes(path);
        for (var offset = 0; offset + 4 <= bytes.Length; offset++)
        {
            var id = BitConverter.ToUInt32(bytes, offset);
            if (assets.TryGetValue(id, out var asset))
                output.Add(new(officialName, asset.Kind, $"{id:x8}", asset.Path,
                    $"{officialName} → {databaseId:x8}.kidsobjdb → {id:x8}"));
        }
        for (var offset = 0; offset + 4 <= bytes.Length; offset += 4)
        {
            var child = BitConverter.ToUInt32(bytes, offset);
            if (databasePaths.TryGetValue(child, out var childPath)) CollectReferencedAssets(officialName, child, childPath, databasePaths, assets, visited, output, depth + 1);
        }
    }

    private static Dictionary<uint, (string Kind, string Path)> BuildAssetIdMap(string root)
    {
        var result = new Dictionary<uint, (string, string)>();
        if (!Directory.Exists(root)) return result;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            if (!uint.TryParse(stem, System.Globalization.NumberStyles.HexNumber, null, out var id)) continue;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var kind = extension switch { ".g1m" or ".g1mx" => "模型", ".g1a" or ".g2a" => "动画", ".g1s" => "骨骼", ".g1t" => "贴图", _ => "资源" };
            if (kind is "模型" or "动画" or "骨骼" or "贴图") result.TryAdd(id, (kind, Path.GetRelativePath(root, path)));
        }
        return result;
    }

    private static int FindAlignedReferenceArray(byte[] data, int count, int knownIndex,
        HashSet<uint> databaseIds, uint knownReferenceId)
    {
        for (var offset = 0; offset + 4 <= data.Length; offset++)
        {
            if (BitConverter.ToUInt32(data, offset) != knownReferenceId) continue;
            var anchoredStart = offset - knownIndex * 4;
            if (anchoredStart < 0 || anchoredStart + count * 4 > data.Length) continue;
            var anchoredScore = 0;
            for (var i = 0; i < count; i++)
                if (databaseIds.Contains(BitConverter.ToUInt32(data, anchoredStart + i * 4))) anchoredScore++;
            if (anchoredScore >= 3) return anchoredStart;
        }

        var bestStart = -1; var bestScore = 0;
        for (var offset = 0; offset + 4 <= data.Length; offset++)
        {
            var value = BitConverter.ToUInt32(data, offset);
            if (!databaseIds.Contains(value)) continue;
            var start = offset - knownIndex * 4;
            if (start < 0 || start + count * 4 > data.Length) continue;
            var score = 0;
            for (var i = 0; i < count; i++) if (databaseIds.Contains(BitConverter.ToUInt32(data, start + i * 4))) score++;
            if (score > bestScore) { bestScore = score; bestStart = start; }
        }
        return bestScore >= 3 ? bestStart : -1;
    }

    private static bool TryReadPrimaryNameTable(byte[] data, out List<string> names, out uint[] hashes, out int stringStart)
    {
        names = []; hashes = []; stringStart = -1;
        for (var start = 0; start < data.Length; start++)
        {
            var list = new List<string>(); var pos = start;
            while (pos < data.Length)
            {
                var end = Array.IndexOf(data, (byte)0, pos); if (end < 0 || end == pos || end - pos > MaxTokenLength) break;
                var valid = true; for (var i = pos; i < end; i++) if (data[i] is < 0x20 or > 0x7e) { valid = false; break; }
                if (!valid) break; list.Add(Encoding.ASCII.GetString(data, pos, end - pos)); pos = end + 1;
            }
            if (list.Count > names.Count && list.Any(x => x == "H0100_Hero0")) { names = list; stringStart = start; }
            start = Math.Max(start, pos - 1);
        }
        if (names.Count == 0 || stringStart < names.Count * 4) return false;
        hashes = new uint[names.Count];
        for (var i = 0; i < names.Count; i++) hashes[i] = BitConverter.ToUInt32(data, stringStart - names.Count * 4 + i * 4);
        return true;
    }

    [GeneratedRegex(@"(?:^|_)H0100_Hero(?:\d|_|$)", RegexOptions.IgnoreCase)] private static partial Regex HeroRegex();
    [GeneratedRegex(@"(?:^|_)H02\d{2}_Soldier(?:_|$)", RegexOptions.IgnoreCase)] private static partial Regex SoldierRegex();
    [GeneratedRegex(@"(?:^|_)H0[145]\d{2}_[A-Za-z]", RegexOptions.IgnoreCase)] private static partial Regex OfficerRegex();
    [GeneratedRegex(@"^S\d{3}_", RegexOptions.IgnoreCase)] private static partial Regex StageRegex();
    [GeneratedRegex(@"^[A-Z]{1,5}\d{2,4}(?:_|$)", RegexOptions.IgnoreCase)] private static partial Regex ContentIdRegex();
    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]+[A-Z][A-Za-z0-9]+$")] private static partial Regex CamelCaseRegex();
    [GeneratedRegex(@"(?:^|_)(?:H|EV|S|W|ACT)\d{3,4}", RegexOptions.IgnoreCase)] private static partial Regex InternalIdRegex();
}

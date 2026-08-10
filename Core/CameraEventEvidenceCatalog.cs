using System.Text;
using System.Text.RegularExpressions;

namespace DWOriginsAssetTool.Core;

public static class CameraEventEvidenceCatalog
{
    private const uint EffectResourceType = 0xec5647e9;
    private static readonly uint[] EffectChildProperties =
    [
        0x96d20025, 0x8d69c872, 0xd78969e0, 0xf92c5190, 0x98bb014b
    ];
    private static readonly string[] CameraKeywords =
    [
        "camera", "Camera", "FOV", "Fov", "fov", "Shake", "shake", "View", "view",
        "Focus", "focus", "Target", "target"
    ];

    public sealed record EffectResourceEvidence(uint ResourceHash, IReadOnlyList<ChildResourceEvidence> Children,
        bool HasCameraKeyword, string Summary);

    public sealed record ChildResourceEvidence(uint Hash, string? RelativePath, long Size, string Kind,
        bool HasCameraKeyword, IReadOnlyList<string> KeywordHits);

    public static IReadOnlyDictionary<uint, EffectResourceEvidence> Build(string gameRoot,
        IEnumerable<uint> effectResourceHashes)
    {
        var distinct = effectResourceHashes.Where(x => x != 0).Distinct().ToArray();
        if (distinct.Length == 0) return new Dictionary<uint, EffectResourceEvidence>();
        var kidsRoot = Path.Combine(gameRoot, "File", "ASSET", "FDataPackage", "Dev", "fdata_out",
            "KIDSSystemResource", "kidsobjdb");
        var fdataRoot = Path.Combine(gameRoot, "File", "ASSET", "FDataPackage", "Dev", "fdata_out");
        var records = new Dictionary<uint, KidsObjectDatabase.Record>();
        foreach (var name in new[]
                 {
                     "CE1EffectResource.motor.kidsscndb",
                     "Field_Common.effect.kidsobjdb",
                     "Field_Common.effect.kidssingletondb"
                 })
        {
            var path = Path.Combine(kidsRoot, name);
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var record in KidsObjectDatabase.Read(path).Where(x => x.TypeHash == EffectResourceType))
                    records.TryAdd(record.ObjectHash, record);
            }
            catch
            {
                // Evidence is diagnostic; optional DB read errors must not block preview.
            }
        }

        var result = new Dictionary<uint, EffectResourceEvidence>();
        foreach (var hash in distinct)
        {
            var children = new List<ChildResourceEvidence>();
            if (records.TryGetValue(hash, out var record))
            {
                foreach (var child in EffectChildProperties
                             .SelectMany(prop => record.Get(prop)?.UInt32Values ?? [])
                             .Where(x => x != 0)
                             .Distinct())
                {
                    children.Add(InspectChildResource(fdataRoot, child));
                }
            }
            var hasCameraKeyword = children.Any(x => x.HasCameraKeyword);
            var summary = BuildSummary(hash, children, hasCameraKeyword);
            result[hash] = new(hash, children, hasCameraKeyword, summary);
        }
        return result;
    }

    private static ChildResourceEvidence InspectChildResource(string fdataRoot, uint hash)
    {
        var path = FindKnownResourceFile(fdataRoot, hash);
        if (path is null)
            return new(hash, null, 0, "missing", false, []);
        var info = new FileInfo(path);
        var kind = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var hits = FindCameraKeywordHits(path);
        return new(hash, Path.GetRelativePath(fdataRoot, path), info.Length, kind, hits.Count > 0, hits);
    }

    private static string? FindKnownResourceFile(string fdataRoot, uint hash)
    {
        if (!Directory.Exists(fdataRoot)) return null;
        var stem = $"{hash:x8}";
        foreach (var domain in Directory.EnumerateDirectories(fdataRoot))
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(domain, "g1e", stem + ".g1e"),
                         Path.Combine(domain, "g1em", stem + ".g1em"),
                         Path.Combine(domain, "1A6300FD", stem + ".1a6300fd"),
                         Path.Combine(domain, "1a6300fd", stem + ".1a6300fd")
                     })
            {
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static IReadOnlyList<string> FindCameraKeywordHits(string path)
    {
        const int maximumBytes = 256 * 1024;
        try
        {
            using var stream = File.OpenRead(path);
            var length = (int)Math.Min(maximumBytes, stream.Length);
            var data = new byte[length];
            _ = stream.Read(data, 0, data.Length);
            var text = Encoding.ASCII.GetString(data);
            return Regex.Matches(text, @"[\x20-\x7E]{4,}")
                .Select(x => x.Value)
                .Where(value => CameraKeywords.Any(value.Contains))
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string BuildSummary(uint hash, IReadOnlyList<ChildResourceEvidence> children,
        bool hasCameraKeyword)
    {
        if (children.Count == 0)
            return $"{hash:x8}: EffectResource 记录未找到，或没有可解析子资源。";
        var parts = children.Select(x =>
        {
            var path = x.RelativePath is null ? "未找到文件" : $"{x.Kind}:{x.RelativePath}";
            var marker = x.HasCameraKeyword ? "发现相机关键字" : "未见相机关键字";
            return $"{x.Hash:x8} {path} {marker}";
        });
        return $"{hash:x8}: {string.Join("; ", parts)}" +
               (hasCameraKeyword ? "" : "。当前证据显示它是特效资源链，不是已解码镜头参数。");
    }
}

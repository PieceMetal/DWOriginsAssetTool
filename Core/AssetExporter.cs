using System.Text.Json;

namespace DWOriginsAssetTool.Core;

public static class AssetExporter
{
    public static async Task<int> ExportOrganizedAsync(IEnumerable<AssetEntry> assets, string destination,
        IProgress<(int Done, int Total, string Name)>? progress = null, CancellationToken token = default)
    {
        var items = assets.ToList(); var done = 0;
        foreach (var asset in items)
        {
            token.ThrowIfCancellationRequested();
            var targetDir = GetOrganizedDirectory(asset, destination);
            Directory.CreateDirectory(targetDir);
            var target = Path.Combine(targetDir, asset.ExportFileName);
            if (!File.Exists(target) || new FileInfo(target).Length != asset.Size)
            {
                await using var source = new FileStream(asset.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                await source.CopyToAsync(output, token);
            }
            progress?.Report((++done, items.Count, asset.DisplayName));
        }
        var manifest = items.Select(x => new { x.OfficialName, x.DisplayName, x.HashId, Kind = x.Kind.ToString(), x.Extension, x.Size, x.RelativePath }).ToList();
        await File.WriteAllTextAsync(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), token);
        return done;
    }

    private static string GetOrganizedDirectory(AssetEntry asset, string destination)
    {
        if (!string.IsNullOrWhiteSpace(asset.OfficialName))
        {
            var owner = asset.OfficialName.Split(" · ", 2)[0].Trim();
            var section = owner.Length > 0 ? char.ToUpperInvariant(owner[0]) switch
            {
                'H' => "character", 'W' => "item", 'S' => "stage", 'E' => "event", _ => "named"
            } : "named";
            return Path.Combine(destination, section, Sanitize(owner), asset.Kind.ToString().ToLowerInvariant());
        }
        return Path.Combine(destination, "_unresolved", asset.Kind.ToString(), Sanitize(asset.Domain));
    }

    public static void ExportObj(G1MInfo model, string destination)
    {
        using var writer = new StreamWriter(destination, false, new System.Text.UTF8Encoding(false));
        writer.WriteLine("# DWOriginsAssetTool G1M geometry export");
        var baseVertex = 1;
        for (var p = 0; p < model.Parts.Count; p++)
        {
            var part = model.Parts[p]; writer.WriteLine($"o mesh_{p:D4}");
            for (var i = 0; i + 2 < part.Positions.Length; i += 3)
                writer.WriteLine(FormattableString.Invariant($"v {part.Positions[i]} {part.Positions[i + 1]} {part.Positions[i + 2]}"));
            if (!part.TriangleStrip)
            {
                for (var i = 0; i + 2 < part.Indices.Length; i += 3)
                {
                    var a = part.Indices[i]; var b = part.Indices[i + 1]; var c = part.Indices[i + 2];
                    var vertexCount = part.Positions.Length / 3;
                    if (a == b || b == c || a == c || a >= vertexCount || b >= vertexCount || c >= vertexCount) continue;
                    writer.WriteLine($"f {baseVertex + a} {baseVertex + b} {baseVertex + c}");
                }
            }
            else
            {
                for (var i = 0; i + 2 < part.Indices.Length; i++)
                {
                    var a = part.Indices[i]; var b = part.Indices[i + 1]; var c = part.Indices[i + 2];
                    if (a == b || b == c || a == c || a >= 0xFFFF || b >= 0xFFFF || c >= 0xFFFF) continue;
                    if ((i & 1) != 0) (a, b) = (b, a);
                    writer.WriteLine($"f {baseVertex + a} {baseVertex + b} {baseVertex + c}");
                }
            }
            baseVertex += part.Positions.Length / 3;
        }
    }

    private static string Sanitize(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

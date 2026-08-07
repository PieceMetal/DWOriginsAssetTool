using System.Collections.Concurrent;

namespace DWOriginsAssetTool.Core;

public static class AssetScanner
{
    private static readonly Dictionary<string, AssetKind> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".g1m"] = AssetKind.Model, [".g1mx"] = AssetKind.Model, [".g1co"] = AssetKind.Collision,
        [".g1cox"] = AssetKind.Collision, [".g1a"] = AssetKind.Animation, [".g2a"] = AssetKind.Animation,
        [".g1s"] = AssetKind.Skeleton, [".rigbin"] = AssetKind.Skeleton,
        [".g1t"] = AssetKind.Texture, [".ktid"] = AssetKind.Texture, [".texinfo"] = AssetKind.Texture,
        [".mtl"] = AssetKind.Material, [".kts"] = AssetKind.Material, [".grp"] = AssetKind.Material,
        [".fdata"] = AssetKind.Package, [".bin"] = AssetKind.Package, [".webm"] = AssetKind.Movie
    };

    public static Task<List<AssetEntry>> ScanAsync(string root, IProgress<(int Count, string Path)>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var output = new ConcurrentBag<AssetEntry>();
        var count = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        Parallel.ForEach(Directory.EnumerateFiles(root, "*", options), new ParallelOptions { CancellationToken = cancellationToken }, path =>
        {
            var extension = Path.GetExtension(path);
            var kind = Kinds.GetValueOrDefault(extension, AssetKind.Other);
            if (kind == AssetKind.Other) return;
            var info = new FileInfo(path);
            output.Add(new AssetEntry
            {
                FullPath = path,
                RelativePath = Path.GetRelativePath(root, path),
                Kind = kind,
                Size = info.Length,
                ResourceId = TryReadResourceId(path)
            });
            var current = Interlocked.Increment(ref count);
            if (current % 1000 == 0) progress?.Report((current, path));
        });
        return output.OrderBy(x => x.Kind).ThenBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }, cancellationToken);

    private static string? TryReadResourceId(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.Length == 8 && uint.TryParse(stem, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out _) ? stem.ToLowerInvariant() : null;
    }

    public static AssetKind ValidateMagic(string path, AssetKind fallback)
    {
        Span<byte> header = stackalloc byte[8];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) < 4) return fallback;
        var magic = System.Text.Encoding.ASCII.GetString(header[..4]);
        return magic switch
        {
            "_M1G" => AssetKind.Model, "_A1G" or "_A2G" => AssetKind.Animation,
            "_S1G" or "_S2G" or "BGIR" => AssetKind.Skeleton, "GT1G" => AssetKind.Texture,
            "PDRK" or "IDRK" => AssetKind.Package, _ => fallback
        };
    }
}

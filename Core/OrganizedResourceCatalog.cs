using System.Text.Json;

namespace DWOriginsAssetTool.Core;

/// <summary>
/// Restores real resource ids and animation ownership recorded by the shipped
/// KIDS object databases.
/// </summary>
public static class OrganizedResourceCatalog
{
    public static Dictionary<string, string> ReadManifestResourceIds(string organizedRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestRoot = Path.Combine(organizedRoot, "_manifests");
        if (!Directory.Exists(manifestRoot)) return result;

        foreach (var path in Directory.EnumerateFiles(manifestRoot, "*.manifest.json", SearchOption.TopDirectoryOnly))
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("Files", out var files)) continue;
            foreach (var file in files.EnumerateArray())
            {
                if (!file.TryGetProperty("Id", out var idElement) ||
                    !file.TryGetProperty("RelativePath", out var pathElement)) continue;
                var id = idElement.GetString()?.Trim();
                var relative = pathElement.GetString();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(relative)) continue;
                if (id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) id = id[2..];
                result.TryAdd(Normalize(relative), id.ToLowerInvariant());
            }
        }
        return result;
    }

    public static int ApplyResourceIds(IEnumerable<AssetEntry> assets, IReadOnlyDictionary<string, string> ids)
    {
        var applied = 0;
        foreach (var asset in assets)
        {
            if (!ids.TryGetValue(Normalize(asset.RelativePath), out var id)) continue;
            asset.ResourceId = id;
            applied++;
        }
        return applied;
    }

    public static int ApplyGameAnimationCatalog(IEnumerable<AssetEntry> assets, string gameRoot,
        IReadOnlyDictionary<string, ActionCameraCatalog.Binding>? cameraBindings = null)
    {
        var bindings = KidsAnimationCatalog.Build(gameRoot);
        var cameras = cameraBindings ?? ActionCameraCatalog.Build(gameRoot);
        var cameraOwners = cameras.Values.GroupBy(x => NormalizeId(x.CameraAnimationId))
            .ToDictionary(x => x.Key,
                x => (IReadOnlyList<string>)x.SelectMany(camera =>
                        bindings.TryGetValue(NormalizeId(camera.AnimationId), out var action) ? action.Owners : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(y => y, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var applied = 0;
        foreach (var asset in assets.Where(x => x.Kind == AssetKind.Animation && x.ResourceId is not null))
        {
            var id = NormalizeId(asset.ResourceId!);
            if (cameraOwners.TryGetValue(id, out var owners))
            {
                asset.IsActionCamera = true;
                asset.GameActionCategory = "动画镜头";
                asset.GameActionOwners = owners;
                asset.ClassificationEvidence = string.Join(" | ", cameras.Values
                    .Where(x => NormalizeId(x.CameraAnimationId) == id).Select(x => x.Evidence));
                asset.OwnerName = owners.Count == 1 ? owners[0] : null;
                asset.OfficialName = $"{(owners.Count == 1 ? owners[0] : "Shared_Action")}_camera_{id}";
                applied++;
                continue;
            }
            if (!bindings.TryGetValue(id, out var binding)) continue;
            asset.GameActionCategory = binding.Category;
            asset.GameActionOwners = binding.Owners;
            asset.ClassificationEvidence = binding.Evidence;
            asset.OwnerName = binding.Owners.Count == 1 ? binding.Owners[0] : null;
            asset.OfficialName = $"{(binding.Owners.Count == 1 ? binding.Owners[0] : "Shared_Action_Table")}_animation_{asset.ResourceId}";
            if (cameras.TryGetValue(id, out var camera))
            {
                asset.ActionCameraResourceId = camera.CameraAnimationId;
                asset.ActionCameraEvidence = camera.Evidence;
            }
            applied++;
        }
        return applied;
    }

    private static string Normalize(string path) => path.Replace(Path.AltDirectorySeparatorChar,
        Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

    private static string NormalizeId(string value)
    {
        value = value.Trim();
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
    }
}

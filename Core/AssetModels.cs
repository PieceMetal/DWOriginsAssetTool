using System.Globalization;

namespace DWOriginsAssetTool.Core;

public enum AssetKind { Model, Animation, Skeleton, Texture, Material, Collision, Package, Movie, Other }

public sealed class AssetEntry
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required AssetKind Kind { get; init; }
    public required long Size { get; init; }
    public string? ResourceId { get; set; }
    public string? OwnerName { get; set; }
    public string? GameActionCategory { get; set; }
    public IReadOnlyList<string>? GameActionOwners { get; set; }
    public string? ClassificationEvidence { get; set; }
    public bool IsActionCamera { get; set; }
    public string? ActionCameraResourceId { get; set; }
    public string? ActionCameraEvidence { get; set; }
    public string Extension => Path.GetExtension(FullPath).TrimStart('.').ToUpperInvariant();
    public string Name => Path.GetFileName(FullPath);
    public string HashId => ResourceId ?? Path.GetFileNameWithoutExtension(FullPath);
    public string? OfficialName { get; set; }
    public string DisplayName => string.IsNullOrWhiteSpace(OfficialName) ? Name
        : OfficialName!.EndsWith($".{Extension}", StringComparison.OrdinalIgnoreCase)
            ? OfficialName
            : $"{OfficialName}.{Extension.ToLowerInvariant()}";
    public string? NameAnnotation => OfficialNameAnnotations.Get(OwnerName ?? OfficialName);
    public string AnnotatedDisplayName => NameAnnotation is { Length: > 0 } note
        ? $"{DisplayName}（{note}）" : DisplayName;
    public string TreeDisplayName => DisplayName;
    // Disk/export names stay ASCII and retain the game's canonical object ID.
    // Chinese character/weapon notes are UI metadata only.
    public string ExportFileName => SanitizeFileName(EnglishFileName());
    public string SearchText => $"{OfficialName} {OwnerName} {NameAnnotation} {ResourceId} {GameActionCategory} {ClassificationEvidence} {ActionCameraResourceId} {ActionCameraEvidence} {(IsActionCamera ? "动作镜头 相机 camera" : "")} {string.Join(' ', GameActionOwners ?? [])} {string.Join(' ', (GameActionOwners ?? []).Select(OfficialNameAnnotations.Format))} {Name} {RelativePath} {Kind} {Domain}";
    public string Domain
    {
        get
        {
            var parts = RelativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var expanded = Array.FindIndex(parts, x => x.Equals("fdata_out", StringComparison.OrdinalIgnoreCase));
            if (expanded >= 0 && expanded + 1 < parts.Length) return parts[expanded + 1];
            var package = Array.FindIndex(parts, x => x.Equals("FDataPackage", StringComparison.OrdinalIgnoreCase));
            if (package >= 0 && package + 2 < parts.Length && !parts[package + 2].EndsWith(".fdata", StringComparison.OrdinalIgnoreCase))
            {
                if (package + 4 < parts.Length && !parts[package + 3].Equals("data", StringComparison.OrdinalIgnoreCase)) return parts[package + 3];
                return parts[package + 2];
            }
            return parts.FirstOrDefault() ?? "Root";
        }
    }
    public string SizeText => Size switch
    {
        >= 1_073_741_824 => (Size / 1_073_741_824d).ToString("0.00 GB", CultureInfo.InvariantCulture),
        >= 1_048_576 => (Size / 1_048_576d).ToString("0.00 MB", CultureInfo.InvariantCulture),
        >= 1024 => (Size / 1024d).ToString("0.0 KB", CultureInfo.InvariantCulture),
        _ => $"{Size} B"
    };

    private static string SanitizeFileName(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private string EnglishFileName()
    {
        var stem = string.IsNullOrWhiteSpace(OfficialName)
            ? (OwnerName is { Length: > 0 } owner ? $"{owner}_{KindToken()}_{HashId}" : HashId)
            : OfficialName!;
        stem = stem
            .Replace("模型", "Model", StringComparison.Ordinal)
            .Replace("动画", "Animation", StringComparison.Ordinal)
            .Replace("骨骼", "Skeleton", StringComparison.Ordinal)
            .Replace("贴图", "Texture", StringComparison.Ordinal)
            .Replace("材质", "Material", StringComparison.Ordinal)
            .Replace("碰撞", "Collision", StringComparison.Ordinal)
            .Replace(" · ", "_", StringComparison.Ordinal)
            .Replace(' ', '_');
        stem = string.Concat(stem.Select(c => c <= 127 && (char.IsLetterOrDigit(c) || c is '_' or '-') ? c : '_'));
        while (stem.Contains("__", StringComparison.Ordinal)) stem = stem.Replace("__", "_", StringComparison.Ordinal);
        stem = stem.Trim('_');
        if (stem.Length == 0) stem = $"resource_{HashId}";
        return stem + Path.GetExtension(FullPath).ToLowerInvariant();
    }

    private string KindToken() => Kind switch
    {
        AssetKind.Model => "Model", AssetKind.Animation => "Animation", AssetKind.Skeleton => "Skeleton",
        AssetKind.Texture => "Texture", AssetKind.Material => "Material", AssetKind.Collision => "Collision",
        AssetKind.Package => "Package", AssetKind.Movie => "Movie", _ => "Resource"
    };

    public void RestoreOfficialNameFromOrganizedPath()
    {
        var parts = RelativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[0].Equals("character", StringComparison.OrdinalIgnoreCase))
            OfficialName = OwnerName = parts[1];
        else if (parts.Length >= 3 && parts[0] is "item" or "stage" or "event" or "named")
            OfficialName = OwnerName = parts[1];
    }
}

public sealed record G1MInfo(
    string Version, string Platform, int ChunkCount, int MeshCount, int SubmeshCount,
    int VertexBufferCount, int IndexBufferCount, int MaterialCount,
    float MinX, float MinY, float MinZ, float MaxX, float MaxY, float MaxZ,
    IReadOnlyList<MeshPart> Parts, IReadOnlyList<SkeletonBone> Bones, string? Warning);

public sealed record MeshPart(
    float[] Positions,
    uint[] Indices,
    bool TriangleStrip,
    int[]? BoneIndices = null,
    float[]? BoneWeights = null,
    float[]? Normals = null,
    float[]? TextureCoordinates = null,
    int MaterialIndex = -1,
    int DiffuseTextureIndex = -1,
    int InfluencesPerVertex = 4);
public sealed record G1AInfo(string Version, float Duration, int TrackCount, int MaxBoneId, int AnimationType);
public sealed record SkeletonBone(int LocalId, int GlobalId, int ParentLocalId,
    float ScaleX, float ScaleY, float ScaleZ, float RotationX, float RotationY, float RotationZ, float RotationW,
    float PositionX, float PositionY, float PositionZ);
public sealed record AnimationChannel(float[][] Coefficients, float[] Times);
public sealed record AnimationTrack(int GlobalBoneId, int Opcode, AnimationChannel[] Channels,
    ushort[]? FrameTimings = null, ulong[][]? QuantizedData = null, float FrameRate = 30, int AnimationFrames = 0);
public sealed record G1AClip(G1AInfo Info, IReadOnlyList<AnimationTrack> Tracks);

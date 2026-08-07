using System.Numerics;

namespace DWOriginsAssetTool.Core;

/// <summary>
/// Resolves the bind-space transform used to merge an independently-authored
/// weapon G1M onto a character socket. Weapon vertices are stored in weapon
/// model space, so the weapon attachment bone's bind transform must be removed
/// before the animated character socket transform is applied.
/// </summary>
public static class ModelAttachmentComposer
{
    public sealed record Attachment(int TargetGlobalBoneId, int SourceLocalBoneId,
        int SourceGlobalBoneId, Matrix4x4 SourceBindInverse);

    public static Attachment Resolve(G1MInfo weapon, int targetGlobalBoneId)
    {
        if (weapon.Bones.Count == 0)
            return new(targetGlobalBoneId, -1, -1, Matrix4x4.Identity);

        var weighted = weapon.Parts
            .Where(part => part.BoneIndices is not null)
            .SelectMany(part => part.BoneIndices!)
            .Where(index => index >= 0 && index < weapon.Bones.Count)
            .Distinct()
            .ToArray();
        var sourceLocal = weighted.Length > 0 ? FindCommonAncestor(weapon.Bones, weighted)
            : weapon.Bones.Count > 1 ? 1 : 0;
        if (sourceLocal < 0 || sourceLocal >= weapon.Bones.Count) sourceLocal = 0;

        var bindWorld = BuildBindWorld(weapon.Bones);
        var inverse = Matrix4x4.Invert(bindWorld[sourceLocal], out var value)
            ? value : Matrix4x4.Identity;
        return new(targetGlobalBoneId, sourceLocal, weapon.Bones[sourceLocal].GlobalId, inverse);
    }

    public static Attachment Resolve(G1MInfo accessory, int sourceLocalBoneId, int targetGlobalBoneId)
    {
        if (accessory.Bones.Count == 0)
            return new(targetGlobalBoneId, -1, -1, Matrix4x4.Identity);
        sourceLocalBoneId = Math.Clamp(sourceLocalBoneId, 0, accessory.Bones.Count - 1);
        var bindWorld = BuildBindWorld(accessory.Bones);
        var inverse = Matrix4x4.Invert(bindWorld[sourceLocalBoneId], out var value)
            ? value : Matrix4x4.Identity;
        return new(targetGlobalBoneId, sourceLocalBoneId,
            accessory.Bones[sourceLocalBoneId].GlobalId, inverse);
    }

    public static Matrix4x4 Compose(Attachment attachment, Matrix4x4 targetSocketWorld) =>
        attachment.SourceBindInverse * targetSocketWorld;

    public static Matrix4x4 Compose(Attachment attachment, Matrix4x4 adjustment,
        Matrix4x4 targetSocketWorld) => attachment.SourceBindInverse * adjustment * targetSocketWorld;

    private static int FindCommonAncestor(IReadOnlyList<SkeletonBone> bones, IReadOnlyList<int> locals)
    {
        var common = Ancestors(bones, locals[0]).ToList();
        foreach (var local in locals.Skip(1))
        {
            var ancestors = Ancestors(bones, local).ToHashSet();
            common.RemoveAll(value => !ancestors.Contains(value));
        }
        return common.FirstOrDefault(-1);
    }

    private static IEnumerable<int> Ancestors(IReadOnlyList<SkeletonBone> bones, int local)
    {
        var visited = new HashSet<int>();
        while (local >= 0 && local < bones.Count && visited.Add(local))
        {
            yield return local;
            local = bones[local].ParentLocalId;
        }
    }

    private static Matrix4x4[] BuildBindWorld(IReadOnlyList<SkeletonBone> bones)
    {
        var result = new Matrix4x4[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            var local = Matrix4x4.CreateScale(bone.ScaleX, bone.ScaleY, bone.ScaleZ) *
                        Matrix4x4.CreateFromQuaternion(new Quaternion(bone.RotationX, bone.RotationY,
                            bone.RotationZ, bone.RotationW)) *
                        Matrix4x4.CreateTranslation(bone.PositionX, bone.PositionY, bone.PositionZ);
            result[i] = bone.ParentLocalId >= 0 && bone.ParentLocalId < i
                ? local * result[bone.ParentLocalId] : local;
        }
        return result;
    }
}

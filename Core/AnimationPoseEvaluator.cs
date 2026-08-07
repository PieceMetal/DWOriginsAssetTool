using System.Numerics;

namespace DWOriginsAssetTool.Core;

public sealed record EvaluatedAnimationPose(Matrix4x4[] WorldMatrices, Matrix4x4[] SkinMatrices,
    IReadOnlySet<int> AnimatedLocalBoneIds);

public static class AnimationPoseEvaluator
{
    public static EvaluatedAnimationPose Evaluate(G1MInfo model, G1AClip clip, float time)
    {
        var bones = model.Bones;
        var globalToLocal = bones.GroupBy(x => x.GlobalId)
            .ToDictionary(x => x.Key, x => x.First().LocalId);
        var tracks = clip.Tracks.Where(x => globalToLocal.ContainsKey(x.GlobalBoneId))
            .GroupBy(x => globalToLocal[x.GlobalBoneId])
            .ToDictionary(x => x.Key, x => x.ToArray());
        var world = new Matrix4x4[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            var scale = new Vector3(bone.ScaleX, bone.ScaleY, bone.ScaleZ);
            var rotation = new Quaternion(bone.RotationX, bone.RotationY, bone.RotationZ, bone.RotationW);
            var translation = new Vector3(bone.PositionX, bone.PositionY, bone.PositionZ);
            if (tracks.TryGetValue(bone.LocalId, out var boneTracks))
            {
                foreach (var track in boneTracks)
                {
                    var sampled = G1Parsers.SampleTrack(track, time);
                    switch (track.Opcode)
                    {
                        case 0: rotation = sampled.Rotation; break;
                        case 1: translation = sampled.Translation; break;
                        case 2: scale = sampled.Scale; break;
                        default:
                            if (track.Opcode is 4 or 6 or 0x66) translation = sampled.Translation;
                            if (track.Opcode is 2 or 4 or 6 or 8 or 0x66) rotation = sampled.Rotation;
                            if (track.Opcode is 6 or 8 or 0x66) scale = sampled.Scale;
                            break;
                    }
                }
            }
            var local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) *
                        Matrix4x4.CreateTranslation(translation);
            world[i] = bone.ParentLocalId >= 0 && bone.ParentLocalId < i ? local * world[bone.ParentLocalId] : local;
        }

        var bindWorld = BuildBindWorld(bones);
        var skin = new Matrix4x4[bones.Count];
        for (var i = 0; i < bones.Count; i++)
            skin[i] = Matrix4x4.Invert(bindWorld[i], out var inverse) ? inverse * world[i] : Matrix4x4.Identity;
        return new(world, skin, tracks.Keys.ToHashSet());
    }

    public static Vector3 SkinVertex(MeshPart part, int vertexIndex, IReadOnlyList<Matrix4x4> skin)
    {
        var source = new Vector3(part.Positions[vertexIndex * 3], part.Positions[vertexIndex * 3 + 1],
            part.Positions[vertexIndex * 3 + 2]);
        if (part.BoneIndices is null || part.BoneWeights is null) return source;
        var result = Vector3.Zero;
        var total = 0f;
        var influencesPerVertex = Math.Max(1, part.InfluencesPerVertex);
        var influenceBase = vertexIndex * influencesPerVertex;
        for (var k = 0; k < influencesPerVertex; k++)
        {
            if (influenceBase + k >= part.BoneIndices.Length || influenceBase + k >= part.BoneWeights.Length) break;
            var bone = part.BoneIndices[influenceBase + k];
            var weight = part.BoneWeights[influenceBase + k];
            if (bone < 0 || bone >= skin.Count || !float.IsFinite(weight) || weight <= 0) continue;
            result += Vector3.Transform(source, skin[bone]) * weight;
            total += weight;
        }
        return total > .0001f ? result / total : source;
    }

    private static Matrix4x4[] BuildBindWorld(IReadOnlyList<SkeletonBone> bones)
    {
        var result = new Matrix4x4[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var b = bones[i];
            var local = Matrix4x4.CreateScale(b.ScaleX, b.ScaleY, b.ScaleZ) *
                        Matrix4x4.CreateFromQuaternion(new Quaternion(b.RotationX, b.RotationY, b.RotationZ, b.RotationW)) *
                        Matrix4x4.CreateTranslation(b.PositionX, b.PositionY, b.PositionZ);
            result[i] = b.ParentLocalId >= 0 && b.ParentLocalId < i ? local * result[b.ParentLocalId] : local;
        }
        return result;
    }
}

using System.Numerics;
using System.Text;
using System.Text.Json;

namespace DWOriginsAssetTool.Core;

/// <summary>
/// Writes the decoded asset as glTF 2.0.  This is deliberately used as the
/// interchange format for the Blender bridge: Blender's glTF importer is
/// stable for skinned meshes and baked animation, while its ASCII FBX importer
/// is not suitable as a validation/conversion path.
/// </summary>
public static class GltfExporter
{
    public sealed record AccessoryExport(G1MInfo Model, ModelAttachmentComposer.Attachment Attachment,
        Matrix4x4 Adjustment, string Name);

    // G1M coordinates are centimeters (Hero0 is about 182 units tall), while
    // glTF/Blender scene units are meters.  Keep this conversion at the bridge
    // boundary so preview math and the game's source data remain unchanged.
    private const float GameCentimetersToMeters = 0.01f;
    private const int Float = 5126;
    private const int UnsignedShort = 5123;
    private const int UnsignedInt = 5125;

    public static void Export(G1MInfo model, G1AClip? clip, string destination, string name, bool includeGeometry = true,
        ActionCameraClip? camera = null, float cameraPositionScale = GameCentimetersToMeters,
        float cameraObjectScale = 1f, AccessoryExport? accessory = null)
    {
        var data = new BinaryBuffer();
        var bufferViews = new List<Dictionary<string, object?>>();
        var accessors = new List<Dictionary<string, object?>>();
        var nodes = new List<Dictionary<string, object?>>();
        var meshes = new List<Dictionary<string, object?>>();

        int AddView(byte[] bytes, int? target = null)
        {
            var offset = data.Append(bytes);
            var view = new Dictionary<string, object?> { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = bytes.Length };
            if (target is not null) view["target"] = target;
            bufferViews.Add(view); return bufferViews.Count - 1;
        }
        int AddAccessor(byte[] bytes, int componentType, int count, string type, int? target = null,
            float[]? minimum = null, float[]? maximum = null)
        {
            var accessor = new Dictionary<string, object?>
            {
                ["bufferView"] = AddView(bytes, target), ["componentType"] = componentType, ["count"] = count, ["type"] = type
            };
            if (minimum is not null) accessor["min"] = minimum;
            if (maximum is not null) accessor["max"] = maximum;
            accessors.Add(accessor); return accessors.Count - 1;
        }

        // Node 0 is a neutral scene root.  Bone nodes retain their game-local TRS;
        // glTF's column-major transforms represent the same hierarchy as the
        // row-major matrices used by the decoder when matrices are written below.
        nodes.Add(new Dictionary<string, object?> { ["name"] = Escape(name) });
        var boneNodeIds = new int[model.Bones.Count];
        for (var i = 0; i < model.Bones.Count; i++)
        {
            var bone = model.Bones[i];
            boneNodeIds[i] = nodes.Count;
            nodes.Add(new Dictionary<string, object?>
            {
                ["name"] = $"bone_{bone.GlobalId:D4}",
                ["translation"] = new[] { Finite(bone.PositionX * GameCentimetersToMeters), Finite(bone.PositionY * GameCentimetersToMeters), Finite(bone.PositionZ * GameCentimetersToMeters) },
                ["rotation"] = QuaternionArray(new Quaternion(bone.RotationX, bone.RotationY, bone.RotationZ, bone.RotationW)),
                ["scale"] = new[] { Finite(bone.ScaleX, 1), Finite(bone.ScaleY, 1), Finite(bone.ScaleZ, 1) }
            });
        }
        var rootChildren = new List<int>();
        for (var i = 0; i < model.Bones.Count; i++)
        {
            var bone = model.Bones[i];
            if (bone.ParentLocalId >= 0 && bone.ParentLocalId < model.Bones.Count)
            {
                var parent = nodes[boneNodeIds[bone.ParentLocalId]];
                if (!parent.TryGetValue("children", out var raw)) parent["children"] = raw = new List<int>();
                ((List<int>)raw!).Add(boneNodeIds[i]);
            }
            else rootChildren.Add(boneNodeIds[i]);
        }

        var skins = new List<Dictionary<string, object?>>();
        var cameras = new List<Dictionary<string, object?>>();
        var cameraNodeId = -1;
        if (model.Bones.Count > 0)
        {
            var bindWorld = BuildBindWorld(model.Bones);
            var matrices = new float[model.Bones.Count * 16];
            for (var i = 0; i < bindWorld.Length; i++)
            {
                var inverse = Matrix4x4.Invert(bindWorld[i], out var result) ? result : Matrix4x4.Identity;
                WriteGltfMatrix(matrices, i * 16, inverse);
            }
            var inverseAccessor = AddAccessor(ToBytes(matrices), Float, model.Bones.Count, "MAT4");
            skins.Add(new Dictionary<string, object?>
            {
                ["name"] = "DWOriginsSkeleton", ["inverseBindMatrices"] = inverseAccessor,
                ["joints"] = boneNodeIds, ["skeleton"] = rootChildren.FirstOrDefault()
            });
        }

        foreach (var (part, partIndex) in (includeGeometry ? model.Parts : Array.Empty<MeshPart>()).Select((value, index) => (value, index)))
        {
            var vertices = part.Positions.Length / 3;
            if (vertices == 0) continue;
            var attributes = new Dictionary<string, int>
            {
                ["POSITION"] = AddAccessor(ToBytes(ScalePositions(part.Positions)), Float, vertices, "VEC3", 34962,
                    new[] { Min(part.Positions, 0) * GameCentimetersToMeters, Min(part.Positions, 1) * GameCentimetersToMeters, Min(part.Positions, 2) * GameCentimetersToMeters },
                    new[] { Max(part.Positions, 0) * GameCentimetersToMeters, Max(part.Positions, 1) * GameCentimetersToMeters, Max(part.Positions, 2) * GameCentimetersToMeters })
            };
            if (part.Normals is { Length: > 0 } normals)
                attributes["NORMAL"] = AddAccessor(ToBytes(FitFloatAttribute(normals, vertices, 3, new[] { 0f, 1f, 0f })), Float, vertices, "VEC3", 34962);
            if (part.TextureCoordinates is { Length: > 0 } uv)
                attributes["TEXCOORD_0"] = AddAccessor(ToBytes(FitFloatAttribute(uv, vertices, 2, new[] { 0f, 0f })), Float, vertices, "VEC2", 34962);
            if (model.Bones.Count > 0 && part.BoneIndices is not null && part.BoneWeights is not null)
            {
                var (joints, weights) = BuildJointWeights(part, vertices, model.Bones.Count);
                attributes["JOINTS_0"] = AddAccessor(ToBytes(joints), UnsignedShort, vertices, "VEC4", 34962);
                attributes["WEIGHTS_0"] = AddAccessor(ToBytes(weights), Float, vertices, "VEC4", 34962);
            }
            var triangles = Triangles(part).SelectMany(x => new[] { (uint)x.A, (uint)x.B, (uint)x.C }).ToArray();
            if (triangles.Length == 0) continue;
            var primitive = new Dictionary<string, object?>
            {
                ["attributes"] = attributes,
                ["indices"] = AddAccessor(ToBytes(triangles), UnsignedInt, triangles.Length, "SCALAR", 34963),
                ["mode"] = 4
            };
            meshes.Add(new Dictionary<string, object?> { ["name"] = $"mesh_{partIndex:D3}", ["primitives"] = new[] { primitive } });
            var meshNode = new Dictionary<string, object?> { ["name"] = $"mesh_{partIndex:D3}", ["mesh"] = meshes.Count - 1 };
            if (skins.Count > 0 && part.BoneIndices is not null && part.BoneWeights is not null) meshNode["skin"] = 0;
            nodes.Add(meshNode); rootChildren.Add(nodes.Count - 1);
        }

        // A weapon/accessory remains a separate rigid mesh object. Its source
        // grip bind transform and the editor adjustment are baked into its
        // object-space vertices; the mesh nodes are then parented to the chosen
        // character bone. This is the same transform chain used by the viewport
        // and does not merge the accessory into the character skin.
        if (includeGeometry && accessory is not null)
        {
            var targetBone = model.Bones.FirstOrDefault(bone =>
                bone.GlobalId == accessory.Attachment.TargetGlobalBoneId);
            if (targetBone is null)
                throw new InvalidOperationException($"Accessory target bone {accessory.Attachment.TargetGlobalBoneId} is missing");
            var targetNode = nodes[boneNodeIds[targetBone.LocalId]];
            if (!targetNode.TryGetValue("children", out var targetChildrenRaw))
                targetNode["children"] = targetChildrenRaw = new List<int>();
            var targetChildren = (List<int>)targetChildrenRaw!;
            var localAttachment = accessory.Attachment.SourceBindInverse * accessory.Adjustment;
            var accessoryName = Escape(accessory.Name);
            foreach (var (part, partIndex) in accessory.Model.Parts.Select((value, index) => (value, index)))
            {
                var vertices = part.Positions.Length / 3;
                if (vertices == 0) continue;
                var positions = new float[vertices * 3];
                for (var vertex = 0; vertex < vertices; vertex++)
                {
                    var sourcePosition = new Vector3(part.Positions[vertex * 3], part.Positions[vertex * 3 + 1], part.Positions[vertex * 3 + 2]);
                    var transformed = Vector3.Transform(sourcePosition, localAttachment) * GameCentimetersToMeters;
                    WriteVector(positions, vertex * 3, transformed);
                }
                var attributes = new Dictionary<string, int>
                {
                    ["POSITION"] = AddAccessor(ToBytes(positions), Float, vertices, "VEC3", 34962,
                        new[] { Min(positions, 0), Min(positions, 1), Min(positions, 2) },
                        new[] { Max(positions, 0), Max(positions, 1), Max(positions, 2) })
                };
                if (part.Normals is { Length: > 0 })
                {
                    var normals = new float[vertices * 3];
                    for (var vertex = 0; vertex < vertices; vertex++)
                    {
                        var sourceNormal = new Vector3(part.Normals[vertex * 3], part.Normals[vertex * 3 + 1], part.Normals[vertex * 3 + 2]);
                        var transformed = Vector3.TransformNormal(sourceNormal, localAttachment);
                        if (transformed.LengthSquared() > 1e-12f) transformed = Vector3.Normalize(transformed);
                        WriteVector(normals, vertex * 3, transformed);
                    }
                    attributes["NORMAL"] = AddAccessor(ToBytes(normals), Float, vertices, "VEC3", 34962);
                }
                if (part.TextureCoordinates is { Length: > 0 } uv)
                    attributes["TEXCOORD_0"] = AddAccessor(ToBytes(FitFloatAttribute(uv, vertices, 2, new[] { 0f, 0f })), Float, vertices, "VEC2", 34962);
                var triangles = Triangles(part).SelectMany(x => new[] { (uint)x.A, (uint)x.B, (uint)x.C }).ToArray();
                if (triangles.Length == 0) continue;
                var primitive = new Dictionary<string, object?>
                {
                    ["attributes"] = attributes,
                    ["indices"] = AddAccessor(ToBytes(triangles), UnsignedInt, triangles.Length, "SCALAR", 34963),
                    ["mode"] = 4
                };
                meshes.Add(new Dictionary<string, object?>
                {
                    ["name"] = $"accessory_{accessoryName}_{partIndex:D3}", ["primitives"] = new[] { primitive }
                });
                nodes.Add(new Dictionary<string, object?>
                {
                    ["name"] = $"accessory_{accessoryName}_{partIndex:D3}", ["mesh"] = meshes.Count - 1
                });
                targetChildren.Add(nodes.Count - 1);
            }
        }
        nodes[0]["children"] = rootChildren;

        if (camera is not null)
        {
            var frame = ActionCameraCatalog.Sample(camera, 0);
            var cameraRotation = CameraRotation(frame.Forward, frame.Up);
            cameraNodeId = nodes.Count;
            nodes.Add(new Dictionary<string, object?>
            {
                ["name"] = "ActionCamera",
                ["translation"] = new[] { frame.Position.X * cameraPositionScale, frame.Position.Y * cameraPositionScale, frame.Position.Z * cameraPositionScale },
                ["rotation"] = QuaternionArray(cameraRotation),
                // Camera scale does not affect projection.  UE's editor uses it
                // only to size the CineCameraActor visualization.
                ["scale"] = new[] { cameraObjectScale, cameraObjectScale, cameraObjectScale },
                ["camera"] = 0
            });
            // Keep the camera outside the armature's scene root.  Blender turns
            // that root into the armature object; parenting a camera to it makes
            // FBX apply the armature's transform a second time in UE Sequencer.
            cameras.Add(new Dictionary<string, object?>
            {
                ["name"] = "ActionCamera",
                ["type"] = "perspective",
                ["perspective"] = new Dictionary<string, object?>
                {
                    ["yfov"] = frame.FieldOfView * MathF.PI / 180f, ["znear"] = 0.01f, ["zfar"] = 10000f
                }
            });
        }

        Dictionary<string, object?>? animation = null;
        if (clip is not null && model.Bones.Count > 0)
            animation = BuildAnimation(model, clip, data, bufferViews, accessors);
        Dictionary<string, object?>? cameraAnimation = null;
        if (camera is not null)
            cameraAnimation = BuildCameraAnimation(camera, cameraNodeId, data, bufferViews, accessors, cameraPositionScale);
        var sceneNodes = cameraNodeId >= 0 ? new[] { 0, cameraNodeId } : new[] { 0 };
        var root = new Dictionary<string, object?>
        {
            ["asset"] = new Dictionary<string, object?> { ["version"] = "2.0", ["generator"] = "DWOriginsAssetTool Blender bridge" },
            ["scene"] = 0, ["scenes"] = new[] { new Dictionary<string, object?> { ["name"] = Escape(name), ["nodes"] = sceneNodes } },
            ["nodes"] = nodes, ["meshes"] = meshes, ["skins"] = skins, ["cameras"] = cameras,
            ["buffers"] = new[] { new Dictionary<string, object?> { ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(data.ToArray()), ["byteLength"] = data.Length } },
            ["bufferViews"] = bufferViews, ["accessors"] = accessors
        };
        var animations = new List<Dictionary<string, object?>>();
        if (animation is not null) animations.Add(animation);
        if (cameraAnimation is not null) animations.Add(cameraAnimation);
        if (animations.Count > 0) root["animations"] = animations;
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
        File.WriteAllText(destination, JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = false }), new UTF8Encoding(false));
    }

    private static Dictionary<string, object?> BuildCameraAnimation(ActionCameraClip camera, int nodeId, BinaryBuffer data,
        List<Dictionary<string, object?>> views, List<Dictionary<string, object?>> accessors, float positionScale)
    {
        int AddAccessor(byte[] bytes, int count, string type)
        {
            var offset = data.Append(bytes);
            views.Add(new Dictionary<string, object?> { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = bytes.Length });
            accessors.Add(new Dictionary<string, object?> { ["bufferView"] = views.Count - 1, ["componentType"] = Float, ["count"] = count, ["type"] = type });
            return accessors.Count - 1;
        }
        var times = SampleTimes(camera.Clip.Info.Duration).ToArray();
        var positions = new float[times.Length * 3]; var rotations = new float[times.Length * 4];
        for (var i = 0; i < times.Length; i++)
        {
            var frame = ActionCameraCatalog.Sample(camera, times[i]);
            WriteVector(positions, i * 3, frame.Position * positionScale);
            WriteQuaternion(rotations, i * 4, CameraRotation(frame.Forward, frame.Up));
        }
        var input = AddAccessor(ToBytes(times), times.Length, "SCALAR");
        var samplers = new List<Dictionary<string, object?>>(); var channels = new List<Dictionary<string, object?>>();
        AddChannel("translation", AddAccessor(ToBytes(positions), times.Length, "VEC3"));
        AddChannel("rotation", AddAccessor(ToBytes(rotations), times.Length, "VEC4"));
        void AddChannel(string path, int output)
        {
            samplers.Add(new Dictionary<string, object?> { ["input"] = input, ["output"] = output, ["interpolation"] = "LINEAR" });
            channels.Add(new Dictionary<string, object?> { ["sampler"] = samplers.Count - 1, ["target"] = new Dictionary<string, object?> { ["node"] = nodeId, ["path"] = path } });
        }
        return new Dictionary<string, object?> { ["name"] = "ActionCamera", ["samplers"] = samplers, ["channels"] = channels };
    }

    private static Dictionary<string, object?> BuildAnimation(G1MInfo model, G1AClip clip, BinaryBuffer data,
        List<Dictionary<string, object?>> views, List<Dictionary<string, object?>> accessors)
    {
        int AddAccessor(byte[] bytes, int count, string type)
        {
            var offset = data.Append(bytes);
            views.Add(new Dictionary<string, object?> { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = bytes.Length });
            accessors.Add(new Dictionary<string, object?> { ["bufferView"] = views.Count - 1, ["componentType"] = Float, ["count"] = count, ["type"] = type });
            return accessors.Count - 1;
        }
        var times = SampleTimes(clip.Info.Duration).ToArray();
        var timeAccessor = AddAccessor(ToBytes(times), times.Length, "SCALAR");
        var samplers = new List<Dictionary<string, object?>>();
        var channels = new List<Dictionary<string, object?>>();
        var globalToLocal = model.Bones.ToDictionary(x => x.GlobalId, x => x.LocalId);
        foreach (var localId in clip.Tracks.Where(x => globalToLocal.ContainsKey(x.GlobalBoneId)).Select(x => globalToLocal[x.GlobalBoneId]).Distinct().Order())
        {
            var translation = new float[times.Length * 3]; var rotation = new float[times.Length * 4]; var scale = new float[times.Length * 3];
            for (var i = 0; i < times.Length; i++)
            {
                var (s, r, t) = SampleLocal(model, clip, localId, times[i]);
                WriteVector(translation, i * 3, t * GameCentimetersToMeters); WriteQuaternion(rotation, i * 4, r); WriteVector(scale, i * 3, s);
            }
            AddChannel("translation", AddAccessor(ToBytes(translation), times.Length, "VEC3"));
            AddChannel("rotation", AddAccessor(ToBytes(rotation), times.Length, "VEC4"));
            AddChannel("scale", AddAccessor(ToBytes(scale), times.Length, "VEC3"));
            void AddChannel(string path, int output)
            {
                samplers.Add(new Dictionary<string, object?> { ["input"] = timeAccessor, ["output"] = output, ["interpolation"] = "LINEAR" });
                channels.Add(new Dictionary<string, object?> { ["sampler"] = samplers.Count - 1, ["target"] = new Dictionary<string, object?> { ["node"] = localId + 1, ["path"] = path } });
            }
        }
        return new Dictionary<string, object?> { ["name"] = "CharacterAction", ["samplers"] = samplers, ["channels"] = channels };
    }

    private static (ushort[] Joints, float[] Weights) BuildJointWeights(MeshPart part, int vertices, int boneCount)
    {
        var joints = new ushort[vertices * 4]; var weights = new float[vertices * 4]; var influences = Math.Max(1, part.InfluencesPerVertex);
        for (var vertex = 0; vertex < vertices; vertex++)
        {
            var values = new List<(int Bone, float Weight)>();
            for (var k = 0; k < influences; k++)
            {
                var index = vertex * influences + k;
                if (index >= part.BoneIndices!.Length || index >= part.BoneWeights!.Length) break;
                if (part.BoneIndices[index] >= 0 && part.BoneIndices[index] < boneCount && float.IsFinite(part.BoneWeights[index]) && part.BoneWeights[index] > 0)
                    values.Add((part.BoneIndices[index], part.BoneWeights[index]));
            }
            var selected = values.OrderByDescending(x => x.Weight).Take(4).ToArray(); var total = selected.Sum(x => x.Weight);
            for (var k = 0; k < selected.Length; k++) { joints[vertex * 4 + k] = (ushort)selected[k].Bone; weights[vertex * 4 + k] = selected[k].Weight / total; }
        }
        return (joints, weights);
    }

    private static (Vector3 Scale, Quaternion Rotation, Vector3 Translation) SampleLocal(G1MInfo model, G1AClip clip, int localId, float time)
    {
        var bone = model.Bones[localId]; var scale = new Vector3(bone.ScaleX, bone.ScaleY, bone.ScaleZ);
        var rotation = new Quaternion(bone.RotationX, bone.RotationY, bone.RotationZ, bone.RotationW); var translation = new Vector3(bone.PositionX, bone.PositionY, bone.PositionZ);
        foreach (var track in clip.Tracks.Where(x => x.GlobalBoneId == bone.GlobalId))
        {
            var sampled = G1Parsers.SampleTrack(track, time);
            switch (track.Opcode)
            {
                case 0: rotation = sampled.Rotation; break; case 1: translation = sampled.Translation; break; case 2: scale = sampled.Scale; break;
                default:
                    if (track.Opcode is 4 or 6 or 0x66) translation = sampled.Translation;
                    if (track.Opcode is 2 or 4 or 6 or 8 or 0x66) rotation = sampled.Rotation;
                    if (track.Opcode is 6 or 8 or 0x66) scale = sampled.Scale;
                    break;
            }
        }
        return (scale, Quaternion.Normalize(rotation), translation);
    }

    private static IEnumerable<float> SampleTimes(float duration)
    {
        var frames = Math.Max(1, (int)Math.Ceiling(duration * 60));
        for (var frame = 0; frame <= frames; frame++) yield return Math.Min(duration, frame / 60f);
    }
    private static Matrix4x4[] BuildBindWorld(IReadOnlyList<SkeletonBone> bones)
    {
        var result = new Matrix4x4[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var b = bones[i]; var local = Matrix4x4.CreateScale(b.ScaleX, b.ScaleY, b.ScaleZ) * Matrix4x4.CreateFromQuaternion(new Quaternion(b.RotationX, b.RotationY, b.RotationZ, b.RotationW)) * Matrix4x4.CreateTranslation(b.PositionX * GameCentimetersToMeters, b.PositionY * GameCentimetersToMeters, b.PositionZ * GameCentimetersToMeters);
            result[i] = b.ParentLocalId >= 0 && b.ParentLocalId < i ? local * result[b.ParentLocalId] : local;
        }
        return result;
    }
    private static IEnumerable<(int A, int B, int C)> Triangles(MeshPart part)
    {
        var count = part.Positions.Length / 3;
        if (!part.TriangleStrip) for (var i = 0; i + 2 < part.Indices.Length; i += 3) { var a = (int)part.Indices[i]; var b = (int)part.Indices[i + 1]; var c = (int)part.Indices[i + 2]; if (Valid(a, b, c, count)) yield return (a, b, c); }
        else for (var i = 0; i + 2 < part.Indices.Length; i++) { var a = (int)part.Indices[i]; var b = (int)part.Indices[i + 1]; var c = (int)part.Indices[i + 2]; if ((i & 1) != 0) (a, b) = (b, a); if (Valid(a, b, c, count)) yield return (a, b, c); }
    }
    private static bool Valid(int a, int b, int c, int count) => a >= 0 && b >= 0 && c >= 0 && a < count && b < count && a != b && b != c && a != c;
    private static float[] ScalePositions(float[] source)
    {
        var result = new float[source.Length];
        for (var i = 0; i < source.Length; i++) result[i] = Finite(source[i]) * GameCentimetersToMeters;
        return result;
    }
    private static float[] FitFloatAttribute(float[] source, int count, int components, float[] fallback)
    { var result = new float[count * components]; for (var i = 0; i < result.Length; i++) result[i] = i < source.Length && float.IsFinite(source[i]) ? source[i] : fallback[i % components]; return result; }
    private static float Min(float[] values, int component) => Enumerable.Range(0, Math.Max(0, (values.Length - component + 2) / 3)).Select(i => values[component + i * 3]).Where(float.IsFinite).DefaultIfEmpty(0).Min();
    private static float Max(float[] values, int component) => Enumerable.Range(0, Math.Max(0, (values.Length - component + 2) / 3)).Select(i => values[component + i * 3]).Where(float.IsFinite).DefaultIfEmpty(0).Max();
    private static float[] QuaternionArray(Quaternion value) { value = Quaternion.Normalize(value); return new[] { Finite(value.X), Finite(value.Y), Finite(value.Z), Finite(value.W, 1) }; }
    private static Quaternion CameraRotation(Vector3 forward, Vector3 up)
    {
        if (forward.LengthSquared() < 1e-8f) forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward); up = Vector3.Normalize(up.LengthSquared() < 1e-8f ? Vector3.UnitY : up);
        var world = Matrix4x4.CreateWorld(Vector3.Zero, forward, up);
        return Matrix4x4.Decompose(world, out _, out var rotation, out _) ? Quaternion.Normalize(rotation) : Quaternion.Identity;
    }
    private static float Finite(float value, float fallback = 0) => float.IsFinite(value) ? value : fallback;
    private static void WriteVector(float[] target, int offset, Vector3 value) { target[offset] = Finite(value.X); target[offset + 1] = Finite(value.Y); target[offset + 2] = Finite(value.Z); }
    private static void WriteQuaternion(float[] target, int offset, Quaternion value) { value = Quaternion.Normalize(value); target[offset] = Finite(value.X); target[offset + 1] = Finite(value.Y); target[offset + 2] = Finite(value.Z); target[offset + 3] = Finite(value.W, 1); }
    private static void WriteGltfMatrix(float[] target, int offset, Matrix4x4 value)
    {
        var values = new[] { value.M11, value.M12, value.M13, value.M14, value.M21, value.M22, value.M23, value.M24,
            value.M31, value.M32, value.M33, value.M34, value.M41, value.M42, value.M43, value.M44 };
        Array.Copy(values, 0, target, offset, values.Length);
    }
    private static byte[] ToBytes(float[] values) { var bytes = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return bytes; }
    private static byte[] ToBytes(uint[] values) { var bytes = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return bytes; }
    private static byte[] ToBytes(ushort[] values) { var bytes = new byte[values.Length * 2]; Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length); return bytes; }
    private static string Escape(string value) => string.IsNullOrWhiteSpace(value) ? "DWOrigins" : value.Replace('"', '_');
    private sealed class BinaryBuffer { private readonly MemoryStream _stream = new(); public int Length => (int)_stream.Length; public int Append(byte[] bytes) { while ((_stream.Length & 3) != 0) _stream.WriteByte(0); var offset = (int)_stream.Length; _stream.Write(bytes); return offset; } public byte[] ToArray() => _stream.ToArray(); }
}

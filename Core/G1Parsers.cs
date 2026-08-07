using System.Buffers.Binary;
using System.Text;
using System.Numerics;

namespace DWOriginsAssetTool.Core;

public static class G1Parsers
{
    private sealed record VertexBuffer(int Stride, int Count, int DataOffset);
    private sealed record Attribute(int BufferId, int Offset, int Type, int Semantic, int Layer);
    private sealed record AttributeSet(List<Attribute> Attributes);
    private sealed record IndexBuffer(int Count, int Width, int DataOffset);
    private sealed record MaterialInfo(int DiffuseTextureIndex, int DiffuseUvLayer);
    private sealed record Submesh(int AttributeSet, int Palette, int Material, int IndexBuffer, int Primitive, int VertexOffset, int VertexCount, int IndexOffset, int IndexCount);

    public static G1AInfo ReadAnimation(string path)
    {
        var b = File.ReadAllBytes(path);
        Require(b.Length >= 32 && Encoding.ASCII.GetString(b, 0, 4) is "_A1G" or "_A2G", "不是有效的 G1A/G2A 文件");
        var version = Encoding.ASCII.GetString(b, 4, 4);
        if (Encoding.ASCII.GetString(b, 0, 4) == "_A2G")
        {
            var frameRate = F32(b, 12); var packed = U32(b, 16); var frameCount = checked((int)(packed & 0x3ffff));
            var boneInfoBytes = checked((int)((packed >> 18) & 0x3ffc)); var count = boneInfoBytes / 4;
            var checkpoint = version == "0500" ? 32 : 28;
            var maxBone = ReadG2ABoneIds(b, version, count, checkpoint).DefaultIfEmpty(0).Max();
            return new(version, frameRate > 0 ? frameCount / frameRate : 0, count, maxBone, 0x200);
        }
        Require(b.Length >= 52, "G1A 头不完整");
        return new G1AInfo(version, F32(b, 16), U16(b, 48), U16(b, 50), U16(b, 12));
    }

    public static G1AClip ReadAnimationClip(string path)
    {
        var b = File.ReadAllBytes(path); var info = ReadAnimation(path);
        if (Encoding.ASCII.GetString(b, 0, 4) == "_A2G") return ReadG2AClip(b, info);
        Require(string.CompareOrdinal(info.Version, "0400") > 0, $"G1A {info.Version} 使用旧布局，当前关键帧解码器支持 0041+；元数据仍可查看");
        var checkpoint = 52; var tracks = new List<AnimationTrack>();
        for (var i = 0; i < info.TrackCount; i++)
        {
            var entry = checkpoint + i * 8; Require(entry + 8 <= b.Length, "动画轨道表越界");
            var boneId = CheckedInt(U32(b, entry), int.MaxValue);
            var splineUnits = CheckedInt(U32(b, entry + 4), b.Length / 16 + 1);
            var spline = checkpoint - 4 + splineUnits * 16;
            Require(spline + 4 <= b.Length, "动画曲线偏移越界");
            var opcode = CheckedInt(U32(b, spline), 0x100);
            var componentCount = opcode switch { 1 => 2, 2 => 4, 4 => 7, 6 => 10, 8 => 7, 0x66 => 9, _ => 0 };
            if (componentCount == 0) continue;
            var channels = new AnimationChannel[componentCount];
            for (var c = 0; c < componentCount; c++)
            {
                var desc = spline + 4 + c * 8; Require(desc + 8 <= b.Length, "动画曲线描述越界");
                var keyCount = CheckedInt(U32(b, desc), 2_000_000);
                var dataUnits = CheckedInt(U32(b, desc + 4), b.Length / 16 + 1);
                var dataOffset = spline + dataUnits * 16;
                Require(dataOffset + (long)keyCount * 20 <= b.Length, "动画关键帧数据越界");
                var coefficients = new float[keyCount][];
                for (var k = 0; k < keyCount; k++)
                    coefficients[k] = [F32(b, dataOffset + k * 16), F32(b, dataOffset + k * 16 + 4), F32(b, dataOffset + k * 16 + 8), F32(b, dataOffset + k * 16 + 12)];
                var timesOffset = dataOffset + keyCount * 16; var times = new float[keyCount];
                for (var k = 0; k < keyCount; k++) times[k] = F32(b, timesOffset + k * 4);
                channels[c] = new(coefficients, times);
            }
            tracks.Add(new(boneId, opcode, channels));
        }
        return new(info, tracks);
    }

    public static (Vector3 Scale, Quaternion Rotation, Vector3 Translation) SampleTrack(AnimationTrack track, float time)
    {
        if (track.QuantizedData is not null && track.FrameTimings is not null)
        {
            var vector = SampleG2A(track, time);
            return track.Opcode switch
            {
                0 => (Vector3.One, ExponentialQuaternion(vector), Vector3.Zero),
                1 => (Vector3.One, Quaternion.Identity, vector),
                2 => (vector, Quaternion.Identity, Vector3.Zero),
                _ => (Vector3.One, Quaternion.Identity, Vector3.Zero)
            };
        }
        var scale = Vector3.One; var rotation = Quaternion.Identity; var translation = Vector3.Zero;
        var scaleIndex = track.Opcode is 6 or 8 or 0x66 ? 0 : -1;
        var rotationIndex = track.Opcode switch { 2 or 4 => 0, 6 or 8 => 3, _ => -1 };
        var translationIndex = track.Opcode switch { 4 => 4, 6 => 7, 0x66 => 6, _ => -1 };
        if (scaleIndex >= 0) scale = new(SampleChannel(track.Channels[scaleIndex], time), SampleChannel(track.Channels[scaleIndex + 1], time), SampleChannel(track.Channels[scaleIndex + 2], time));
            if (rotationIndex >= 0)
            {
                rotation = new(SampleChannel(track.Channels[rotationIndex], time), SampleChannel(track.Channels[rotationIndex + 1], time),
                    SampleChannel(track.Channels[rotationIndex + 2], time), SampleChannel(track.Channels[rotationIndex + 3], time));
            if (rotation.LengthSquared() > 1e-8f) rotation = Quaternion.Normalize(Quaternion.Conjugate(rotation)); else rotation = Quaternion.Identity;
        }
        else if (track.Opcode == 0x66)
        {
            var vector = new Vector3(SampleChannel(track.Channels[3], time), SampleChannel(track.Channels[4], time), SampleChannel(track.Channels[5], time));
            var angle = vector.Length();
            if (angle > 0.000011920929f)
            {
                var factor = MathF.Sin(angle * .5f) / angle;
                rotation = Quaternion.Conjugate(Quaternion.Normalize(new Quaternion(vector * factor, MathF.Cos(angle * .5f))));
            }
            else rotation = Quaternion.Conjugate(Quaternion.Normalize(new Quaternion(vector * .5f, 1)));
        }
        if (translationIndex >= 0) translation = new(SampleChannel(track.Channels[translationIndex], time), SampleChannel(track.Channels[translationIndex + 1], time), SampleChannel(track.Channels[translationIndex + 2], time));
        return (scale, rotation, translation);
    }

    public static (Vector3 Position, Vector3 Target, Vector3 Lens) SampleCameraTrack(AnimationTrack track, float time)
    {
        Require(track.Opcode == 0x66 && track.Channels.Length >= 9,
            "动作镜头需要包含位置、目标点和镜头参数的 0x66 九通道轨道");
        Vector3 Group(int start) => new(SampleChannel(track.Channels[start], time),
            SampleChannel(track.Channels[start + 1], time), SampleChannel(track.Channels[start + 2], time));
        return (Group(0), Group(3), Group(6));
    }

    private static G1AClip ReadG2AClip(byte[] b, G1AInfo info)
    {
        var version = info.Version; var frameRate = F32(b, 12); var packedHeader = U32(b, 16);
        var frames = checked((int)(packedHeader & 0x3ffff)); var boneInfoSize = checked((int)((packedHeader >> 18) & 0x3ffc));
        var timingSize = CheckedInt(U32(b, 20), b.Length); var count = boneInfoSize / 4;
        // ResourceSectionHeader (12) + AnimationV2Header (20). Version 0500+
        // inserts one additional 32-bit field.
        var checkpoint = version == "0500" ? 36 : 32;
        Require(checkpoint + boneInfoSize + timingSize <= b.Length, "G2A 分区大小越界");
        var tracks = new List<AnimationTrack>(); var lastId = 0; var globalOffset = 0;
        for (var i = 0; i < count; i++)
        {
            var packed = U32(b, checkpoint + i * 4); var splineCount = checked((int)(packed & 0xf));
            var isV5 = version == "0500"; var boneId = checked((int)(isV5 ? (packed >> 4) & 0xff : (packed >> 4) & 0x3ff));
            var timingOffset = checked((int)(isV5 ? packed >> 12 : packed >> 14));
            if (boneId < lastId) globalOffset++; lastId = boneId; boneId += globalOffset * (isV5 ? 256 : 1024);
            var cursor = (checkpoint + boneInfoSize + timingOffset) & ~3;
            for (var spline = 0; spline < splineCount; spline++)
            {
                Require(cursor + 8 <= b.Length, "G2A 曲线描述越界");
                var opcode = U16(b, cursor); var keyCount = U16(b, cursor + 2); var firstData = CheckedInt(U32(b, cursor + 4), b.Length / 32 + 1); cursor += 8;
                Require(cursor + keyCount * 2 <= b.Length, "G2A 时间表越界");
                var timings = new ushort[keyCount]; for (var k = 0; k < keyCount; k++) timings[k] = U16(b, cursor + k * 2);
                cursor = (cursor + keyCount * 2 + 3) & ~3;
                var dataOffset = checkpoint + boneInfoSize + timingSize + firstData * 32;
                Require(dataOffset + (long)keyCount * 32 <= b.Length, "G2A 量化曲线越界");
                var quantized = new ulong[keyCount][];
                for (var k = 0; k < keyCount; k++) quantized[k] = [U64(b, dataOffset + k * 32), U64(b, dataOffset + k * 32 + 8), U64(b, dataOffset + k * 32 + 16), U64(b, dataOffset + k * 32 + 24)];
                if (opcode <= 2) tracks.Add(new(boneId, opcode, [], timings, quantized, frameRate, frames));
            }
        }
        return new(info, tracks);
    }

    private static IEnumerable<int> ReadG2ABoneIds(byte[] b, string version, int count, int checkpoint)
    {
        var last = 0; var global = 0; var isV5 = version == "0500";
        for (var i = 0; i < count && checkpoint + i * 4 + 4 <= b.Length; i++)
        {
            var packed = U32(b, checkpoint + i * 4); var id = checked((int)(isV5 ? (packed >> 4) & 0xff : (packed >> 4) & 0x3ff));
            if (id < last) global++; last = id; yield return id + global * (isV5 ? 256 : 1024);
        }
    }

    private static Vector3 SampleG2A(AnimationTrack track, float time)
    {
        var timings = track.FrameTimings!; var data = track.QuantizedData!; if (data.Length == 0) return Vector3.Zero;
        var frame = Math.Clamp(time * track.FrameRate, 0, track.AnimationFrames);
        if (data.Length == 1) return DecodeQuantized(data[0], 0, 1);
        var index = 0; while (index + 1 < timings.Length && frame >= timings[index + 1]) index++;
        var start = timings[Math.Min(index, timings.Length - 1)];
        var end = index + 1 < timings.Length ? timings[index + 1] : track.AnimationFrames;
        return DecodeQuantized(data[Math.Min(index, data.Length - 1)], frame - start, Math.Max(1, end - start));
    }

    private static Vector3 DecodeQuantized(ulong[] rows, float current, float total)
    {
        // The quantized payload is four little-endian 64-bit words. Each word
        // packs signed 20-bit XYZ coefficients plus a shared exponent.
        var r0 = rows[0];
        var r1 = rows[1];
        var r2 = rows[2];
        var r3 = rows[3];
        var t = current / total; var t2 = t * t; var t3 = t2 * t;
        static float Scale(ulong row) => BitConverter.Int32BitsToSingle(unchecked((int)(((row >> 0x25) & 0x07800000) + 0x32000000)));
        var x = Scale(r0); var y = Scale(r1); var z = Scale(r2); var w = Scale(r3);
        static float C0(ulong row) => unchecked((int)((row >> 28) & 0xfffff000));
        static float C1(ulong row) => unchecked((int)((row >> 8) & 0xfffff000));
        static float C2(ulong row) => unchecked((int)(row << 12));
        return new(
            C0(r3) * w * t3 + C0(r0) * x + C0(r1) * y * t + C0(r2) * z * t2,
            C1(r3) * w * t3 + C1(r0) * x + C1(r1) * y * t + C1(r2) * z * t2,
            C2(r3) * w * t3 + C2(r0) * x + C2(r1) * y * t + C2(r2) * z * t2);
    }

    private static Quaternion ExponentialQuaternion(Vector3 vector)
    {
        var angle = vector.Length(); Quaternion q;
        if (angle > 0.000011920929f) { var f = MathF.Sin(angle * .5f) / angle; q = new(vector * f, MathF.Cos(angle * .5f)); }
        else q = new(vector * .5f, 1);
        // G2A exponential-map coefficients are already in the runtime/local
        // quaternion convention. Conjugating them here mirrors every animated
        // rotation and folds the skinned hierarchy into itself.
        return Quaternion.Normalize(q);
    }

    private static float SampleChannel(AnimationChannel channel, float time)
    {
        if (channel.Times.Length == 0) return 0;
        var index = Array.FindIndex(channel.Times, t => time < t); if (index < 0) index = channel.Times.Length - 1;
        var t1 = channel.Times[index]; var t0 = index == 0 ? 0 : channel.Times[index - 1];
        var ratio = Math.Abs(t1 - t0) < 1e-8f ? 0 : (time - t0) / (t1 - t0);
        var c = channel.Coefficients[index]; return c[0] * ratio * ratio * ratio + c[1] * ratio * ratio + c[2] * ratio + c[3];
    }

    public static G1MInfo ReadModel(string path, bool geometry = true)
    {
        var b = File.ReadAllBytes(path);
        Require(b.Length >= 24 && Encoding.ASCII.GetString(b, 0, 4) == "_M1G", "不是有效的 G1M 文件");
        var version = Encoding.ASCII.GetString(b, 4, 4);
        var first = CheckedInt(U32(b, 12), b.Length);
        var chunkCount = CheckedInt(U32(b, 20), 4096);
        var chunkOffset = first;
        int g1mg = -1, g1mgSize = 0;
        var skeletonChunks = new List<int>();
        for (var i = 0; i < chunkCount; i++)
        {
            Require(chunkOffset + 12 <= b.Length, "G1M 分块表越界");
            var size = CheckedInt(U32(b, chunkOffset + 8), b.Length - chunkOffset);
            Require(size >= 12, "G1M 分块大小无效");
            if (Encoding.ASCII.GetString(b, chunkOffset, 4) == "GM1G") { g1mg = chunkOffset; g1mgSize = size; }
            if (Encoding.ASCII.GetString(b, chunkOffset, 4) == "SM1G") skeletonChunks.Add(chunkOffset);
            chunkOffset += size;
        }
        Require(g1mg >= 0 && g1mg + 48 <= b.Length, "未找到 G1MG 几何分块");
        var platform = Encoding.ASCII.GetString(b, g1mg + 12, 4).TrimEnd('\0');
        var minX = F32(b, g1mg + 20); var minY = F32(b, g1mg + 24); var minZ = F32(b, g1mg + 28);
        var maxX = F32(b, g1mg + 32); var maxY = F32(b, g1mg + 36); var maxZ = F32(b, g1mg + 40);
        var sectionCount = CheckedInt(U32(b, g1mg + 44), 128);
        var sections = new Dictionary<uint, (int Offset, int Count)>();
        var cursor = g1mg + 48;
        for (var i = 0; i < sectionCount; i++)
        {
            Require(cursor + 12 <= g1mg + g1mgSize && cursor + 12 <= b.Length, "G1MG 子分块表越界");
            var magic = U32(b, cursor); var size = CheckedInt(U32(b, cursor + 4), g1mgSize);
            var count = CheckedInt(U32(b, cursor + 8), 5_000_000);
            Require(size >= 12 && cursor + size <= b.Length, "G1MG 子分块大小无效");
            sections[magic] = (cursor + 12, count);
            cursor += size;
        }

        var materialCount = sections.GetValueOrDefault(0x10002u).Count;
        var vbCount = sections.GetValueOrDefault(0x10004u).Count;
        var ibCount = sections.GetValueOrDefault(0x10007u).Count;
        var smCount = sections.GetValueOrDefault(0x10008u).Count;
        var meshCount = sections.GetValueOrDefault(0x10009u).Count;
        var bones = skeletonChunks.Count > 0 ? ParseSkeleton(b, skeletonChunks[0]) : [];
        if (!geometry)
            return new(version, platform, chunkCount, meshCount, smCount, vbCount, ibCount, materialCount,
                minX, minY, minZ, maxX, maxY, maxZ, [], bones, null);

        try
        {
            var vbs = ParseVertexBuffers(b, sections[0x10004u], version);
            var attrs = ParseAttributes(b, sections[0x10005u]);
            var materials = sections.TryGetValue(0x10002u, out var materialSection) ? ParseMaterials(b, materialSection) : [];
            var palettes = sections.TryGetValue(0x10006u, out var paletteSection) ? ParsePalettes(b, paletteSection, bones) : [];
            var ibs = ParseIndexBuffers(b, sections[0x10007u], version);
            var submeshes = ParseSubmeshes(b, sections[0x10008u]);
            var clothSubmeshes = sections.TryGetValue(0x10009u, out var lodSection)
                ? ParseClothSubmeshes(b, lodSection, version) : [];
            var parts = new List<MeshPart>();
            foreach (var indexedSubmesh in submeshes.Take(5000).Select((value, index) => (value, index)))
            {
                var sm = indexedSubmesh.value;
                // Cloth submeshes store simulation-space vertices and require the
                // NUNO/NUNV runtime solver before they can be displayed. Rendering
                // their raw buffers produces the fan-shaped debris at world zero.
                if (clothSubmeshes.Contains(indexedSubmesh.index)) continue;
                if ((uint)sm.AttributeSet >= attrs.Count || (uint)sm.IndexBuffer >= ibs.Count) continue;
                var pos = attrs[sm.AttributeSet].Attributes.FirstOrDefault(a => a.Semantic == 0);
                if (pos is null || (uint)pos.BufferId >= vbs.Count) continue;
                var vb = vbs[pos.BufferId]; var ib = ibs[sm.IndexBuffer];
                var vertexCount = Math.Min(sm.VertexCount, vb.Count - sm.VertexOffset);
                if (vertexCount <= 0 || sm.IndexCount <= 0) continue;
                var positions = new float[vertexCount * 3];
                for (var v = 0; v < vertexCount; v++)
                {
                    var o = vb.DataOffset + (sm.VertexOffset + v) * vb.Stride + pos.Offset;
                    if (!TryReadVector3(b, o, pos.Type, out var x, out var y, out var z)) break;
                    positions[v * 3] = x; positions[v * 3 + 1] = y; positions[v * 3 + 2] = z;
                }
                float[]? normals = null;
                var normal = attrs[sm.AttributeSet].Attributes.FirstOrDefault(a => a.Semantic == 3 && a.Layer == 0);
                if (normal is not null && (uint)normal.BufferId < vbs.Count)
                {
                    normals = new float[vertexCount * 3]; var normalBuffer = vbs[normal.BufferId];
                    for (var v = 0; v < vertexCount; v++)
                    {
                        var o = normalBuffer.DataOffset + (sm.VertexOffset + v) * normalBuffer.Stride + normal.Offset;
                        if (!TryReadVector3(b, o, normal.Type, out var x, out var y, out var z)) { normals = null; break; }
                        var length = MathF.Sqrt(x * x + y * y + z * z);
                        if (length > 1e-8f) { x /= length; y /= length; z /= length; }
                        normals[v * 3] = x; normals[v * 3 + 1] = y; normals[v * 3 + 2] = z;
                    }
                }
                float[]? texCoords = null;
                var uvLayer = (uint)sm.Material < materials.Count ? materials[sm.Material].DiffuseUvLayer : 0;
                var uv = attrs[sm.AttributeSet].Attributes.FirstOrDefault(a => a.Semantic == 5 && a.Layer == uvLayer)
                    ?? attrs[sm.AttributeSet].Attributes.FirstOrDefault(a => a.Semantic == 5 && a.Layer == 0);
                if (uv is not null && (uint)uv.BufferId < vbs.Count)
                {
                    texCoords = new float[vertexCount * 2]; var uvBuffer = vbs[uv.BufferId];
                    for (var v = 0; v < vertexCount; v++)
                    {
                        var o = uvBuffer.DataOffset + (sm.VertexOffset + v) * uvBuffer.Stride + uv.Offset;
                        if (!TryReadVector2(b, o, uv.Type, out var x, out var y)) { texCoords = null; break; }
                        texCoords[v * 2] = x; texCoords[v * 2 + 1] = y;
                    }
                }
                var indices = new uint[sm.IndexCount];
                for (var j = 0; j < indices.Length; j++)
                {
                    var o = ib.DataOffset + (sm.IndexOffset + j) * ib.Width;
                    if (o + ib.Width > b.Length) { Array.Resize(ref indices, j); break; }
                    indices[j] = ib.Width switch { 1 => b[o], 2 => U16(b, o), _ => U32(b, o) };
                }
                NormalizeSubmeshIndices(indices, sm.VertexOffset, vertexCount, ib.Width);
                int[]? boneIndices = null; float[]? boneWeights = null;
                var joint0 = attrs[sm.AttributeSet].Attributes.FirstOrDefault(a => a.Semantic == 2 && a.Layer == 0);
                var joint1 = attrs[sm.AttributeSet].Attributes.FirstOrDefault(a => a.Semantic == 2 && a.Layer == 1);
                var weight0 = attrs[sm.AttributeSet].Attributes.FirstOrDefault(a => a.Semantic == 1 && a.Layer == 0);
                var weight1 = attrs[sm.AttributeSet].Attributes.FirstOrDefault(a => a.Semantic == 1 && a.Layer == 1);
                var influencesPerVertex = joint1 is not null ? 8 : 4;
                if (joint0 is not null && (uint)joint0.BufferId < vbs.Count && (uint)sm.Palette < palettes.Count)
                {
                    boneIndices = new int[vertexCount * influencesPerVertex];
                    boneWeights = new float[vertexCount * influencesPerVertex];
                    for (var v = 0; v < vertexCount; v++)
                    {
                        var vertex = sm.VertexOffset + v;
                        for (var influence = 0; influence < influencesPerVertex; influence++)
                        {
                            var layer = influence / 4;
                            var component = influence % 4;
                            var joint = layer == 0 ? joint0 : joint1;
                            var weight = layer == 0 ? weight0 : weight1;
                            var destination = v * influencesPerVertex + influence;
                            if (joint is null || (uint)joint.BufferId >= vbs.Count)
                            {
                                boneIndices[destination] = -1;
                                continue;
                            }
                            var jointBuffer = vbs[joint.BufferId];
                            var jointOffset = jointBuffer.DataOffset + vertex * jointBuffer.Stride + joint.Offset;
                            // G1MG stores a byte/word offset into the 3-DWORD joint palette
                            // entry, not the ordinal itself. Project-G1M/Noesis represents
                            // this by placing each mapped joint at boneMap[ordinal * 3].
                            // Using the raw 0,3,6,... value as an ordinal maps vertices to
                            // unrelated bones and makes an otherwise correct animation fold.
                            var rawPaletteIndex = ReadIntegerComponent(b, jointOffset, joint.Type, component);
                            var paletteIndex = rawPaletteIndex >= 0 && rawPaletteIndex % 3 == 0
                                ? rawPaletteIndex / 3
                                : -1;
                            boneIndices[destination] = paletteIndex >= 0 && paletteIndex < palettes[sm.Palette].Length
                                ? palettes[sm.Palette][paletteIndex]
                                : -1;
                            if (weight is not null && (uint)weight.BufferId < vbs.Count)
                            {
                                var weightBuffer = vbs[weight.BufferId];
                                var weightOffset = weightBuffer.DataOffset + vertex * weightBuffer.Stride + weight.Offset;
                                boneWeights[destination] = ReadWeightComponent(b, weightOffset, weight.Type, component);
                            }
                            else
                            {
                                // Noesis gives an index-only primary layer full weight on
                                // its first joint. A secondary index-only layer contributes
                                // no weight, otherwise it would displace every vertex.
                                boneWeights[destination] = layer == 0 && component == 0 ? 1 : 0;
                            }
                        }
                    }
                }
                var diffuse = (uint)sm.Material < materials.Count ? materials[sm.Material].DiffuseTextureIndex : -1;
                parts.Add(new MeshPart(positions, indices, sm.Primitive == 4, boneIndices, boneWeights,
                    normals, texCoords, sm.Material, diffuse, influencesPerVertex));
            }
            return new(version, platform, chunkCount, meshCount, smCount, vbCount, ibCount, materialCount,
                minX, minY, minZ, maxX, maxY, maxZ, parts, bones, parts.Count == 0 ? "已读取结构，但未找到可显示的位置缓冲。" : null);
        }
        catch (Exception ex)
        {
            return new(version, platform, chunkCount, meshCount, smCount, vbCount, ibCount, materialCount,
                minX, minY, minZ, maxX, maxY, maxZ, [], bones, "几何预览解析失败：" + ex.Message);
        }
    }

    private static void NormalizeSubmeshIndices(uint[] indices, int vertexOffset, int vertexCount, int indexWidth)
    {
        if (indices.Length == 0 || vertexCount <= 0) return;
        var restart = indexWidth switch { 1 => byte.MaxValue, 2 => ushort.MaxValue, _ => uint.MaxValue };
        var localValid = 0;
        var globalValid = 0;
        var relevant = 0;
        foreach (var index in indices)
        {
            if (index == restart) continue;
            relevant++;
            if (index < vertexCount) localValid++;
            if (index >= vertexOffset && index < (long)vertexOffset + vertexCount) globalValid++;
        }

        // An index buffer uses one basis for the whole submesh.  Converting each
        // value independently corrupts local buffers whenever their values happen
        // to overlap [vertexOffset, vertexOffset + vertexCount), creating the large
        // triangle fans visible around the model origin.
        var usesGlobalIndices = relevant > 0 && globalValid == relevant && localValid != relevant;
        if (!usesGlobalIndices && globalValid > localValid) usesGlobalIndices = true;
        if (!usesGlobalIndices || vertexOffset == 0) return;

        for (var i = 0; i < indices.Length; i++)
        {
            if (indices[i] == restart) continue;
            if (indices[i] >= vertexOffset && indices[i] < (long)vertexOffset + vertexCount)
                indices[i] -= (uint)vertexOffset;
            else
                indices[i] = uint.MaxValue;
        }
    }

    private static List<SkeletonBone> ParseSkeleton(byte[] b, int chunk)
    {
        Require(chunk + 28 <= b.Length, "G1MS 头越界");
        var version = Encoding.ASCII.GetString(b, chunk + 4, 4);
        var jointInfoOffset = CheckedInt(U32(b, chunk + 12), b.Length - chunk);
        var jointCount = U16(b, chunk + 20); var mapCount = U16(b, chunk + 22);
        var localToGlobal = new Dictionary<int, int>();
        if (string.CompareOrdinal(version, "2300") >= 0)
        {
            for (var global = 0; global < mapCount; global++)
            {
                var local = U16(b, chunk + 28 + global * 2); if (local != 0xffff) localToGlobal[local] = global;
            }
        }
        else for (var i = 0; i < jointCount; i++) localToGlobal[i] = i;
        var result = new List<SkeletonBone>(jointCount); var joints = chunk + jointInfoOffset;
        Require(joints + (long)jointCount * 48 <= b.Length, "G1MS 骨骼数据越界");
        for (var i = 0; i < jointCount; i++)
        {
            var o = joints + i * 48; var parentRaw = U32(b, o + 12);
            var parent = parentRaw == uint.MaxValue || (parentRaw & 0x80000000) != 0 ? -1 : checked((int)parentRaw);
            // Project-G1M/Noesis inverts the G1MS matrix because Noesis uses the
            // opposite vector/matrix convention. System.Numerics uses row-vector
            // transforms, so feeding the normalized source quaternion already
            // produces that inverse matrix. Conjugating here inverts it twice and
            // rotates the bind skeleton onto the wrong axes.
            var qx = F32(b, o + 16); var qy = F32(b, o + 20); var qz = F32(b, o + 24); var qw = F32(b, o + 28);
            var qLength = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
            if (qLength > 1e-8f) { qx /= qLength; qy /= qLength; qz /= qLength; qw /= qLength; }
            else { qx = qy = qz = 0; qw = 1; }
            result.Add(new(i, localToGlobal.GetValueOrDefault(i, i), parent,
                F32(b, o), F32(b, o + 4), F32(b, o + 8), qx, qy, qz, qw,
                F32(b, o + 32), F32(b, o + 36), F32(b, o + 40)));
        }
        return result;
    }

    private static List<VertexBuffer> ParseVertexBuffers(byte[] b, (int Offset, int Count) section, string version)
    {
        var result = new List<VertexBuffer>(); var o = section.Offset; var total = 0; var sharedOffset = 0;
        while (total < section.Count)
        {
            Require(o + 16 <= b.Length, "顶点缓冲越界");
            var stride = CheckedInt(U32(b, o + 4), 4096); var count = CheckedInt(U32(b, o + 8), 100_000_000);
            var header = string.CompareOrdinal(version, "0400") > 0 ? 16 : 12;
            var data = o + header; var bytes = checked(stride * count);
            Require(data + bytes <= b.Length, "顶点数据越界");
            result.Add(new(stride, count, data)); o = data + bytes; total++;

            // G1MG 0045 can store one physical vertex blob followed by virtual
            // buffer descriptors. Their first dword is the 0x80000000 sentinel.
            while (total < section.Count && o + 16 <= b.Length && U32(b, o) == 0x80000000)
            {
                var virtualStride = CheckedInt(U32(b, o + 4), 4096);
                var virtualCount = CheckedInt(U32(b, o + 8), 100_000_000);
                Require(result[0].DataOffset + sharedOffset + (long)virtualStride * virtualCount <= b.Length, "共享顶点缓冲越界");
                result.Add(new(virtualStride, virtualCount, result[0].DataOffset + sharedOffset));
                sharedOffset += checked(virtualStride * virtualCount); o += 16; total++;
            }
        }
        return result;
    }

    private static List<AttributeSet> ParseAttributes(byte[] b, (int Offset, int Count) section)
    {
        var result = new List<AttributeSet>(); var o = section.Offset;
        for (var i = 0; i < section.Count; i++)
        {
            var refs = CheckedInt(U32(b, o), 1024); o += 4; var map = new int[refs];
            for (var j = 0; j < refs; j++) { map[j] = CheckedInt(U32(b, o), 100000); o += 4; }
            var count = CheckedInt(U32(b, o), 1024); o += 4; var list = new List<Attribute>();
            for (var j = 0; j < count; j++)
            {
                var local = U16(b, o); var offset = U16(b, o + 2); var type = b[o + 4]; var semantic = b[o + 6]; var layer = b[o + 7]; o += 8;
                if (local < map.Length) list.Add(new(map[local], offset, type, semantic, layer));
            }
            result.Add(new(list));
        }
        return result;
    }

    private static List<MaterialInfo> ParseMaterials(byte[] b, (int Offset, int Count) section)
    {
        var result = new List<MaterialInfo>(section.Count); var o = section.Offset;
        for (var i = 0; i < section.Count; i++)
        {
            Require(o + 16 <= b.Length, "材质数据越界");
            var textureCount = CheckedInt(U32(b, o + 4), 4096); o += 16;
            var diffuse = -1; var layer = 0;
            for (var j = 0; j < textureCount; j++, o += 12)
            {
                Require(o + 12 <= b.Length, "材质贴图表越界");
                var index = U16(b, o); var uvLayer = U16(b, o + 2); var type = U16(b, o + 4);
                if (type == 1 && diffuse < 0) { diffuse = index; layer = uvLayer; }
            }
            result.Add(new(diffuse, layer));
        }
        return result;
    }

    private static List<IndexBuffer> ParseIndexBuffers(byte[] b, (int Offset, int Count) section, string version)
    {
        var result = new List<IndexBuffer>(); var o = section.Offset;
        for (var i = 0; i < section.Count; i++)
        {
            var count = CheckedInt(U32(b, o), 100_000_000); var bits = U32(b, o + 4);
            var width = bits switch { 8 => 1, 16 => 2, 32 => 4, _ => throw new InvalidDataException($"未知索引位宽 {bits}") };
            var header = string.CompareOrdinal(version, "0400") > 0 ? 12 : 8; var data = o + header;
            Require(data + (long)count * width <= b.Length, "索引数据越界");
            result.Add(new(count, width, data));
            o = data + count * width;
            o = (o + 3) & ~3; // individual index buffers are DWORD-aligned
        }
        return result;
    }

    private static List<Submesh> ParseSubmeshes(byte[] b, (int Offset, int Count) section)
    {
        var result = new List<Submesh>(); var o = section.Offset;
        for (var i = 0; i < section.Count; i++, o += 56)
        {
            Require(o + 56 <= b.Length, "子网格数据越界");
            result.Add(new(CheckedInt(U32(b, o + 4), 1_000_000), CheckedInt(U32(b, o + 8), 1_000_000), CheckedInt(U32(b, o + 24), 1_000_000), CheckedInt(U32(b, o + 28), 1_000_000),
                CheckedInt(U32(b, o + 36), 100), CheckedInt(U32(b, o + 40), 100_000_000), CheckedInt(U32(b, o + 44), 100_000_000),
                CheckedInt(U32(b, o + 48), 100_000_000), CheckedInt(U32(b, o + 52), 100_000_000)));
        }
        return result;
    }

    private static HashSet<int> ParseClothSubmeshes(byte[] b, (int Offset, int Count) section, string version)
    {
        var result = new HashSet<int>();
        var o = section.Offset;
        var extendedHeader = string.CompareOrdinal(version, "0400") > 0;
        for (var container = 0; container < section.Count; container++)
        {
            Require(o + (extendedHeader ? 36 : 20) <= b.Length, "G1MG LOD 数据越界");
            o += 12; // unknown LOD header values
            var meshCount1 = CheckedInt(U32(b, o), 1_000_000); o += 4;
            var meshCount2 = CheckedInt(U32(b, o), 1_000_000); o += 4;
            if (extendedHeader) o += 16;
            var lodCount = checked(meshCount1 + meshCount2);
            for (var lod = 0; lod < lodCount; lod++)
            {
                Require(o + 28 <= b.Length, "G1MG LOD 网格数据越界");
                o += 16; // name
                var clothId = U16(b, o); o += 4; // cloth id + reserved
                o += 4; // NUNO/NUNV id
                var indexCount = CheckedInt(U32(b, o), 5_000_000); o += 4;
                Require(o + (long)Math.Max(1, indexCount) * 4 <= b.Length, "G1MG LOD 索引越界");
                if (indexCount == 0) { o += 4; continue; }
                for (var i = 0; i < indexCount; i++, o += 4)
                {
                    var submeshIndex = CheckedInt(U32(b, o), 5_000_000);
                    if (clothId is 1 or 2) result.Add(submeshIndex);
                }
            }
        }
        return result;
    }

    private static List<int[]> ParsePalettes(byte[] b, (int Offset, int Count) section, IReadOnlyList<SkeletonBone> bones)
    {
        var result = new List<int[]>(); var o = section.Offset; var globalToLocal = bones.ToDictionary(x => x.GlobalId, x => x.LocalId);
        for (var i = 0; i < section.Count; i++)
        {
            var count = CheckedInt(U32(b, o), 100_000); o += 4; Require(o + (long)count * 12 <= b.Length, "骨骼调色板越界");
            var map = new int[count];
            for (var j = 0; j < count; j++)
            {
                var jointIndex = U32(b, o + j * 12 + 8); var local = checked((int)(jointIndex & 0x7fffffff));
                var global = bones.FirstOrDefault(x => x.LocalId == local)?.GlobalId ?? local;
                map[j] = globalToLocal.GetValueOrDefault(global, -1);
            }
            o += count * 12; result.Add(map);
        }
        return result;
    }

    private static int ReadIntegerComponent(byte[] b, int offset, int type, int component) => type switch
    {
        5 => offset + component < b.Length ? b[offset + component] : -1,
        7 => offset + component * 2 + 2 <= b.Length ? U16(b, offset + component * 2) : -1,
        9 => offset + component * 4 + 4 <= b.Length ? checked((int)U32(b, offset + component * 4)) : -1,
        _ => -1
    };

    private static float ReadWeightComponent(byte[] b, int offset, int type, int component) => type switch
    {
        3 => F32(b, offset + component * 4),
        11 => (float)BitConverter.UInt16BitsToHalf(U16(b, offset + component * 2)),
        13 => b[offset + component] / 255f,
        _ => component == 0 ? 1 : 0
    };

    private static bool TryReadVector3(byte[] b, int o, int type, out float x, out float y, out float z)
    {
        x = y = z = 0;
        if (type is 2 or 3 && o + 12 <= b.Length) { x = F32(b, o); y = F32(b, o + 4); z = F32(b, o + 8); return true; }
        if (type == 11 && o + 6 <= b.Length) { x = (float)BitConverter.UInt16BitsToHalf(U16(b, o)); y = (float)BitConverter.UInt16BitsToHalf(U16(b, o + 2)); z = (float)BitConverter.UInt16BitsToHalf(U16(b, o + 4)); return true; }
        return false;
    }

    private static bool TryReadVector2(byte[] b, int o, int type, out float x, out float y)
    {
        x = y = 0;
        if (type is 1 or 3 && o + 8 <= b.Length) { x = F32(b, o); y = F32(b, o + 4); return true; }
        if (type is 10 or 11 && o + 4 <= b.Length)
        {
            x = (float)BitConverter.UInt16BitsToHalf(U16(b, o));
            y = (float)BitConverter.UInt16BitsToHalf(U16(b, o + 2)); return true;
        }
        return false;
    }

    private static ushort U16(byte[] b, int o) { Require(o >= 0 && o + 2 <= b.Length, "读取越界"); return BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2)); }
    private static uint U32(byte[] b, int o) { Require(o >= 0 && o + 4 <= b.Length, "读取越界"); return BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4)); }
    private static ulong U64(byte[] b, int o) { Require(o >= 0 && o + 8 <= b.Length, "读取越界"); return BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(o, 8)); }
    private static float F32(byte[] b, int o) => BitConverter.Int32BitsToSingle(unchecked((int)U32(b, o)));
    private static int CheckedInt(uint value, int max) { if (value > max || value > int.MaxValue) throw new InvalidDataException($"数值 {value} 超出合理范围"); return (int)value; }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}

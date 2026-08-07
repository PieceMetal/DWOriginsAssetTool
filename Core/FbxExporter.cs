using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace DWOriginsAssetTool.Core;

/// <summary>
/// ASCII FBX 7.4 writer for the G1M/G1A data already decoded by this tool.
/// It deliberately bakes animation at the game's 60 Hz timeline so every
/// importer receives the same evaluated pose as the viewport.
/// </summary>
public static class FbxExporter
{
    private const long FbxTicksPerSecond = 46_186_158_000L;

    /// <summary>Exports a bind-pose skeletal mesh for UE and 3ds Max.</summary>
    public static void ExportModel(G1MInfo model, string destination, string name) =>
        Export(destination, name, model, null, null, includeGeometry: true);

    /// <summary>
    /// Exports an animation-only FBX.  This is the UE-friendly file to import against the
    /// skeletal mesh exported by <see cref="ExportModel"/>.  It intentionally contains no
    /// duplicate mesh.
    /// </summary>
    public static void ExportCharacterAnimation(G1MInfo model, G1AClip clip, string destination, string name) =>
        Export(destination, name, model, clip, null, includeGeometry: false);

    /// <summary>
    /// Exports the currently selected character mesh and animation together.  It is useful
    /// for direct inspection in 3ds Max; UE can also import it as a skeletal mesh with the
    /// embedded animation enabled.
    /// </summary>
    public static void ExportPreview(G1MInfo model, G1AClip clip, string destination, string name) =>
        Export(destination, name, model, clip, null, includeGeometry: true);

    /// <summary>
    /// Legacy camera-only ASCII export.  The application uses the Blender bridge
    /// for camera export because UE Sequencer requires a real FBX Camera node.
    /// </summary>
    public static void ExportActionCamera(ActionCameraClip camera, string destination, string name) =>
        Export(destination, name, null, null, camera, includeGeometry: false);

    public static string ExportActionCameraReport(ActionCameraClip camera, string fbxDestination, double aspectRatio = 1.5)
    {
        var directory = Path.GetDirectoryName(fbxDestination) ?? ".";
        var reportBase = Path.Combine(directory, Path.GetFileNameWithoutExtension(fbxDestination) + "_report");
        var jsonPath = reportBase + ".json";
        var reportPath = reportBase + ".html";
        var duration = camera.Clip.Info.Duration;
        var frameCount = Math.Max(1, (int)Math.Ceiling(duration * 60));
        var frames = Enumerable.Range(0, frameCount + 1).Select(frameNumber =>
        {
            var time = Math.Min(duration, frameNumber / 60f);
            var raw = G1Parsers.SampleCameraTrack(camera.TransformTrack, time);
            var sampled = ActionCameraCatalog.Sample(camera, time);
            var verticalFov = 2f * MathF.Atan(MathF.Abs(raw.Lens.Y)) * 180f / MathF.PI;
            return new CameraReportFrame(frameNumber, time, raw.Position, raw.Target, raw.Lens,
                sampled.Forward, sampled.Up, sampled.FieldOfView, verticalFov);
        }).ToArray();
        var cameraPathLength = PathLength(frames.Select(x => x.Position));
        var targetPathLength = PathLength(frames.Select(x => x.Target));
        var maxCameraSpeed = MaxSpeed(frames.Select(x => x.Position));
        var maxTargetSpeed = MaxSpeed(frames.Select(x => x.Target));
        var report = new
        {
            format = "DWOrigins camera design report",
            sourceAnimationId = camera.Binding.AnimationId,
            cameraAnimationId = camera.Binding.CameraAnimationId,
            sourcePath = camera.Binding.CameraPath,
            durationSeconds = duration,
            sampleRate = 60,
            frameCount = frames.Length,
            previewAspectRatio = aspectRatio,
            filmback = new { sensorWidthMm = 24.0 * aspectRatio, sensorHeightMm = 24.0 },
            coordinateSystem = "DWOrigins source coordinates (unchanged from game data)",
            summary = new
            {
                cameraPathLength,
                targetPathLength,
                maxCameraSpeedPerSecond = maxCameraSpeed,
                maxTargetSpeedPerSecond = maxTargetSpeed,
                cameraToTargetDistance = new { min = frames.Min(x => x.Distance), max = frames.Max(x => x.Distance) },
                verticalFieldOfViewDegrees = new { min = frames.Min(x => x.FieldOfViewDegrees), max = frames.Max(x => x.FieldOfViewDegrees) },
                horizontalFieldOfViewDegrees = new
                {
                    min = frames.Min(x => HorizontalFov(x.FieldOfViewDegrees, aspectRatio)),
                    max = frames.Max(x => HorizontalFov(x.FieldOfViewDegrees, aspectRatio))
                }
            },
            frames = frames.Select(x => new
            {
                x.Frame,
                x.TimeSeconds,
                position = Components(x.Position),
                target = Components(x.Target),
                lens = Components(x.Lens),
                forward = Components(x.Forward),
                up = Components(x.Up),
                verticalFieldOfViewDegrees = x.FieldOfViewDegrees,
                horizontalFieldOfViewDegrees = HorizontalFov(x.FieldOfViewDegrees, aspectRatio),
                x.Distance,
                x.YawDegrees,
                x.PitchDegrees
            }).ToArray()
        };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }), new UTF8Encoding(false));
        File.WriteAllText(reportPath, BuildCameraReportHtml(camera, frames, cameraPathLength, targetPathLength,
            maxCameraSpeed, maxTargetSpeed, Path.GetFileName(jsonPath)), new UTF8Encoding(false));
        return reportPath;
    }

    private sealed record CameraReportFrame(int Frame, float TimeSeconds, Vector3 Position, Vector3 Target,
        Vector3 Lens, Vector3 Forward, Vector3 Up, float FieldOfViewDegrees, float RawVerticalFovDegrees)
    {
        public float Distance => Vector3.Distance(Position, Target);
        public float YawDegrees => MathF.Atan2(Forward.X, Forward.Z) * 180f / MathF.PI;
        public float PitchDegrees => MathF.Asin(Math.Clamp(Forward.Y, -1f, 1f)) * 180f / MathF.PI;
    }

    private static string BuildCameraReportHtml(ActionCameraClip camera, IReadOnlyList<CameraReportFrame> frames,
        float cameraPathLength, float targetPathLength, float maxCameraSpeed, float maxTargetSpeed, string jsonFileName)
    {
        var summary = $"""
<section class=\"cards\">
  <div><b>镜头时长</b><span>{F(camera.Clip.Info.Duration)} 秒 / {frames.Count} 帧</span></div>
  <div><b>镜头移动距离</b><span>{F(cameraPathLength)}</span></div>
  <div><b>目标移动距离</b><span>{F(targetPathLength)}</span></div>
  <div><b>最大镜头速度</b><span>{F(maxCameraSpeed)} / 秒</span></div>
  <div><b>最大目标速度</b><span>{F(maxTargetSpeed)} / 秒</span></div>
  <div><b>景别距离范围</b><span>{F(frames.Min(x => x.Distance))} – {F(frames.Max(x => x.Distance))}</span></div>
  <div><b>FOV 范围</b><span>{F(frames.Min(x => x.FieldOfViewDegrees))}° – {F(frames.Max(x => x.FieldOfViewDegrees))}°</span></div>
 </section>
""";
        var rows = new StringBuilder();
        CameraReportFrame? previous = null;
        foreach (var frame in frames.Where(x => x.Frame % 3 == 0 || x.Frame == frames.Count - 1))
        {
            var cameraSpeed = previous is null ? 0 : Vector3.Distance(previous.Position, frame.Position) / (frame.TimeSeconds - previous.TimeSeconds);
            var targetSpeed = previous is null ? 0 : Vector3.Distance(previous.Target, frame.Target) / (frame.TimeSeconds - previous.TimeSeconds);
            previous = frame;
            rows.Append($"<tr><td>{frame.Frame}</td><td>{F(frame.TimeSeconds)}</td><td>{V(frame.Position)}</td><td>{V(frame.Target)}</td><td>{F(frame.Distance)}</td><td>{F(frame.FieldOfViewDegrees)}°</td><td>{F(frame.YawDegrees)}° / {F(frame.PitchDegrees)}°</td><td>{F(cameraSpeed)}</td><td>{F(targetSpeed)}</td><td>{V(frame.Lens)}</td></tr>");
        }
        return BuildCameraReportHtmlCore(camera, jsonFileName, summary, rows); /*
<!doctype html>
<html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>镜头设计报告</title><style>
body{{margin:28px;background:#0c1118;color:#dce5ef;font:14px/1.5 \"Segoe UI\",sans-serif}}h1{{margin:0 0 4px;color:#f1b73f}}h2{{margin-top:28px}}.muted{{color:#8da0b6}}.cards{{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:10px;margin:18px 0}}.cards div{{padding:12px;border:1px solid #2d3a4b;background:#131c27;border-radius:7px}}.cards b,.cards span{{display:block}}.cards b{{color:#91cfff;font-size:12px}}table{{border-collapse:collapse;width:100%;font-family:Consolas,monospace;font-size:12px}}th{{position:sticky;top:0;background:#233044;color:#f4c45b}}td,th{{border:1px solid #2b394a;padding:6px;text-align:right;white-space:nowrap}}td:nth-child(3),td:nth-child(4),td:last-child{{text-align:left}}tr:nth-child(even){{background:#111a24}}</style></head><body>
<h1>动作镜头设计报告</h1><div class=\"muted\">人物动作：{WebUtility.HtmlEncode(camera.Binding.AnimationId)}　镜头资源：{WebUtility.HtmlEncode(camera.Binding.CameraAnimationId)}　采样：60 FPS</div>
{summary}
<h2>如何阅读</h2><p class=\"muted\">“景别距离”是镜头位置与目标点的距离；FOV 越小，透视越压缩、画面越接近长焦。镜头/目标速度可以用来识别推拉、跟拍、甩镜和目标重定向。下表每 3 帧列一行，完整 60 FPS 原始采样见同目录 <code>{WebUtility.HtmlEncode(jsonFileName)}</code>。</p>
<h2>时间轴参数</h2><table><thead><tr><th>帧</th><th>时间 (秒)</th><th>镜头位置</th><th>目标点</th><th>景别距离</th><th>FOV</th><th>朝向 (Yaw / Pitch)</th><th>镜头速度</th><th>目标速度</th><th>原始 Lens</th></tr></thead><tbody>{rows}</tbody></table>
</body></html>
*/
    }

    private static string BuildCameraReportHtmlCore(ActionCameraClip camera, string jsonFileName, string summary, StringBuilder rows)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><title>镜头设计报告</title><style>");
        html.Append("body{margin:28px;background:#0c1118;color:#dce5ef;font:14px/1.5 Segoe UI,sans-serif}h1{margin:0 0 4px;color:#f1b73f}h2{margin-top:28px}.muted{color:#8da0b6}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(170px,1fr));gap:10px;margin:18px 0}.cards div{padding:12px;border:1px solid #2d3a4b;background:#131c27;border-radius:7px}.cards b,.cards span{display:block}.cards b{color:#91cfff;font-size:12px}table{border-collapse:collapse;width:100%;font-family:Consolas,monospace;font-size:12px}th{position:sticky;top:0;background:#233044;color:#f4c45b}td,th{border:1px solid #2b394a;padding:6px;text-align:right;white-space:nowrap}td:nth-child(3),td:nth-child(4),td:last-child{text-align:left}tr:nth-child(even){background:#111a24}</style></head><body>");
        html.Append("<h1>动作镜头设计报告</h1><div class=\"muted\">人物动作：").Append(WebUtility.HtmlEncode(camera.Binding.AnimationId)).Append("　镜头资源：").Append(WebUtility.HtmlEncode(camera.Binding.CameraAnimationId)).Append("　采样：60 FPS</div>");
        html.Append(summary);
        html.Append("<h2>如何阅读</h2><p class=\"muted\">“景别距离”是镜头位置与目标点的距离；FOV 越小，透视越压缩、画面越接近长焦。镜头/目标速度可以用来识别推拉、跟拍、甩镜和目标重定向。下表每 3 帧列一行，完整 60 FPS 原始采样见同目录 <code>").Append(WebUtility.HtmlEncode(jsonFileName)).Append("</code>。</p>");
        html.Append("<h2>时间轴参数</h2><table><thead><tr><th>帧</th><th>时间 (秒)</th><th>镜头位置</th><th>目标点</th><th>景别距离</th><th>FOV</th><th>朝向 (Yaw / Pitch)</th><th>镜头速度</th><th>目标速度</th><th>原始 Lens</th></tr></thead><tbody>").Append(rows).Append("</tbody></table></body></html>");
        return html.ToString();
    }

    private static float PathLength(IEnumerable<Vector3> points)
    {
        var total = 0f; Vector3? previous = null;
        foreach (var point in points) { if (previous is { } last) total += Vector3.Distance(last, point); previous = point; }
        return total;
    }

    private static float MaxSpeed(IEnumerable<Vector3> points)
    {
        var max = 0f; Vector3? previous = null;
        foreach (var point in points) { if (previous is { } last) max = Math.Max(max, Vector3.Distance(last, point) * 60); previous = point; }
        return max;
    }

    private static string V(Vector3 value) => $"({F(value.X)}, {F(value.Y)}, {F(value.Z)})";
    private static object Components(Vector3 value) => new { x = value.X, y = value.Y, z = value.Z };
    private static double HorizontalFov(double verticalDegrees, double aspectRatio) =>
        2d * Math.Atan(Math.Tan(verticalDegrees * Math.PI / 360d) * Math.Max(.1d, aspectRatio)) * 180d / Math.PI;

    public static void ExportPreview(G1MInfo model, G1AClip? clip, ActionCameraClip? camera, string destination, string name) =>
        Export(destination, name, model, clip, camera, includeGeometry: true);

    private static void Export(string destination, string name, G1MInfo? model, G1AClip? clip,
        ActionCameraClip? actionCamera, bool includeGeometry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? ".");
        using var writer = new StreamWriter(destination, false, new UTF8Encoding(false));
        var connections = new List<string>();
        var rootId = 1L;
        var nextId = 10_000L;
        var boneIds = model is null ? [] : model.Bones.Select(_ => nextId++).ToArray();
        var meshModelIds = model is null ? [] : model.Parts.Select(_ => nextId++).ToArray();
        var geometryIds = model is null ? [] : model.Parts.Select(_ => nextId++).ToArray();
        var skinIds = model is null ? [] : model.Parts.Select(_ => nextId++).ToArray();
        var poseId = model is null ? 0 : nextId++;
        var cameraModelId = actionCamera is null ? 0 : nextId++;
        var cameraAttributeId = actionCamera is null ? 0 : nextId++;

        WriteHeader(writer);
        var animatedBoneCount = model is null || clip is null ? 0 : clip.Tracks.Select(track => model.Bones.FirstOrDefault(b => b.GlobalId == track.GlobalBoneId)?.LocalId ?? -1).Where(index => index >= 0).Distinct().Count();
        var hasCharacterAnimation = animatedBoneCount > 0;
        var hasCameraAnimation = actionCamera is not null;
        // Definitions must exactly match the object graph.  Max's importer is stricter than
        // UE here: an animation-only file may not advertise mesh objects that it does not emit.
        var exportedMeshCount = includeGeometry ? meshModelIds.Length : 0;
        var modelCount = 1 + boneIds.Length + exportedMeshCount + (actionCamera is null ? 0 : 1);
        var geometryCount = includeGeometry ? geometryIds.Length : 0;
        var deformerCount = model is null || !includeGeometry ? 0 : skinIds.Length + CountSkinClusters(model);
        var animationStackCount = (hasCharacterAnimation ? 1 : 0) + (hasCameraAnimation ? 1 : 0);
        var animationNodeCount = (hasCharacterAnimation ? animatedBoneCount * 3 : 0) + (hasCameraAnimation ? 4 : 0);
        var animationCurveCount = (hasCharacterAnimation ? animatedBoneCount * 9 : 0) + (hasCameraAnimation ? 10 : 0);
        writer.WriteLine("Definitions:  {");
        writer.WriteLine("  Version: 100");
        writer.WriteLine($"  ObjectType: \"Model\" {{ Count: {modelCount} }}");
        writer.WriteLine($"  ObjectType: \"Geometry\" {{ Count: {geometryCount} }}");
        writer.WriteLine($"  ObjectType: \"Deformer\" {{ Count: {deformerCount} }}");
        writer.WriteLine($"  ObjectType: \"NodeAttribute\" {{ Count: {(actionCamera is null ? 0 : 1)} }}");
        writer.WriteLine($"  ObjectType: \"Pose\" {{ Count: {(model is null ? 0 : 1)} }}");
        writer.WriteLine($"  ObjectType: \"AnimationStack\" {{ Count: {animationStackCount} }}");
        writer.WriteLine($"  ObjectType: \"AnimationLayer\" {{ Count: {animationStackCount} }}");
        writer.WriteLine($"  ObjectType: \"AnimationCurveNode\" {{ Count: {animationNodeCount} }}");
        writer.WriteLine($"  ObjectType: \"AnimationCurve\" {{ Count: {animationCurveCount} }}");
        writer.WriteLine("}");
        writer.WriteLine("Objects:  {");
        WriteNullModel(writer, rootId, "SceneRoot");

        if (model is not null)
        {
            WriteSkeleton(writer, model, boneIds, rootId, connections);
            if (includeGeometry)
                WriteGeometry(writer, model, meshModelIds, geometryIds, skinIds, boneIds, rootId, ref nextId, connections);
            WriteBindPose(writer, model, poseId, rootId, boneIds,
                includeGeometry ? meshModelIds : Array.Empty<long>());
        }
        if (actionCamera is not null)
            WriteCamera(writer, actionCamera, cameraModelId, cameraAttributeId, rootId, connections);
        if (clip is not null && model is not null)
            WriteCharacterAnimation(writer, model, clip, boneIds, ref nextId, connections);
        if (actionCamera is not null)
            WriteCameraAnimation(writer, actionCamera, cameraModelId, cameraAttributeId, ref nextId, connections);

        writer.WriteLine("}");
        writer.WriteLine("Connections:  {");
        foreach (var connection in connections) writer.WriteLine("  " + connection);
        writer.WriteLine("}");
        // Kept even when no animation stack exists.  The Autodesk/Max importer expects a
        // Takes section in several older FBX 7.x import paths.
        writer.WriteLine("Takes:  {");
        writer.WriteLine("  Current: \"\"");
        writer.WriteLine("}");
    }

    private static void WriteHeader(StreamWriter writer)
    {
        writer.WriteLine("; FBX 7.4.0 project file");
        writer.WriteLine("; Generated by DWOriginsAssetTool");
        writer.WriteLine("FBXHeaderExtension:  {");
        writer.WriteLine("  FBXHeaderVersion: 1003");
        writer.WriteLine("  FBXVersion: 7400");
        writer.WriteLine("  Creator: \"DWOriginsAssetTool\"");
        writer.WriteLine("}");
        writer.WriteLine("GlobalSettings:  {");
        writer.WriteLine("  Version: 1000");
        writer.WriteLine("  Properties70:  {");
        writer.WriteLine("    P: \"UpAxis\", \"int\", \"Integer\", \"\",1");
        writer.WriteLine("    P: \"UpAxisSign\", \"int\", \"Integer\", \"\",1");
        writer.WriteLine("    P: \"FrontAxis\", \"int\", \"Integer\", \"\",2");
        writer.WriteLine("    P: \"FrontAxisSign\", \"int\", \"Integer\", \"\",1");
        writer.WriteLine("    P: \"CoordAxis\", \"int\", \"Integer\", \"\",0");
        writer.WriteLine("    P: \"CoordAxisSign\", \"int\", \"Integer\", \"\",1");
        writer.WriteLine("    P: \"UnitScaleFactor\", \"double\", \"Number\", \"\",1");
        writer.WriteLine("    P: \"TimeMode\", \"enum\", \"\", \"\",6");
        writer.WriteLine("  }");
        writer.WriteLine("}");
    }

    private static void WriteNullModel(StreamWriter writer, long id, string name)
    {
        writer.WriteLine($"  Model: {id}, \"Model::{Escape(name)}\", \"Null\" {{");
        writer.WriteLine("    Version: 232");
        writer.WriteLine("    Properties70:  {");
        writer.WriteLine("      P: \"Lcl Translation\", \"Lcl Translation\", \"\", \"A\",0,0,0");
        writer.WriteLine("      P: \"Lcl Rotation\", \"Lcl Rotation\", \"\", \"A\",0,0,0");
        writer.WriteLine("      P: \"Lcl Scaling\", \"Lcl Scaling\", \"\", \"A\",1,1,1");
        writer.WriteLine("    }");
        writer.WriteLine("  }");
    }

    private static void WriteSkeleton(StreamWriter writer, G1MInfo model, IReadOnlyList<long> boneIds,
        long rootId, ICollection<string> connections)
    {
        foreach (var bone in model.Bones)
        {
            var rotation = QuaternionToEulerDegrees(new Quaternion(bone.RotationX, bone.RotationY, bone.RotationZ, bone.RotationW));
            var id = boneIds[bone.LocalId];
            writer.WriteLine($"  Model: {id}, \"Model::bone_{bone.GlobalId:D4}\", \"LimbNode\" {{");
            writer.WriteLine("    Version: 232");
            writer.WriteLine("    Properties70:  {");
            writer.WriteLine($"      P: \"Lcl Translation\", \"Lcl Translation\", \"\", \"A\",{F(bone.PositionX)},{F(bone.PositionY)},{F(bone.PositionZ)}");
            writer.WriteLine($"      P: \"Lcl Rotation\", \"Lcl Rotation\", \"\", \"A\",{F(rotation.X)},{F(rotation.Y)},{F(rotation.Z)}");
            writer.WriteLine($"      P: \"Lcl Scaling\", \"Lcl Scaling\", \"\", \"A\",{F(bone.ScaleX)},{F(bone.ScaleY)},{F(bone.ScaleZ)}");
            writer.WriteLine("    }");
            writer.WriteLine("  }");
            var parent = bone.ParentLocalId >= 0 && bone.ParentLocalId < boneIds.Count ? boneIds[bone.ParentLocalId] : rootId;
            connections.Add($"C: \"OO\",{id},{parent}");
        }
    }

    private static void WriteBindPose(StreamWriter writer, G1MInfo model, long poseId, long rootId,
        IReadOnlyList<long> boneIds, IReadOnlyList<long> meshModelIds)
    {
        var bindWorld = BuildBindWorld(model.Bones);
        writer.WriteLine($"  Pose: {poseId}, \"Pose::BindPose\", \"BindPose\" {{");
        writer.WriteLine("    Type: \"BindPose\"");
        writer.WriteLine("    Version: 100");
        writer.WriteLine($"    NbPoseNodes: {model.Bones.Count + meshModelIds.Count + 1}");
        writer.WriteLine("    PoseNode: {");
        writer.WriteLine($"      Node: {rootId}");
        WriteMatrix(writer, "Matrix", Matrix4x4.Identity, 6);
        writer.WriteLine("    }");
        for (var i = 0; i < model.Bones.Count; i++)
        {
            writer.WriteLine("    PoseNode: {");
            writer.WriteLine($"      Node: {boneIds[i]}");
            WriteMatrix(writer, "Matrix", bindWorld[i], 6);
            writer.WriteLine("    }");
        }
        // Skin clusters use the mesh's world transform as Transform.  Include every mesh
        // node in the bind pose as well, which is required by 3ds Max and avoids a UE bind
        // pose warning on import.
        foreach (var meshModelId in meshModelIds)
        {
            writer.WriteLine("    PoseNode: {");
            writer.WriteLine($"      Node: {meshModelId}");
            WriteMatrix(writer, "Matrix", Matrix4x4.Identity, 6);
            writer.WriteLine("    }");
        }
        writer.WriteLine("  }");
    }

    private static void WriteGeometry(StreamWriter writer, G1MInfo model, IReadOnlyList<long> meshModelIds,
        IReadOnlyList<long> geometryIds, IReadOnlyList<long> skinIds, IReadOnlyList<long> boneIds, long rootId,
        ref long nextId, ICollection<string> connections)
    {
        var bindWorld = BuildBindWorld(model.Bones);
        for (var partIndex = 0; partIndex < model.Parts.Count; partIndex++)
        {
            var part = model.Parts[partIndex];
            var triangles = Triangles(part).ToArray();
            var geometryId = geometryIds[partIndex];
            var meshId = meshModelIds[partIndex];
            writer.WriteLine($"  Geometry: {geometryId}, \"Geometry::mesh_{partIndex:D3}\", \"Mesh\" {{");
            writer.WriteLine("    GeometryVersion: 124");
            WriteFloatArray(writer, "Vertices", part.Positions, 4);
            var polygon = new List<int>(triangles.Length * 3);
            foreach (var (a, b, c) in triangles) { polygon.Add(a); polygon.Add(b); polygon.Add(-c - 1); }
            WriteIntArray(writer, "PolygonVertexIndex", polygon, 4);
            WriteMeshLayers(writer, part, triangles);
            writer.WriteLine("  }");
            WriteMeshModel(writer, meshId, partIndex);
            connections.Add($"C: \"OO\",{geometryId},{meshId}");
            connections.Add($"C: \"OO\",{meshId},{rootId}");

            if (part.BoneIndices is null || part.BoneWeights is null || model.Bones.Count == 0) continue;
            var skinId = skinIds[partIndex];
            writer.WriteLine($"  Deformer: {skinId}, \"Deformer::skin_{partIndex:D3}\", \"Skin\" {{ Version: 101 }}");
            connections.Add($"C: \"OO\",{skinId},{geometryId}");
            var influences = Math.Max(1, part.InfluencesPerVertex);
            for (var boneIndex = 0; boneIndex < model.Bones.Count; boneIndex++)
            {
                var vertices = new List<int>(); var weights = new List<float>();
                for (var vertex = 0; vertex < part.Positions.Length / 3; vertex++)
                {
                    var baseIndex = vertex * influences;
                    for (var influence = 0; influence < influences; influence++)
                    {
                        var index = baseIndex + influence;
                        if (index >= part.BoneIndices.Length || index >= part.BoneWeights.Length) break;
                        if (part.BoneIndices[index] != boneIndex || part.BoneWeights[index] <= 0) continue;
                        vertices.Add(vertex); weights.Add(part.BoneWeights[index]);
                    }
                }
                if (vertices.Count == 0) continue;
                var clusterId = nextId++;
                writer.WriteLine($"  Deformer: {clusterId}, \"SubDeformer::cluster_{partIndex:D3}_{boneIndex:D3}\", \"Cluster\" {{");
                writer.WriteLine("    Version: 100");
                WriteIntArray(writer, "Indexes", vertices, 4);
                WriteFloatArray(writer, "Weights", weights, 4);
                WriteMatrix(writer, "Transform", Matrix4x4.Identity, 4);
                WriteMatrix(writer, "TransformLink", bindWorld[boneIndex], 4);
                writer.WriteLine("  }");
                connections.Add($"C: \"OO\",{clusterId},{skinId}");
                connections.Add($"C: \"OO\",{boneIds[boneIndex]},{clusterId}");
            }
        }
    }

    private static void WriteMeshLayers(StreamWriter writer, MeshPart part, IReadOnlyList<(int A, int B, int C)> triangles)
    {
        if (part.Normals is { Length: > 0 } normals)
        {
            var values = new List<float>(triangles.Count * 9);
            foreach (var (a, b, c) in triangles)
                foreach (var vertex in new[] { a, b, c })
                {
                    var offset = vertex * 3;
                    values.Add(offset + 2 < normals.Length ? normals[offset] : 0);
                    values.Add(offset + 2 < normals.Length ? normals[offset + 1] : 1);
                    values.Add(offset + 2 < normals.Length ? normals[offset + 2] : 0);
                }
            writer.WriteLine("    LayerElementNormal: 0 {");
            writer.WriteLine("      Version: 101");
            writer.WriteLine("      Name: \"\"");
            writer.WriteLine("      MappingInformationType: \"ByPolygonVertex\"");
            writer.WriteLine("      ReferenceInformationType: \"Direct\"");
            WriteFloatArray(writer, "Normals", values, 6);
            writer.WriteLine("    }");
        }
        if (part.TextureCoordinates is { Length: > 0 } uv)
        {
            var values = new List<float>(triangles.Count * 6);
            foreach (var (a, b, c) in triangles)
                foreach (var vertex in new[] { a, b, c })
                {
                    var offset = vertex * 2;
                    values.Add(offset + 1 < uv.Length ? uv[offset] : 0);
                    values.Add(offset + 1 < uv.Length ? 1 - uv[offset + 1] : 0);
                }
            writer.WriteLine("    LayerElementUV: 0 {");
            writer.WriteLine("      Version: 101");
            writer.WriteLine("      Name: \"UV0\"");
            writer.WriteLine("      MappingInformationType: \"ByPolygonVertex\"");
            writer.WriteLine("      ReferenceInformationType: \"Direct\"");
            WriteFloatArray(writer, "UV", values, 6);
            writer.WriteLine("    }");
        }
        writer.WriteLine("    Layer: 0 {");
        writer.WriteLine("      Version: 100");
        if (part.Normals is { Length: > 0 }) writer.WriteLine("      LayerElement: { Type: \"LayerElementNormal\" TypedIndex: 0 }");
        if (part.TextureCoordinates is { Length: > 0 }) writer.WriteLine("      LayerElement: { Type: \"LayerElementUV\" TypedIndex: 0 }");
        writer.WriteLine("    }");
    }

    private static void WriteMeshModel(StreamWriter writer, long id, int partIndex)
    {
        writer.WriteLine($"  Model: {id}, \"Model::mesh_{partIndex:D3}\", \"Mesh\" {{");
        writer.WriteLine("    Version: 232");
        writer.WriteLine("    Shading: T");
        writer.WriteLine("    Culling: \"CullingOff\"");
        writer.WriteLine("    Properties70:  {");
        writer.WriteLine("      P: \"Lcl Translation\", \"Lcl Translation\", \"\", \"A\",0,0,0");
        writer.WriteLine("      P: \"Lcl Rotation\", \"Lcl Rotation\", \"\", \"A\",0,0,0");
        writer.WriteLine("      P: \"Lcl Scaling\", \"Lcl Scaling\", \"\", \"A\",1,1,1");
        writer.WriteLine("    }");
        writer.WriteLine("  }");
    }

    private static void WriteCamera(StreamWriter writer, ActionCameraClip camera, long modelId, long attributeId,
        long rootId, ICollection<string> connections)
    {
        var frame = ActionCameraCatalog.Sample(camera, 0);
        var rotation = CameraEuler(frame.Position, frame.Forward, frame.Up);
        writer.WriteLine($"  NodeAttribute: {attributeId}, \"NodeAttribute::ActionCamera\", \"Camera\" {{");
        writer.WriteLine("    TypeFlags: \"Camera\"");
        writer.WriteLine("    CameraProjectionType: 0");
        writer.WriteLine($"    FieldOfView: {F(frame.FieldOfView)}");
        writer.WriteLine("  }");
        writer.WriteLine($"  Model: {modelId}, \"Model::ActionCamera\", \"Camera\" {{");
        writer.WriteLine("    Version: 232");
        writer.WriteLine("    Properties70:  {");
        writer.WriteLine($"      P: \"Lcl Translation\", \"Lcl Translation\", \"\", \"A\",{F(frame.Position.X)},{F(frame.Position.Y)},{F(frame.Position.Z)}");
        writer.WriteLine($"      P: \"Lcl Rotation\", \"Lcl Rotation\", \"\", \"A\",{F(rotation.X)},{F(rotation.Y)},{F(rotation.Z)}");
        writer.WriteLine("      P: \"Lcl Scaling\", \"Lcl Scaling\", \"\", \"A\",1,1,1");
        writer.WriteLine("    }");
        writer.WriteLine("  }");
        connections.Add($"C: \"OO\",{attributeId},{modelId}");
        connections.Add($"C: \"OO\",{modelId},{rootId}");
    }

    private static void WriteCharacterAnimation(StreamWriter writer, G1MInfo model, G1AClip clip,
        IReadOnlyList<long> boneIds, ref long nextId, ICollection<string> connections)
    {
        var animated = clip.Tracks.Select(track => model.Bones.FirstOrDefault(b => b.GlobalId == track.GlobalBoneId)?.LocalId ?? -1)
            .Where(index => index >= 0).Distinct().Order().ToArray();
        if (animated.Length == 0) return;
        var stackId = nextId++; var layerId = nextId++;
        WriteAnimationStack(writer, stackId, layerId, "CharacterAction", clip.Info.Duration, connections);
        foreach (var localId in animated)
        {
            var samples = SampleBoneAnimation(model, clip, localId);
            WriteTransformCurves(writer, $"bone_{model.Bones[localId].GlobalId:D4}", boneIds[localId], samples, ref nextId, layerId, connections);
        }
    }

    private static void WriteCameraAnimation(StreamWriter writer, ActionCameraClip camera, long modelId, long attributeId,
        ref long nextId, ICollection<string> connections)
    {
        var stackId = nextId++; var layerId = nextId++;
        WriteAnimationStack(writer, stackId, layerId, "ActionCamera", camera.Clip.Info.Duration, connections);
        var times = SampleTimes(camera.Clip.Info.Duration);
        var position = new List<Vector3>(times.Count); var rotation = new List<Vector3>(times.Count); var fov = new List<float>(times.Count);
        foreach (var time in times)
        {
            var frame = ActionCameraCatalog.Sample(camera, time);
            position.Add(frame.Position); rotation.Add(CameraEuler(frame.Position, frame.Forward, frame.Up)); fov.Add(frame.FieldOfView);
        }
        WriteTransformCurves(writer, "ActionCamera", modelId, new TransformSamples(times, position, rotation,
            Enumerable.Repeat(Vector3.One, times.Count).ToList()), ref nextId, layerId, connections);
        WriteScalarCurve(writer, "ActionCamera_FOV", attributeId, "FieldOfView", times, fov, ref nextId, layerId, connections);
    }

    private static void WriteAnimationStack(StreamWriter writer, long stackId, long layerId, string name, float duration,
        ICollection<string> connections)
    {
        writer.WriteLine($"  AnimationStack: {stackId}, \"AnimStack::{Escape(name)}\", \"\" {{");
        writer.WriteLine("    Properties70:  {");
        writer.WriteLine($"      P: \"LocalStop\", \"KTime\", \"Time\", \"\",{ToFbxTime(duration)}");
        writer.WriteLine($"      P: \"ReferenceStop\", \"KTime\", \"Time\", \"\",{ToFbxTime(duration)}");
        writer.WriteLine("    }");
        writer.WriteLine("  }");
        writer.WriteLine($"  AnimationLayer: {layerId}, \"AnimLayer::BaseLayer\", \"\" {{}}");
        connections.Add($"C: \"OO\",{layerId},{stackId}");
    }

    private sealed record TransformSamples(IReadOnlyList<float> Times, IReadOnlyList<Vector3> Translation,
        IReadOnlyList<Vector3> Rotation, IReadOnlyList<Vector3> Scale);

    private static TransformSamples SampleBoneAnimation(G1MInfo model, G1AClip clip, int localId)
    {
        var times = SampleTimes(clip.Info.Duration);
        var translation = new List<Vector3>(times.Count); var rotation = new List<Vector3>(times.Count); var scale = new List<Vector3>(times.Count);
        Vector3? previousRotation = null;
        foreach (var time in times)
        {
            var (s, r, t) = SampleLocal(model, clip, localId, time);
            var euler = QuaternionToEulerDegrees(r);
            if (previousRotation is { } previous) euler = UnwrapEuler(euler, previous);
            previousRotation = euler;
            translation.Add(t); rotation.Add(euler); scale.Add(s);
        }
        return new(times, translation, rotation, scale);
    }

    private static void WriteTransformCurves(StreamWriter writer, string name, long modelId, TransformSamples samples,
        ref long nextId, long layerId, ICollection<string> connections)
    {
        WriteVectorCurves(writer, name + "_T", modelId, "Lcl Translation", samples.Times, samples.Translation, ref nextId, layerId, connections);
        WriteVectorCurves(writer, name + "_R", modelId, "Lcl Rotation", samples.Times, samples.Rotation, ref nextId, layerId, connections);
        WriteVectorCurves(writer, name + "_S", modelId, "Lcl Scaling", samples.Times, samples.Scale, ref nextId, layerId, connections);
    }

    private static void WriteVectorCurves(StreamWriter writer, string name, long targetId, string property,
        IReadOnlyList<float> times, IReadOnlyList<Vector3> values, ref long nextId, long layerId, ICollection<string> connections)
    {
        var nodeId = nextId++;
        writer.WriteLine($"  AnimationCurveNode: {nodeId}, \"AnimCurveNode::{Escape(name)}\", \"\" {{}}");
        connections.Add($"C: \"OO\",{nodeId},{layerId}");
        connections.Add($"C: \"OP\",{nodeId},{targetId},\"{property}\"");
        var channels = new[] { ('X', values.Select(x => x.X).ToArray()), ('Y', values.Select(x => x.Y).ToArray()), ('Z', values.Select(x => x.Z).ToArray()) };
        foreach (var (axis, channel) in channels)
        {
            var curveId = nextId++;
            WriteCurve(writer, curveId, name + "_" + axis, times, channel);
            connections.Add($"C: \"OP\",{curveId},{nodeId},\"d|{axis}\"");
        }
    }

    private static void WriteScalarCurve(StreamWriter writer, string name, long targetId, string property,
        IReadOnlyList<float> times, IReadOnlyList<float> values, ref long nextId, long layerId, ICollection<string> connections)
    {
        var nodeId = nextId++; var curveId = nextId++;
        writer.WriteLine($"  AnimationCurveNode: {nodeId}, \"AnimCurveNode::{Escape(name)}\", \"\" {{}}");
        WriteCurve(writer, curveId, name, times, values);
        connections.Add($"C: \"OO\",{nodeId},{layerId}");
        connections.Add($"C: \"OP\",{nodeId},{targetId},\"{property}\"");
        connections.Add($"C: \"OP\",{curveId},{nodeId},\"d|X\"");
    }

    private static void WriteCurve(StreamWriter writer, long id, string name, IReadOnlyList<float> times, IReadOnlyList<float> values)
    {
        writer.WriteLine($"  AnimationCurve: {id}, \"AnimCurve::{Escape(name)}\", \"\" {{");
        writer.WriteLine("    Default: 0");
        writer.WriteLine("    KeyVer: 4008");
        WriteLongArray(writer, "KeyTime", times.Select(ToFbxTime).ToArray(), 4);
        WriteFloatArray(writer, "KeyValueFloat", values, 4);
        WriteIntArray(writer, "KeyAttrFlags", Enumerable.Repeat(24836, times.Count).ToArray(), 4);
        WriteFloatArray(writer, "KeyAttrDataFloat", Enumerable.Repeat(0f, times.Count * 4).ToArray(), 4);
        WriteIntArray(writer, "KeyAttrRefCount", Enumerable.Repeat(1, times.Count).ToArray(), 4);
        writer.WriteLine("  }");
    }

    private static (Vector3 Scale, Quaternion Rotation, Vector3 Translation) SampleLocal(G1MInfo model, G1AClip clip, int localId, float time)
    {
        var bone = model.Bones[localId];
        var scale = new Vector3(bone.ScaleX, bone.ScaleY, bone.ScaleZ);
        var rotation = new Quaternion(bone.RotationX, bone.RotationY, bone.RotationZ, bone.RotationW);
        var translation = new Vector3(bone.PositionX, bone.PositionY, bone.PositionZ);
        foreach (var track in clip.Tracks.Where(x => x.GlobalBoneId == bone.GlobalId))
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
        return (scale, Quaternion.Normalize(rotation), translation);
    }

    private static IReadOnlyList<float> SampleTimes(float duration)
    {
        var frames = Math.Max(1, (int)Math.Ceiling(duration * 60));
        var times = new List<float>(frames + 1);
        for (var frame = 0; frame <= frames; frame++) times.Add(Math.Min(duration, frame / 60f));
        return times;
    }

    private static IEnumerable<(int A, int B, int C)> Triangles(MeshPart part)
    {
        var count = part.Positions.Length / 3;
        if (!part.TriangleStrip)
        {
            for (var i = 0; i + 2 < part.Indices.Length; i += 3)
            {
                var a = (int)part.Indices[i]; var b = (int)part.Indices[i + 1]; var c = (int)part.Indices[i + 2];
                if (ValidTriangle(a, b, c, count)) yield return (a, b, c);
            }
            yield break;
        }
        for (var i = 0; i + 2 < part.Indices.Length; i++)
        {
            var a = (int)part.Indices[i]; var b = (int)part.Indices[i + 1]; var c = (int)part.Indices[i + 2];
            if ((i & 1) != 0) (a, b) = (b, a);
            if (ValidTriangle(a, b, c, count)) yield return (a, b, c);
        }
    }

    private static bool ValidTriangle(int a, int b, int c, int count) =>
        a >= 0 && b >= 0 && c >= 0 && a < count && b < count && c < count && a != b && b != c && a != c;

    private static int CountSkinClusters(G1MInfo model)
    {
        var count = 0;
        foreach (var part in model.Parts)
        {
            if (part.BoneIndices is null || part.BoneWeights is null) continue;
            var influences = Math.Max(1, part.InfluencesPerVertex);
            var used = new HashSet<int>();
            for (var i = 0; i < part.BoneIndices.Length && i < part.BoneWeights.Length; i++)
                if (part.BoneIndices[i] >= 0 && part.BoneIndices[i] < model.Bones.Count && part.BoneWeights[i] > 0) used.Add(part.BoneIndices[i]);
            count += used.Count;
        }
        return count;
    }

    private static Matrix4x4[] BuildBindWorld(IReadOnlyList<SkeletonBone> bones)
    {
        var world = new Matrix4x4[bones.Count];
        for (var i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            var local = Matrix4x4.CreateScale(bone.ScaleX, bone.ScaleY, bone.ScaleZ) *
                        Matrix4x4.CreateFromQuaternion(new Quaternion(bone.RotationX, bone.RotationY, bone.RotationZ, bone.RotationW)) *
                        Matrix4x4.CreateTranslation(bone.PositionX, bone.PositionY, bone.PositionZ);
            world[i] = bone.ParentLocalId >= 0 && bone.ParentLocalId < i ? local * world[bone.ParentLocalId] : local;
        }
        return world;
    }

    private static Vector3 QuaternionToEulerDegrees(Quaternion value)
    {
        var q = Quaternion.Normalize(value);
        var sinX = 2 * (q.W * q.X + q.Y * q.Z); var cosX = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        var x = MathF.Atan2(sinX, cosX);
        var sinY = Math.Clamp(2 * (q.W * q.Y - q.Z * q.X), -1f, 1f);
        var y = MathF.Asin(sinY);
        var sinZ = 2 * (q.W * q.Z + q.X * q.Y); var cosZ = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        var z = MathF.Atan2(sinZ, cosZ);
        const float radians = 180f / MathF.PI;
        return new Vector3(x * radians, y * radians, z * radians);
    }

    private static Vector3 CameraEuler(Vector3 position, Vector3 forward, Vector3 up)
    {
        var world = Matrix4x4.CreateWorld(position, forward, up);
        Matrix4x4.Decompose(world, out _, out var rotation, out _);
        return QuaternionToEulerDegrees(rotation);
    }

    private static Vector3 UnwrapEuler(Vector3 value, Vector3 previous) => new(
        Unwrap(value.X, previous.X), Unwrap(value.Y, previous.Y), Unwrap(value.Z, previous.Z));

    private static float Unwrap(float value, float previous)
    {
        while (value - previous > 180) value -= 360;
        while (value - previous < -180) value += 360;
        return value;
    }

    private static void WriteMatrix(StreamWriter writer, string name, Matrix4x4 matrix, int indent)
    {
        var values = new[] { matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44 };
        WriteFloatArray(writer, name, values, indent);
    }

    private static void WriteFloatArray(StreamWriter writer, string name, IEnumerable<float> source, int indent)
    {
        var values = source as ICollection<float> ?? source.ToArray();
        var pad = new string(' ', indent);
        writer.WriteLine($"{pad}{name}: *{values.Count} {{");
        writer.Write($"{pad}  a: ");
        var index = 0;
        foreach (var value in values)
        {
            if (index > 0) writer.Write(',');
            if (index > 0 && index % 12 == 0) writer.Write("\n" + pad + "     ");
            writer.Write(F(value)); index++;
        }
        writer.WriteLine(); writer.WriteLine($"{pad}}}");
    }

    private static void WriteIntArray(StreamWriter writer, string name, IEnumerable<int> source, int indent)
    {
        var values = source as ICollection<int> ?? source.ToArray();
        var pad = new string(' ', indent);
        writer.WriteLine($"{pad}{name}: *{values.Count} {{"); writer.Write($"{pad}  a: ");
        var index = 0;
        foreach (var value in values)
        {
            if (index > 0) writer.Write(',');
            if (index > 0 && index % 18 == 0) writer.Write("\n" + pad + "     ");
            writer.Write(value.ToString(CultureInfo.InvariantCulture)); index++;
        }
        writer.WriteLine(); writer.WriteLine($"{pad}}}");
    }

    private static void WriteLongArray(StreamWriter writer, string name, IEnumerable<long> source, int indent)
    {
        var values = source as ICollection<long> ?? source.ToArray();
        var pad = new string(' ', indent);
        writer.WriteLine($"{pad}{name}: *{values.Count} {{"); writer.Write($"{pad}  a: ");
        var index = 0;
        foreach (var value in values)
        {
            if (index > 0) writer.Write(',');
            if (index > 0 && index % 8 == 0) writer.Write("\n" + pad + "     ");
            writer.Write(value.ToString(CultureInfo.InvariantCulture)); index++;
        }
        writer.WriteLine(); writer.WriteLine($"{pad}}}");
    }

    private static long ToFbxTime(float seconds) => (long)Math.Round(seconds * FbxTicksPerSecond);
    private static string F(float value) => float.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : "0";
    private static string Escape(string value) => value.Replace("\\", "_").Replace("\"", "_");
}

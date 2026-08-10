using System.Numerics;

namespace DWOriginsAssetTool.Core;

/// <summary>
/// Restores action camera clips from the shipped KIDS action graph.
/// The character animation and camera animation are separate G1A resources.
/// </summary>
public static class ActionCameraCatalog
{
    public sealed record Binding(string AnimationId, string CameraAnimationId, string CameraPath,
        string ActionObjectId, string CameraObjectId, float StartFrame, string Evidence,
        float ActionDuration = float.NaN, float CameraDuration = float.NaN, bool IsLocalSegment = false)
    {
        public float StartTime => Math.Max(0, StartFrame) / 60f;
    }
    private sealed record KidsRecord(uint Hash, uint Type, IReadOnlyDictionary<uint, KidsObjectDatabase.Property> Properties);

    private const uint ActionType = 0x11a85121;
    private const uint ModelAnimationWrapperType = 0x34f2ec8b;
    private const uint CameraAnimationDataType = 0xadc35ba3;
    private const uint CameraMotionWrapperType = 0x3b6f1dab;
    private const uint CameraAnimationDataProperty = 0xc94db05f; // CameraAnimationDataObjectNameHash
    private const uint StartFrameProperty = 0x750e63cb;

    public static IReadOnlyDictionary<string, Binding> Build(string gameRoot)
    {
        var data = ReadGameData(gameRoot);
        if (data is null)
            return new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase);
        var (records, animationPaths) = data.Value;
        // Durations are diagnostic metadata only. The game can schedule a local
        // camera segment shorter than its actor action.
        var durationCache = new Dictionary<uint, float>();
        float Duration(uint id)
        {
            if (durationCache.TryGetValue(id, out var value)) return value;
            if (!animationPaths.TryGetValue(id, out var path)) return float.NaN;
            // Catalog construction only needs the authored duration. Reading
            // the full G2A payload here can reject an otherwise valid graph
            // binding when a track variant is not needed by the previewer.
            try { value = G1Parsers.ReadAnimation(path).Duration; }
            catch { value = float.NaN; }
            durationCache[id] = value;
            return value;
        }
        var candidates = new List<(string Key, Binding Binding, float Difference)>();
        foreach (var action in records.Values.Where(x => x.Type == ActionType))
        {
            var references = References(action).Distinct().ToArray();
            var modelAnimations = references.Select(id => records.GetValueOrDefault(id))
                .Where(x => x?.Type == ModelAnimationWrapperType)
                .SelectMany(x => References(x!)).Where(animationPaths.ContainsKey).Distinct().ToArray();
            var cameraObjects = references.Select(id => records.GetValueOrDefault(id))
                .Where(x => x?.Type == CameraAnimationDataType).ToArray();
            var startFrame = action.Properties.TryGetValue(StartFrameProperty, out var start)
                ? start.FloatValues.FirstOrDefault() : 0;
            foreach (var cameraObject in cameraObjects)
            {
                if (!cameraObject!.Properties.TryGetValue(CameraAnimationDataProperty, out var cameraRefs)) continue;
                foreach (var cameraWrapperId in cameraRefs.UInt32Values)
                {
                    if (!records.TryGetValue(cameraWrapperId, out var wrapper) || wrapper.Type != CameraMotionWrapperType) continue;
                    foreach (var cameraAnimationId in References(wrapper).Where(animationPaths.ContainsKey))
                    foreach (var animationId in modelAnimations)
                    {
                        var key = $"{animationId:x8}";
                        var actionDuration = Duration(animationId);
                        var cameraDuration = Duration(cameraAnimationId);
                        var difference = float.IsFinite(actionDuration) && float.IsFinite(cameraDuration)
                            ? Math.Abs(actionDuration - cameraDuration) : float.PositiveInfinity;
                        var localSegment = difference > .001f;
                        candidates.Add((key, new(key, $"{cameraAnimationId:x8}", animationPaths[cameraAnimationId],
                            $"{action.Hash:x8}", $"{cameraObject.Hash:x8}", startFrame,
                            $"KIDS:{animationId:x8} → SequentialPlayer {action.Hash:x8} → " +
                            $"DopeSheet::Camera {cameraObject.Hash:x8} → {cameraAnimationId:x8}.g1a " +
                            $"(start frame {startFrame:0.###}; action {actionDuration:0.###}s / camera {cameraDuration:0.###}s" +
                            (localSegment ? "; local camera segment)" : "; full action camera)"),
                            actionDuration, cameraDuration, localSegment), difference));
                    }
                }
            }
        }
        // A single actor animation can occur in several authored action graphs.
        // Prefer the full-duration camera when present; otherwise keep the
        // directly referenced local segment instead of discarding it merely
        // because its duration is shorter than the actor action.
        return candidates.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x
                .OrderBy(item => item.Binding.IsLocalSegment)
                .ThenBy(item => item.Difference)
                .ThenBy(item => item.Binding.StartFrame)
                .ThenBy(item => item.Binding.CameraAnimationId, StringComparer.OrdinalIgnoreCase)
                .First().Binding, StringComparer.OrdinalIgnoreCase);
    }

    public static ActionCameraClip Load(Binding binding)
    {
        var clip = G1Parsers.ReadAnimationClip(binding.CameraPath);
        var transform = clip.Tracks.FirstOrDefault(x => x.Opcode is 4 or 6 or 8 or 0x66)
            ?? throw new InvalidDataException("镜头 G1A 没有可用的变换轨道");
        return new(binding, clip, transform);
    }

    public static ActionCameraFrame Sample(ActionCameraClip camera, float actionTime)
    {
        var duration = camera.Clip.Info.Duration;
        var time = Math.Clamp(actionTime - camera.Binding.StartTime, 0, duration);
        var (cameraPosition, target, lensParameters) = G1Parsers.SampleCameraTrack(camera.TransformTrack, time);
        // Camera 0x66 is position + look-at target + lens data, not an S/R/T
        // skeletal track. Treating the target coordinates as an exponential
        // quaternion makes the camera spin unpredictably as the target moves.
        var forward = target - cameraPosition;
        if (forward.LengthSquared() < 1e-8f) forward = Vector3.UnitZ;
        forward = Vector3.Normalize(forward);
        var up = Vector3.UnitY;
        if (Math.Abs(Vector3.Dot(forward, up)) > .995f) up = Vector3.UnitZ;
        if (float.IsFinite(lensParameters.X) && Math.Abs(lensParameters.X) <= MathF.PI * 2)
            up = Vector3.Transform(up, Quaternion.CreateFromAxisAngle(forward, lensParameters.X));
        up -= forward * Vector3.Dot(up, forward);
        if (up.LengthSquared() < 1e-8f) up = Vector3.UnitY;
        up = Vector3.Normalize(up);
        // KOEI camera tracks store tan(verticalFov / 2), not a full angle in
        // radians. Interpreting it as radians halves close-up framing and cuts
        // off the actor's head (for example 0.258 is 28.9°, not 14.8°).
        var fovDegrees = 2f * MathF.Atan(MathF.Abs(lensParameters.Y)) * 180f / MathF.PI;
        var fieldOfView = float.IsFinite(fovDegrees) && fovDegrees is >= 10f and <= 150f ? fovDegrees : 42f;
        return new(time, cameraPosition, forward, up, fieldOfView);
    }

    public static bool IsActive(ActionCameraClip camera, float actionTime)
    {
        const float epsilon = 1e-4f;
        return actionTime + epsilon >= camera.Binding.StartTime &&
               actionTime <= camera.Binding.StartTime + camera.Clip.Info.Duration + epsilon;
    }

    public static IEnumerable<float> KeyframeTimes(ActionCameraClip camera)
    {
        return camera.TransformTrack.Channels
            .SelectMany(x => x.Times)
            .Where(float.IsFinite)
            .Select(x => x + camera.Binding.StartTime)
            .Distinct()
            .OrderBy(x => x);
    }

    private static IEnumerable<uint> References(KidsRecord record) =>
        record.Properties.Values.SelectMany(x => x.UInt32Values).Where(x => x != 0);

    private static (Dictionary<uint, KidsRecord> Records, Dictionary<uint, string> AnimationPaths)? ReadGameData(string gameRoot)
    {
        var assetRoot = Path.Combine(gameRoot, "File", "ASSET", "FDataPackage", "Dev", "fdata_out");
        var kidsRoot = Path.Combine(assetRoot, "KIDSSystemResource", "kidsobjdb");
        if (!Directory.Exists(kidsRoot)) return null;
        var actionNeedle = BitConverter.GetBytes(ActionType);
        var cameraNeedle = BitConverter.GetBytes(CameraAnimationDataType);
        var databasePaths = Directory.EnumerateFiles(kidsRoot, "*.kidsobjdb", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(kidsRoot, "*.kidssingletondb", SearchOption.TopDirectoryOnly))
            .Where(path => ContainsAny(path, actionNeedle, cameraNeedle))
            .ToArray();
        if (databasePaths.Length == 0) return null;
        var records = databasePaths.SelectMany(ReadRecords)
            .GroupBy(x => x.Hash).ToDictionary(x => x.Key, x => x.First());
        var animations = Directory.EnumerateFiles(assetRoot, "*.g1a", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(assetRoot, "*.g2a", SearchOption.AllDirectories))
            .Where(x => uint.TryParse(Path.GetFileNameWithoutExtension(x),
                System.Globalization.NumberStyles.HexNumber, null, out _))
            .ToDictionary(x => Convert.ToUInt32(Path.GetFileNameWithoutExtension(x), 16), x => x);
        return (records, animations);
    }

    private static bool ContainsAny(string path, params byte[][] needles)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            return needles.Any(needle => data.AsSpan().IndexOf(needle) >= 0);
        }
        catch { return false; }
    }

    private static IEnumerable<KidsRecord> ReadRecords(string path)
    {
        foreach (var record in KidsObjectDatabase.Read(path).Where(x => !x.IsReference))
            yield return new(record.ObjectHash, record.TypeHash, record.Properties);
    }
}

public sealed record ActionCameraClip(ActionCameraCatalog.Binding Binding, G1AClip Clip, AnimationTrack TransformTrack);
public sealed record ActionCameraFrame(float Time, Vector3 Position, Vector3 Forward, Vector3 Up, float FieldOfView);

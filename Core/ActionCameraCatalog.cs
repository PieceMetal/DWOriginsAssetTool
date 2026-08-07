using System.Numerics;

namespace DWOriginsAssetTool.Core;

/// <summary>
/// Restores action camera clips from the shipped KIDS action graph.
/// The character animation and camera animation are separate G1A resources.
/// </summary>
public static class ActionCameraCatalog
{
    public sealed record Binding(string AnimationId, string CameraAnimationId, string CameraPath,
        string ActionObjectId, string CameraObjectId, string Evidence);
    private sealed record KidsRecord(uint Hash, uint Type, Dictionary<uint, object[]> Properties);

    private const uint ActionType = 0x11a85121;
    private const uint ModelAnimationWrapperType = 0x34f2ec8b;
    private const uint CameraAnimationDataType = 0xadc35ba3;
    private const uint CameraMotionWrapperType = 0x3b6f1dab;
    private const uint CameraAnimationDataProperty = 0xc94db05f; // CameraAnimationDataObjectNameHash

    public static IReadOnlyDictionary<string, Binding> Build(string gameRoot)
    {
        var data = ReadGameData(gameRoot);
        if (data is null)
            return new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase);
        var (records, animationPaths) = data.Value;
        // Cache clip headers while rebuilding the catalog.  A camera association
        // is only valid when it has the same authored duration as the actor
        // action; KIDS occasionally points at an individual camera segment.
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
        static bool SameDuration(float action, float camera) =>
            float.IsFinite(action) && float.IsFinite(camera) && Math.Abs(action - camera) <= 0.001f;

        var result = new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in records.Values.Where(x => x.Type == ActionType))
        {
            var references = References(action).Distinct().ToArray();
            var modelAnimations = references.Select(id => records.GetValueOrDefault(id))
                .Where(x => x?.Type == ModelAnimationWrapperType)
                .SelectMany(x => References(x!)).Where(animationPaths.ContainsKey).Distinct().ToArray();
            var cameraObjects = references.Select(id => records.GetValueOrDefault(id))
                .Where(x => x?.Type == CameraAnimationDataType).ToArray();
            foreach (var cameraObject in cameraObjects)
            {
                if (!cameraObject!.Properties.TryGetValue(CameraAnimationDataProperty, out var cameraRefs)) continue;
                foreach (var cameraWrapperId in cameraRefs.OfType<uint>())
                {
                    if (!records.TryGetValue(cameraWrapperId, out var wrapper) || wrapper.Type != CameraMotionWrapperType) continue;
                    foreach (var cameraAnimationId in References(wrapper).Where(animationPaths.ContainsKey))
                    foreach (var animationId in modelAnimations)
                    {
                        if (!SameDuration(Duration(animationId), Duration(cameraAnimationId)))
                            continue;
                        var key = $"{animationId:x8}";
                        result.TryAdd(key, new(key, $"{cameraAnimationId:x8}", animationPaths[cameraAnimationId],
                            $"{action.Hash:x8}", $"{cameraObject.Hash:x8}",
                            $"KIDS:{animationId:x8} → action {action.Hash:x8} → CameraAnimationData {cameraObject.Hash:x8} → {cameraAnimationId:x8}.g1a"));
                    }
                }
            }
        }
        return result;
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
        var time = Math.Clamp(actionTime, 0, duration);
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

    private static IEnumerable<uint> References(KidsRecord record) =>
        record.Properties.Values.SelectMany(x => x).OfType<uint>().Where(x => x != 0);

    private static (Dictionary<uint, KidsRecord> Records, Dictionary<uint, string> AnimationPaths)? ReadGameData(string gameRoot)
    {
        var assetRoot = Path.Combine(gameRoot, "File", "ASSET", "FDataPackage", "Dev", "fdata_out");
        var kidsRoot = Path.Combine(assetRoot, "KIDSSystemResource", "kidsobjdb");
        var objectDb = Path.Combine(kidsRoot, "CE1CommonResource.motor.kidsobjdb");
        var singletonDb = Path.Combine(kidsRoot, "CE1CommonResource.motor.kidssingletondb");
        if (!File.Exists(objectDb) || !File.Exists(singletonDb)) return null;
        var records = ReadRecords(objectDb).Concat(ReadRecords(singletonDb))
            .GroupBy(x => x.Hash).ToDictionary(x => x.Key, x => x.First());
        var animations = Directory.EnumerateFiles(assetRoot, "*.g1a", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(assetRoot, "*.g2a", SearchOption.AllDirectories))
            .Where(x => uint.TryParse(Path.GetFileNameWithoutExtension(x),
                System.Globalization.NumberStyles.HexNumber, null, out _))
            .ToDictionary(x => Convert.ToUInt32(Path.GetFileNameWithoutExtension(x), 16), x => x);
        return (records, animations);
    }

    private static IEnumerable<KidsRecord> ReadRecords(string path)
    {
        var data = File.ReadAllBytes(path); var starts = new List<int>();
        for (var offset = 0; offset <= data.Length - 8;)
        {
            var relative = data.AsSpan(offset).IndexOf("IDOK0000"u8);
            if (relative < 0) break;
            offset += relative; starts.Add(offset); offset += 8;
        }
        var sizes = new[] { 1, 1, 2, 2, 4, 4, 8, 8, 4, 16, 8, 12 };
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i]; var end = i + 1 < starts.Count ? starts[i + 1] : data.Length;
            if (start + 24 > end) continue;
            var hash = BitConverter.ToUInt32(data, start + 12); var type = BitConverter.ToUInt32(data, start + 16);
            var count = BitConverter.ToInt32(data, start + 20);
            if (count is < 0 or > 4096 || start + 24L + count * 12L > end) continue;
            var descriptors = new (int Type, int Count, uint Hash)[count]; var valid = true;
            for (var n = 0; n < count; n++)
            {
                var offset = start + 24 + n * 12; var propertyType = BitConverter.ToInt32(data, offset);
                var propertyCount = BitConverter.ToInt32(data, offset + 4);
                if (propertyType < 0 || propertyType >= sizes.Length || propertyCount < 0) { valid = false; break; }
                descriptors[n] = (propertyType, propertyCount, BitConverter.ToUInt32(data, offset + 8));
            }
            if (!valid) continue;
            var cursor = start + 24 + count * 12; var properties = new Dictionary<uint, object[]>();
            foreach (var descriptor in descriptors)
            {
                var byteCount = (long)sizes[descriptor.Type] * descriptor.Count;
                if (cursor + byteCount > end) { valid = false; break; }
                object[] values;
                if (descriptor.Type == 5)
                    values = Enumerable.Range(0, descriptor.Count).Select(n => (object)BitConverter.ToUInt32(data, cursor + n * 4)).ToArray();
                else values = [];
                properties[descriptor.Hash] = values; cursor += checked((int)byteCount);
            }
            if (valid) yield return new(hash, type, properties);
        }
    }
}

public sealed record ActionCameraClip(ActionCameraCatalog.Binding Binding, G1AClip Clip, AnimationTrack TransformTrack);
public sealed record ActionCameraFrame(float Time, Vector3 Position, Vector3 Forward, Vector3 Up, float FieldOfView);

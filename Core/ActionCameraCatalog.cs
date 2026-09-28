using System.Numerics;
using System.Security.Cryptography;

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
        // Standalone preview bindings require an equal-duration actor and camera clip.
        // KIDS also references local camera snippets used by larger runtime sequences;
        // those are not the corresponding full camera animation for an actor clip.
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
        // Actor clips whose authored KIDS Action actually contains CameraData. The
        // referenced camera can be only a local segment, but its presence proves that
        // the action was designed as a camera action.
        var cameraAuthoredAnimations = new HashSet<uint>();
        foreach (var action in records.Values.Where(x => x.Type == ActionType))
        {
            var references = References(action).Distinct().ToArray();
            var modelAnimations = references.Select(id => records.GetValueOrDefault(id))
                .Where(x => x?.Type == ModelAnimationWrapperType)
                .SelectMany(x => References(x!)).Where(animationPaths.ContainsKey).Distinct().ToArray();
            var cameraObjects = references.Select(id => records.GetValueOrDefault(id))
                .Where(x => x?.Type == CameraAnimationDataType).ToArray();
            if (cameraObjects.Length > 0)
                cameraAuthoredAnimations.UnionWith(modelAnimations);
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
                        const float durationTolerance = .001f;
                        if (!float.IsFinite(actionDuration) || !float.IsFinite(cameraDuration) ||
                            Math.Abs(actionDuration - cameraDuration) > durationTolerance)
                            continue;
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
        // Field_Common contains full-length standalone camera variants beside the local
        // camera snippet referenced by this Zhao Yun spear action. The three tall-actor
        // variants are byte-identical; use their canonical first resource. This restores
        // the complete 4.9 s timeline instead of the action object's 3.1 s local segment.
        AddRecoveredFullLengthBinding("8b508abf", "0e3329d3", "41865d48", "e9305c48");
        AddEqualDurationCharacterBindings("H0008_Chouun0", [0xe3f1c103u]);

        void AddRecoveredFullLengthBinding(string animationHex, string cameraHex,
            string actionObjectHex, string cameraObjectHex)
        {
            uint animationId = Convert.ToUInt32(animationHex, 16);
            uint cameraId = Convert.ToUInt32(cameraHex, 16);
            if (!animationPaths.TryGetValue(animationId, out _) ||
                !animationPaths.TryGetValue(cameraId, out var cameraPath)) return;
            var actionDuration = Duration(animationId);
            var cameraDuration = Duration(cameraId);
            if (!float.IsFinite(actionDuration) || !float.IsFinite(cameraDuration) ||
                Math.Abs(actionDuration - cameraDuration) > .001f) return;
            candidates.Add((animationHex, new(animationHex, cameraHex, cameraPath,
                actionObjectHex, cameraObjectHex, 0,
                $"KIDS recovered full camera: {animationHex} / {cameraHex}; equal timeline " +
                $"{actionDuration:0.###}s; Field_Common tall-actor camera variant",
                actionDuration, cameraDuration, false), 0));
        }

        void AddEqualDurationCharacterBindings(string ownerName,
            IReadOnlyCollection<uint> externallyConfirmedCameraActions)
        {
            var existing = candidates.Select(candidate => candidate.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ownership = KidsAnimationCatalog.Build(gameRoot);
            var ownedAnimations = ownership
                .Where(pair => pair.Value.Owners.Any(owner =>
                    owner.Contains(ownerName, StringComparison.OrdinalIgnoreCase)))
                .Select(pair => pair.Key)
                .Where(id => uint.TryParse(id, System.Globalization.NumberStyles.HexNumber, null, out _))
                .Select(id => Convert.ToUInt32(id, 16))
                .Where(animationPaths.ContainsKey)
                .ToArray();
            var cameraWrappers = records.Values.Where(record => record.Type == CameraMotionWrapperType).ToArray();
            var cameraIds = cameraWrappers.SelectMany(References).Distinct()
                .Where(animationPaths.ContainsKey)
                .Where(id => animationPaths[id].Contains("Field_Common.character.level",
                    StringComparison.OrdinalIgnoreCase))
                .Where(id =>
                {
                    try
                    {
                        var info = G1Parsers.ReadAnimation(animationPaths[id]);
                        if (info.TrackCount > 8) return false;
                        var clip = G1Parsers.ReadAnimationClip(animationPaths[id]);
                        return clip.Tracks.Any(track => track.Opcode is 4 or 6 or 8 or 0x66);
                    }
                    catch { return false; }
                })
                .OrderBy(id => id)
                .ToArray();

            foreach (var animationId in ownedAnimations.OrderBy(id => id))
            {
                var animationHex = $"{animationId:x8}";
                if (existing.Contains(animationHex)) continue;
                if (!cameraAuthoredAnimations.Contains(animationId) &&
                    !externallyConfirmedCameraActions.Contains(animationId)) continue;
                try
                {
                    // Zhao Yun's authored character skill clips use the complete 232-track
                    // character rig. The ownership graph also reaches 66-track generic hit,
                    // reaction and auxiliary clips; equal duration alone must not turn those
                    // into cinematic actions.
                    if (ownerName.Equals("H0008_Chouun0", StringComparison.OrdinalIgnoreCase) &&
                        G1Parsers.ReadAnimation(animationPaths[animationId]).TrackCount != 232) continue;
                }
                catch { continue; }
                var actionDuration = Duration(animationId);
                if (!float.IsFinite(actionDuration)) continue;
                var cameraId = cameraIds.FirstOrDefault(id =>
                    Math.Abs(Duration(id) - actionDuration) <= .001f);
                if (cameraId == 0) continue;
                var cameraDuration = Duration(cameraId);
                var wrapper = cameraWrappers.FirstOrDefault(record => References(record).Contains(cameraId));
                candidates.Add((animationHex, new(animationHex, $"{cameraId:x8}", animationPaths[cameraId],
                    "duration-match", wrapper is null ? "duration-match" : $"{wrapper.Hash:x8}", 0,
                    $"Equal-duration camera match: {animationHex} / {cameraId:x8}; " +
                    $"owner {ownerName}; timeline {actionDuration:0.###}s",
                    actionDuration, cameraDuration, false), 0));
                existing.Add(animationHex);
            }
        }
        // A single actor animation can occur in several authored action graphs. All
        // remaining candidates have equal timelines; choose deterministically.
        var selected = candidates.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => x
                .OrderBy(item => item.Binding.IsLocalSegment)
                .ThenBy(item => item.Difference)
                .ThenBy(item => item.Binding.StartFrame)
                .ThenBy(item => item.Binding.CameraAnimationId, StringComparer.OrdinalIgnoreCase)
                .First().Binding)
            .ToArray();

        // The shipped database contains several resource IDs for byte-identical actor
        // and camera clips. Collapse only strict duplicates within the same character;
        // equal duration or one matching side alone is not sufficient.
        var allOwnership = KidsAnimationCatalog.Build(gameRoot);
        var shaCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string FileSha(string path)
        {
            if (shaCache.TryGetValue(path, out var sha)) return sha;
            try { sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))); }
            catch { sha = path; }
            return shaCache[path] = sha;
        }
        string OwnerKey(string animationId) => allOwnership.TryGetValue(animationId, out var owner)
            ? string.Join("|", owner.Owners.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            : $"unknown:{animationId}";
        var deduplicated = selected
            .GroupBy(binding =>
            {
                var actorId = Convert.ToUInt32(binding.AnimationId, 16);
                var actorPath = animationPaths.GetValueOrDefault(actorId, binding.AnimationId);
                return $"{OwnerKey(binding.AnimationId)}\0{FileSha(actorPath)}\0{FileSha(binding.CameraPath)}";
            }, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(binding =>
                    animationPaths.GetValueOrDefault(Convert.ToUInt32(binding.AnimationId, 16), binding.AnimationId),
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(binding => binding.AnimationId, StringComparer.OrdinalIgnoreCase).First())
            .ToArray();
        return deduplicated.ToDictionary(binding => binding.AnimationId,
            binding => binding, StringComparer.OrdinalIgnoreCase);
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
        return new(time, cameraPosition, forward, up, fieldOfView) { Target = target };
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
public sealed record ActionCameraFrame(float Time, Vector3 Position, Vector3 Forward, Vector3 Up, float FieldOfView)
{
    public Vector3 Target { get; init; }
}

using System.Numerics;
using System.Globalization;

namespace DWOriginsAssetTool.Core;

/// <summary>
/// Restores runtime camera modifiers scheduled by KIDS SequentialPlayer data.
/// This catalog deliberately exposes only values proven by the shipped data.
/// </summary>
public static class ConfiguredCameraCatalog
{
    public const float ZeroInputCalibrationPitchDegrees = 0f;
    // DWORIGINS retail general-combat-camera table selected by
    // [module+0x3c16390]+0x100 selector 0 -> row 4 in the 0x64-byte table at
    // holder+0xf8. Row 4 stores FOV at +0x18 and distance at +0x1c.
    public const float OriginsCombatFovDegrees = 56.1f;
    public const float OriginsCombatDistance = 320f;
    private const uint DefaultPlayerType = 0xc4b9b28d;
    private const uint ScriptCodeProperty = 0x01ab8e7e;
    private const uint DefaultPlayerScript = 0x422b8674;
    private const uint FocalLengthProperty = 0xad6ed217;
    private const uint HeightOffsetProperty = 0x88a45f3c;
    private const uint MoveSpeedProperty = 0x8e08356c;
    private const uint OrbitSpeedProperty = 0x69f0a57f;
    private const uint TargetFovProperty = 0xeb980f26;
    private const uint CapsuleRatioProperty = 0x23839c80;
    private const uint FollowCameraNearProperty = 0xfa788860;
    private const uint FollowCameraFarProperty = 0x6fd12501;
    private const uint FollowCameraModeProperty = 0x5fdcb09b;
    private const uint FovDeltaProperty = 0x0d6cf34d;
    private const uint UpdatedByDopeSheetProperty = 0xf72bbef4;

    public sealed record BaseFollowSettings(float FocalLength, float HeightOffset, float MoveSpeed,
        float OrbitSpeed, float TargetFovRadians, float CapsuleRatio, float FollowCameraNear,
        float FollowCameraFar, int FollowCameraMode, float FovDelta, bool IsUpdatedCameraByDopeSheet,
        Confidence Confidence, string Evidence, bool IsOriginsCombatPreset = false,
        float? RuntimeDistance = null, float? RuntimeYawRadians = null,
        float? RuntimePitchRadians = null)
    {
        // FocalLength is passed as FollowCamera2's spherical distance by the
        // old DefaultPlayer script. Keep it separate from an Origins runtime
        // distance so a recovered combat preset can replace that input without
        // changing the camera evaluator.
        public float Distance => RuntimeDistance ?? FocalLength;
        public float TargetHeight => HeightOffset;
        public float FieldOfViewDegrees => TargetFovRadians is > .1f and < MathF.PI
            ? TargetFovRadians * 180f / MathF.PI : 45f;
    }

    public static readonly IReadOnlyDictionary<uint, string> OfficialCameraPropertyNames =
        new Dictionary<uint, string>
        {
            [FocalLengthProperty] = "UC_AM_FocalLength",
            [HeightOffsetProperty] = "UC_AM_HeightOffset",
            [MoveSpeedProperty] = "UC_AM_MoveSpeed",
            [OrbitSpeedProperty] = "UC_AM_OrbitSpeed",
            [TargetFovProperty] = "UC_AM_TargetFov",
            [CapsuleRatioProperty] = "UC_AM_CapsuleRatio",
            [FollowCameraNearProperty] = "UC_AM_FollowCameraNear",
            [FollowCameraFarProperty] = "UC_AM_FollowCameraFar",
            [FollowCameraModeProperty] = "UC_AM_FollowCameraMode",
            [FovDeltaProperty] = "UC_AM_FovDelta",
            [UpdatedByDopeSheetProperty] = "UC_AM_IsUpdatedCameraByDopeSheet"
        };

    public enum Confidence
    {
        GameParameter,
        GameParameterWithApproximatedSceneState,
        ApproximatePreview
    }

    public enum EventKind { TargetFov, FollowCamera, FollowCamera2, CameraShake }

    public sealed record ConfigurationTrack(string Label, EventKind Kind, float StartTime,
        float Duration, Confidence Confidence, string Evidence);

    public enum ActionEventKind { Effect, Sound, Model, Camera, MotionSoundInvalidator, Other }

    public sealed record AuthoredActionEvent(uint ObjectHash, ActionEventKind Kind, float StartFrame,
        float DurationFrames, uint ResourceHash, IReadOnlyList<float> PacketFrames,
        IReadOnlyList<uint> PacketValues, string Evidence);

    public sealed record AuthoredActionConfiguration(uint ActionHash, IReadOnlyList<uint> AnimationIds,
        IReadOnlyList<AuthoredActionEvent> Events, string Evidence);

    public sealed record RuntimeConfiguration(BaseFollowSettings Settings,
        IReadOnlyList<ConfigurationTrack> Tracks, IReadOnlyList<ConfiguredCameraEvent> Events,
        string Evidence);

    public sealed record CameraState(Vector3 Position, Vector3 LookAt, float FieldOfView,
        float PushPullDistance = 0, Vector3 ShakeOffset = default, string ActiveEvent = "",
        float NearPlane = -1, float FarPlane = -1);

    public sealed record FollowCameraInput(Vector3 Root, Vector3 Forward, float Distance,
        float HeightOffset, float YawOffsetRadians, float PitchOffsetRadians,
        float NearPlane, float FarPlane, float FieldOfView, Confidence Confidence, string Evidence);

    public abstract record ConfiguredCameraEvent(float StartTime, float Duration, EventKind Kind,
        Confidence Confidence, string Evidence);

    /// <summary>
    /// Exact behavior recovered from kids::placeable::level::TargetFov in the
    /// retail executable. Target FOV is approached by at most StepPerUpdate.
    /// </summary>
    public sealed record TargetFovEvent(float StartTime, float Duration, float TargetFov,
        float StepPerUpdate, string Evidence) : ConfiguredCameraEvent(StartTime, Duration,
        EventKind.TargetFov, Confidence.GameParameter, Evidence);

    public sealed record Clip(string AnimationId, CameraState BaseCamera,
        IReadOnlyList<ConfiguredCameraEvent> Events, string Evidence);

    public static BaseFollowSettings? ReadBaseFollowSettings(string gameRoot)
    {
        var kidsRoot = Path.Combine(gameRoot, "File", "ASSET", "FDataPackage", "Dev", "fdata_out",
            "KIDSSystemResource", "kidsobjdb");
        var candidates = new[] { "61a70c1d.kidsobjdb", "CE1CommonResource.motor.kidsobjdb" }
            .Select(x => Path.Combine(kidsRoot, x)).Where(File.Exists);
        foreach (var path in candidates)
        {
            KidsObjectDatabase.Record? record;
            try
            {
                record = KidsObjectDatabase.Read(path).FirstOrDefault(x => x.TypeHash == DefaultPlayerType &&
                    x.Get(ScriptCodeProperty)?.UInt32Values.Contains(DefaultPlayerScript) == true);
            }
            catch { continue; }
            if (record is null) continue;
            var focalLength = FirstFloat(record, FocalLengthProperty, 500);
            var heightOffset = FirstFloat(record, HeightOffsetProperty, 130);
            var moveSpeed = FirstFloat(record, MoveSpeedProperty, 20);
            var orbitSpeed = FirstFloat(record, OrbitSpeedProperty, .05f);
            var targetFov = FirstFloat(record, TargetFovProperty, MathF.PI / 4);
            var capsuleRatio = FirstFloat(record, CapsuleRatioProperty, .5f);
            var cameraNear = FirstFloat(record, FollowCameraNearProperty, -1);
            var cameraFar = FirstFloat(record, FollowCameraFarProperty, -1);
            var cameraMode = record.Get(FollowCameraModeProperty)?.Int32Values.FirstOrDefault() ?? 0;
            var fovDelta = FirstFloat(record, FovDeltaProperty, MathF.PI);
            var updatedByDopeSheet = FirstBool(record, UpdatedByDopeSheetProperty);
            // The old DefaultPlayer record still supplies generic follow height and
            // smoothing fields, but DWORIGINS does not use its 500/45 camera baseline
            // for the retail battle view. Use the verified Origins row-4 values.
            var originsFovRadians = OriginsCombatFovDegrees * MathF.PI / 180f;
            return new(focalLength, heightOffset, moveSpeed, orbitSpeed, originsFovRadians, capsuleRatio,
                cameraNear, cameraFar, cameraMode, fovDelta, updatedByDopeSheet,
                Confidence.GameParameterWithApproximatedSceneState,
                $"KIDS:{Path.GetFileName(path)} object {record.ObjectHash:x8} 鈫?Script::Code 422b8674; " +
                "DWORIGINS retail combat-camera table holder+0xF8 row 4: FOV 56.1 degrees, distance 320; " +
                "selector monitor kept row 4 throughout Fangtianji derived action 148; " +
                "height/smoothing still use the shipped generic follow fields; scene collision and the unresolved " +
                "action-controller additive distance layer are unavailable",
                IsOriginsCombatPreset: true, RuntimeDistance: OriginsCombatDistance, RuntimeYawRadians: 0,
                RuntimePitchRadians: ZeroInputCalibrationPitchDegrees * MathF.PI / 180f);
        }
        return null;
    }

    public static RuntimeConfiguration? BuildBaseRuntimeConfiguration(string gameRoot, float duration)
    {
        var settings = ReadBaseFollowSettings(gameRoot);
        return settings is null ? null : BuildBaseRuntimeConfiguration(settings, duration);
    }

    public static RuntimeConfiguration BuildBaseRuntimeConfiguration(BaseFollowSettings settings, float duration)
    {
        var tracks = new List<ConfigurationTrack>
        {
            new("FollowCamera2", EventKind.FollowCamera2, 0, Math.Max(0, duration),
                settings.Confidence, settings.Evidence),
            new("TargetFov", EventKind.TargetFov, 0, Math.Max(0, duration),
                Confidence.GameParameter, "UC_AM_TargetFov + retail TargetFov stepping")
        };
        // DefaultPlayer drives TargetFov every update. The shipped value is in
        // radians while the preview camera state is stored in degrees.
        var events = new ConfiguredCameraEvent[]
        {
            new TargetFovEvent(0, Math.Max(0, duration), settings.FieldOfViewDegrees,
                MathF.Abs(settings.FovDelta) * 180f / MathF.PI,
                "UC_AM_TargetFov / UC_AM_FovDelta + retail TargetFov stepping")
        };
        return new(settings, tracks, events, settings.Evidence);
    }

    public const uint SequentialPlayerType = 0x11a85121;
    public const uint AnimationObjectsProperty = 0x9e1e3953;
    public const uint DopeSheetObjectsProperty = 0xe1773a02;
    public const uint StartFrameProperty = 0x750e63cb;
    public const uint FrameLengthProperty = 0xf977f44d;
    public const uint OffsetFromEndProperty = 0xd3f46c71;
    public const uint InterpolateFrameProperty = 0x4bef4ecc;
    public const uint LoopStartFrameProperty = 0xc2afedcf;
    private const uint ModelAnimationWrapperType = 0x34f2ec8b;
    private const uint EffectType = 0xa99dfa37;
    private const uint SoundType = 0xd8004a77;
    private const uint ModelType = 0x0fe035d1;
    private const uint CameraType = 0xadc35ba3;
    private const uint MotionSoundInvalidatorType = 0xed0038d4;
    private const uint EffectResourceProperty = 0x0b6e1578;
    private const uint SoundResourceProperty = 0x72365b94;
    private const uint DataPacketFrameProperty = 0xbd84e645;
    private const uint DataPacketValueProperty = 0xe54ec981;

    public sealed record SequentialEvent(uint ObjectHash, float StartFrame, float FrameLength,
        float OffsetFromEnd, float InterpolateFrame, IReadOnlyList<uint> AnimationObjects,
        IReadOnlyList<uint> DopeSheetObjects, string Database);

    public static IReadOnlyList<SequentialEvent> ReadSequentialEvents(string path)
    {
        return KidsObjectDatabase.Read(path).Where(x => x.TypeHash == SequentialPlayerType).Select(record =>
            new SequentialEvent(record.ObjectHash,
                FirstFloat(record, StartFrameProperty), FirstFloat(record, FrameLengthProperty, -1),
                FirstFloat(record, OffsetFromEndProperty), FirstFloat(record, InterpolateFrameProperty),
                record.Get(AnimationObjectsProperty)?.UInt32Values.ToArray() ?? [],
                record.Get(DopeSheetObjectsProperty)?.UInt32Values.ToArray() ?? [], record.Database)).ToArray();
    }

    /// <summary>
    /// Restores authored per-action event timing from all shipped KIDS action databases.
    /// Ordinary skills are often described in weapon/action module databases instead of
    /// the common singleton, so scanning only the common files misses timing evidence.
    /// </summary>
    public static IReadOnlyDictionary<string, AuthoredActionConfiguration> ReadAuthoredActionConfigurations(
        string gameRoot)
    {
        var kidsRoot = Path.Combine(gameRoot, "File", "ASSET", "FDataPackage", "Dev", "fdata_out",
            "KIDSSystemResource", "kidsobjdb");
        if (!Directory.Exists(kidsRoot)) return new Dictionary<string, AuthoredActionConfiguration>();
        var paths = Directory.EnumerateFiles(kidsRoot, "*.kidssingletondb", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(kidsRoot, "*.kidsobjdb", SearchOption.TopDirectoryOnly)
                .Where(MayContainSequentialPlayers))
            .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0) return new Dictionary<string, AuthoredActionConfiguration>();
        var groupedByAnimation = new Dictionary<string, List<AuthoredActionConfiguration>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            IReadOnlyDictionary<uint, KidsObjectDatabase.Record> records;
            try
            {
                records = KidsObjectDatabase.Read(path).Where(x => !x.IsReference)
                    .GroupBy(x => x.ObjectHash).ToDictionary(x => x.Key, x => x.First());
            }
            catch
            {
                continue;
            }
            foreach (var player in records.Values.Where(x => x.TypeHash == SequentialPlayerType))
            {
                var animationIds = player.Get(AnimationObjectsProperty)?.UInt32Values
                    .SelectMany(id => ResolveAnimationIds(id, records))
                    .Where(x => x != 0).Distinct().ToArray() ?? [];
                if (animationIds.Length == 0) continue;
                var start = FirstFloat(player, StartFrameProperty);
                var length = FirstFloat(player, FrameLengthProperty, -1);
                var events = (player.Get(DopeSheetObjectsProperty)?.UInt32Values ?? [])
                    .Select(id => records.GetValueOrDefault(id)).Where(x => x is not null)
                    .Select(x => new AuthoredActionEvent(x!.ObjectHash, ActionEventKindFor(x.TypeHash), start,
                        length, FirstUInt32(x, EffectResourceProperty, FirstUInt32(x, SoundResourceProperty)),
                        x.Get(DataPacketFrameProperty)?.FloatValues.ToArray() ?? [],
                        x.Get(DataPacketValueProperty)?.UInt32Values.ToArray() ?? [],
                        $"KIDS:{player.Database} SequentialPlayer {player.ObjectHash:x8} -> {x.ObjectHash:x8}"))
                    .ToArray();
                var configuration = new AuthoredActionConfiguration(player.ObjectHash, animationIds, events,
                    $"KIDS:{player.Database} SequentialPlayer {player.ObjectHash:x8}");
                foreach (var animation in animationIds)
                {
                    var key = $"{animation:x8}";
                    if (!groupedByAnimation.TryGetValue(key, out var list))
                    {
                        list = [];
                        groupedByAnimation[key] = list;
                    }
                    list.Add(configuration);
                }
            }
        }
        return groupedByAnimation.ToDictionary(x => x.Key, x => MergeAuthoredActionConfigurations(x.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    private static bool MayContainSequentialPlayers(string path)
    {
        try
        {
            var needle = BitConverter.GetBytes(SequentialPlayerType);
            using var stream = File.OpenRead(path);
            Span<byte> buffer = stackalloc byte[64 * 1024 + 3];
            var carry = 0;
            while (true)
            {
                var read = stream.Read(buffer[carry..]);
                if (read <= 0) return false;
                var length = carry + read;
                for (var i = 0; i + 4 <= length; i++)
                    if (buffer[i] == needle[0] && buffer[i + 1] == needle[1] &&
                        buffer[i + 2] == needle[2] && buffer[i + 3] == needle[3])
                        return true;
                carry = Math.Min(3, length);
                buffer.Slice(length - carry, carry).CopyTo(buffer);
            }
        }
        catch
        {
            return false;
        }
    }

    private static AuthoredActionConfiguration MergeAuthoredActionConfigurations(
        IReadOnlyList<AuthoredActionConfiguration> configurations)
    {
        var first = configurations.First();
        if (configurations.Count == 1) return first;
        var animations = configurations.SelectMany(x => x.AnimationIds).Distinct().ToArray();
        var events = configurations.SelectMany(x => x.Events)
            .GroupBy(EventIdentity)
            .Select(x => x.First())
            .OrderBy(x => x.PacketFrames.Count > 0 ? x.PacketFrames.Min() : x.StartFrame)
            .ThenBy(x => x.ObjectHash)
            .ToArray();
        var evidence = string.Join("; ", configurations
            .Select(x => x.Evidence)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12));
        if (configurations.Count > 12) evidence += $"; ... +{configurations.Count - 12} more SequentialPlayer";
        return new(first.ActionHash, animations, events, evidence);
    }

    private static string EventIdentity(AuthoredActionEvent actionEvent)
    {
        var frames = string.Join(",", actionEvent.PacketFrames.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
        var packets = string.Join(",", actionEvent.PacketValues.Select(x => x.ToString("x8", CultureInfo.InvariantCulture)));
        return $"{actionEvent.Kind}|{actionEvent.ObjectHash:x8}|{actionEvent.ResourceHash:x8}|{frames}|{packets}";
    }


    private static IEnumerable<uint> ResolveAnimationIds(uint id,
        IReadOnlyDictionary<uint, KidsObjectDatabase.Record> records)
    {
        if (id == 0) yield break;
        if (records.TryGetValue(id, out var record) && record.TypeHash == ModelAnimationWrapperType)
        {
            foreach (var reference in record.References)
                yield return reference;
            yield break;
        }
        yield return id;
    }

    private static ActionEventKind ActionEventKindFor(uint type) => type switch
    {
        EffectType => ActionEventKind.Effect,
        SoundType => ActionEventKind.Sound,
        ModelType => ActionEventKind.Model,
        CameraType => ActionEventKind.Camera,
        MotionSoundInvalidatorType => ActionEventKind.MotionSoundInvalidator,
        _ => ActionEventKind.Other
    };

    private static uint FirstUInt32(KidsObjectDatabase.Record record, uint property, uint fallback = 0) =>
        record.Get(property)?.UInt32Values.FirstOrDefault(fallback) ?? fallback;

    private static float FirstFloat(KidsObjectDatabase.Record record, uint property, float fallback = 0) =>
        record.Get(property)?.FloatValues.FirstOrDefault(fallback) ?? fallback;

    private static bool FirstBool(KidsObjectDatabase.Record record, uint property)
    {
        var value = record.Get(property)?.Values.FirstOrDefault();
        return value switch
        {
            byte x => x != 0,
            sbyte x => x != 0,
            int x => x != 0,
            uint x => x != 0,
            _ => false
        };
    }

    /// <summary>
    /// Evaluates proven configuration events at the game's 60 Hz update rate.
    /// Events whose parameter meaning is not yet proven are deliberately not
    /// evaluated here.
    /// </summary>
    public static CameraState Sample(Clip clip, float time)
    {
        var state = clip.BaseCamera;
        const float frameRate = 60f;
        foreach (var fov in clip.Events.OfType<TargetFovEvent>()
                     .Where(x => time >= x.StartTime).OrderBy(x => x.StartTime))
        {
            var end = fov.Duration > 0 ? Math.Min(time, fov.StartTime + fov.Duration) : time;
            var updates = Math.Max(0, (int)MathF.Floor((end - fov.StartTime) * frameRate + 1e-4f));
            var maximumDelta = MathF.Abs(fov.StepPerUpdate) * updates;
            var delta = Math.Clamp(fov.TargetFov - state.FieldOfView, -maximumDelta, maximumDelta);
            state = state with
            {
                FieldOfView = state.FieldOfView + delta,
                ActiveEvent = $"TargetFov 鈫?{fov.TargetFov:0.###}掳"
            };
        }
        return state;
    }

    /// <summary>
    /// Spatial core recovered from retail FollowCamera2: obtain the azimuth and
    /// downward elevation from the previously committed camera direction,
    /// apply controller yaw/pitch offsets, place the eye at the configured
    /// spherical distance, and add HeightOffset to the eye.
    /// The retail function adds that height to the eye only; applying it to the
    /// LookAt as well incorrectly flattens the combat-camera composition.
    /// Scene collision and target selection are runtime state and intentionally
    /// stay outside this pure evaluator.
    /// </summary>
    public static CameraState SampleFollowCamera2(FollowCameraInput input)
    {
        var forward = input.Forward;
        if (forward.LengthSquared() < 1e-8f) forward = Vector3.UnitZ;
        else forward = Vector3.Normalize(forward);
        var horizontal = MathF.Sqrt(forward.X * forward.X + forward.Z * forward.Z);
        // The retail routine derives elevation from the inverse committed W2V
        // basis.  In the preview's look-direction convention a downward view
        // therefore has a positive controller elevation.
        var sourcePitch = MathF.Atan2(-forward.Y, MathF.Max(horizontal, 1e-8f));
        // Retail FollowCamera2 clamps the resulting elevation just below pi/2.
        var pitch = Math.Clamp(sourcePitch + input.PitchOffsetRadians, 0, 1.4137167f);
        var flat = new Vector3(forward.X, 0, forward.Z);
        if (flat.LengthSquared() < 1e-8f) flat = Vector3.UnitZ;
        else flat = Vector3.Normalize(flat);
        if (MathF.Abs(input.YawOffsetRadians) > 1e-8f)
            flat = Vector3.Transform(flat,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, input.YawOffsetRadians));
        var sphericalForward = flat * MathF.Cos(pitch) - Vector3.UnitY * MathF.Sin(pitch);
        var position = input.Root - sphericalForward * input.Distance + Vector3.UnitY * input.HeightOffset;
        // The preview has no live W2V state from the game.  Aim the configured
        // camera at the followed target so the actor remains framed while its
        // controller root translates or jumps.  Root is supplied at the
        // actor's visual centre by the viewport adapter.
        var lookAt = input.Root;
        return new(position, lookAt, input.FieldOfView, input.Distance, default,
            "FollowCamera2", input.NearPlane, input.FarPlane);
    }

    /// <summary>
    /// Small deterministic audit for the recovered coordinate rules. Kept in
    /// production code so release builds can validate the evaluator without a
    /// renderer or a live game process.
    /// </summary>
    public static bool ValidateFollowCamera2Evaluator(out string error)
    {
        var baseInput = new FollowCameraInput(Vector3.Zero,
            Vector3.UnitZ, 500, 130, 0, 0,
            -1, -1, 45, Confidence.ApproximatePreview, "self-test");
        var down = SampleFollowCamera2(baseInput);
        if (!(down.Position.Y >= 130 && down.LookAt.Y < down.Position.Y))
        {
            error = "committed downward elevation was not preserved";
            return false;
        }
        var yawed = SampleFollowCamera2(baseInput with { YawOffsetRadians = MathF.PI / 2 });
        if (!(MathF.Abs(yawed.Position.X) > 100 && MathF.Abs(yawed.Position.Z) < 10))
        {
            error = "controller yaw was not applied around the Y-up axis";
            return false;
        }
        var translated = SampleFollowCamera2(baseInput with { Root = new Vector3(0, 0, 250) });
        if (MathF.Abs((translated.Position.Z - down.Position.Z) - 250) > 1e-3f)
        {
            error = "controller translation did not move the follow camera";
            return false;
        }
        var jumped = SampleFollowCamera2(baseInput with { Root = new Vector3(0, 180, 0) });
        if (MathF.Abs((jumped.Position.Y - down.Position.Y) - 180) > 1e-3f ||
            MathF.Abs(jumped.LookAt.Y - 180) > 1e-3f)
        {
            error = "controller jump did not move the configured camera target";
            return false;
        }
        var controllerTranslation = ExtractControllerTranslation(Vector3.Zero, new Vector3(12, 180, 250));
        if (controllerTranslation != new Vector3(0, 180, 250))
        {
            error = "configured camera did not separate lateral pose sway from jump/forward movement";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>
    /// Translation that the action root contributes to the configured camera
    /// target. Root X contains lateral pose sway for ordinary attacks and is
    /// not a reliable player-controller displacement, so it must not steer the
    /// camera. Keep Y for jumps and Z for forward/back controller movement.
    /// </summary>
    public static Vector3 ExtractControllerTranslation(Vector3 firstRootTranslation,
        Vector3 currentRootTranslation)
    {
        var delta = currentRootTranslation - firstRootTranslation;
        return new Vector3(0,
            float.IsFinite(delta.Y) ? delta.Y : 0,
            float.IsFinite(delta.Z) ? delta.Z : 0);
    }
}

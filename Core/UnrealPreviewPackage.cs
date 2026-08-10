using System.Text;
using System.Text.Json;

namespace DWOriginsAssetTool.Core;

public static class UnrealPreviewPackage
{
    public sealed record Manifest(string Name, string CharacterModel, string CharacterAnimation,
        string Camera, string? WeaponModel, string WeaponSocket, float DurationSeconds,
        int FrameRate, int EndFrame, string CameraMode, double CameraAspectRatio);

    public static void Write(string directory, Manifest manifest,
        IReadOnlyList<GltfExporter.BakedCameraFrame> cameraFrames,
        G1MInfo model, G1AClip clip)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "dworigins_ue_manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "camera_validation_60fps.json"),
            JsonSerializer.Serialize(new
            {
                SampleRate = manifest.FrameRate,
                FilmbackHeightMm = 24.0,
                Frames = cameraFrames.Select(frame => new
                {
                    frame.Time,
                    Position = new[] { frame.Position.X, frame.Position.Z, frame.Position.Y },
                    Forward = new[] { frame.Forward.X, frame.Forward.Z, frame.Forward.Y },
                    Up = new[] { frame.Up.X, frame.Up.Z, frame.Up.Y },
                    VerticalFov = frame.FieldOfView
                }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = false }), new UTF8Encoding(false));
        var preparedPose = AnimationPoseEvaluator.Prepare(model, clip);
        var validatedBones = model.Bones.Where(bone => bone.ParentLocalId >= 0)
            .OrderBy(bone => bone.LocalId).ToArray();
        var animationFrameCount = Math.Max(1, (int)Math.Ceiling(Math.Max(0, clip.Info.Duration) * 60));
        File.WriteAllText(Path.Combine(directory, "animation_validation_60fps.json"),
            JsonSerializer.Serialize(new
            {
                SampleRate = manifest.FrameRate,
                BoneNames = validatedBones.Select(bone => $"bone_{bone.GlobalId:D4}").ToArray(),
                Frames = Enumerable.Range(0, animationFrameCount + 1).Select(frameIndex =>
                {
                    var time = Math.Min(clip.Info.Duration, frameIndex / 60f);
                    var pose = preparedPose.Evaluate(time);
                    var values = new float[validatedBones.Length * 12];
                    for (var boneIndex = 0; boneIndex < validatedBones.Length; boneIndex++)
                    {
                        var matrix = pose.WorldMatrices[validatedBones[boneIndex].LocalId];
                        var offset = boneIndex * 12;
                        // Blender/FBX maps game Y-up to UE Z-up and applies the
                        // verified -90-degree bone basis to every non-root bone.
                        values[offset] = matrix.M41; values[offset + 1] = matrix.M43; values[offset + 2] = matrix.M42;
                        values[offset + 3] = matrix.M11; values[offset + 4] = matrix.M13; values[offset + 5] = matrix.M12;
                        values[offset + 6] = -matrix.M21; values[offset + 7] = -matrix.M23; values[offset + 8] = -matrix.M22;
                        values[offset + 9] = matrix.M31; values[offset + 10] = matrix.M33; values[offset + 11] = matrix.M32;
                    }
                    return new { Time = time, Values = values };
                }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = false }), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "import_dworigins_preview.py"), PythonImporter,
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "README_UE.txt"), Instructions,
            new UTF8Encoding(false));
    }

    private const string Instructions = """
DWOrigins UE 一比一预览包

使用方法：
1. 在 Unreal Editor 中启用 Python Editor Script Plugin、Editor Scripting Utilities、Sequencer Scripting。
2. 打开“输出日志”，切换到 Python 输入模式。
3. 执行：
   exec(open(r'本文件夹\\import_dworigins_preview.py', encoding='utf-8').read())

导入结果：
- 脚本会在 /Game/DWOriginsPreview 下导入角色模型、角色动画、独立武器，并创建 60 FPS Level Sequence。
- 可见即所得的镜头动画在 Level Sequence 的 ActionCamera 绑定里，不是 AnimSequence。
- 打开 LS_... 后查看 Camera Cuts、ActionCamera/变换、ActionCamera/CurrentFocalLength。
- camera_animation_60fps.fbx 是工具当前视口模式的 60 FPS 烘焙镜头源文件；UE 实际播放使用脚本写入的 Sequencer 轨道。

脚本不会覆盖已有同名资产；如果目标目录已存在，会创建带数字后缀的新目录。
""";

    private const string PythonImporter = """
import json
import math
import os
import unreal

PACKAGE_DIR = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(PACKAGE_DIR, 'dworigins_ue_manifest.json'), 'r', encoding='utf-8') as stream:
    MANIFEST = json.load(stream)

def asset_name(value):
    value = os.path.splitext(os.path.basename(value))[0]
    return ''.join(ch if ch.isalnum() or ch == '_' else '_' for ch in value)

def unique_content_folder(base):
    candidate = base
    index = 2
    while unreal.EditorAssetLibrary.does_directory_exist(candidate):
        candidate = base + '_' + str(index)
        index += 1
    unreal.EditorAssetLibrary.make_directory(candidate)
    return candidate

CONTENT_ROOT = unique_content_folder('/Game/DWOriginsPreview/' + asset_name(MANIFEST['Name']))

def import_fbx(filename, destination, import_mesh, import_animations, skeleton=None):
    # UE 5.5+ routes FBX through Interchange by default. Its automated pipeline
    # rejects valid animation-only skeletal FBX as "no content". Use Epic's
    # still-shipped legacy FbxFactory for these deterministic generated files.
    previous_interchange = unreal.SystemLibrary.get_console_variable_bool_value('Interchange.FeatureFlags.Import.FBX')
    unreal.SystemLibrary.execute_console_command(
        unreal.get_editor_subsystem(unreal.UnrealEditorSubsystem).get_editor_world(),
        'Interchange.FeatureFlags.Import.FBX 0')
    options = unreal.FbxImportUI()
    options.automated_import_should_detect_type = False
    options.import_as_skeletal = True
    options.mesh_type_to_import = unreal.FBXImportType.FBXIT_SKELETAL_MESH
    options.original_import_type = unreal.FBXImportType.FBXIT_SKELETAL_MESH
    options.import_mesh = import_mesh
    options.import_animations = import_animations
    options.import_materials = False
    options.import_textures = False
    options.create_physics_asset = False
    if skeleton is not None:
        options.skeleton = skeleton
    task = unreal.AssetImportTask()
    task.filename = os.path.join(PACKAGE_DIR, filename)
    task.destination_path = destination
    task.destination_name = asset_name(filename)
    task.automated = True
    task.replace_existing = False
    task.save = True
    task.options = options
    try:
        unreal.AssetToolsHelpers.get_asset_tools().import_asset_tasks([task])
        return [unreal.load_asset(path) for path in task.imported_object_paths if unreal.load_asset(path)]
    finally:
        unreal.SystemLibrary.execute_console_command(
            unreal.get_editor_subsystem(unreal.UnrealEditorSubsystem).get_editor_world(),
            'Interchange.FeatureFlags.Import.FBX ' + ('1' if previous_interchange else '0'))

def first(items, cls):
    return next((item for item in items if isinstance(item, cls)), None)

character_assets = import_fbx(MANIFEST['CharacterModel'], CONTENT_ROOT + '/Character', True, False)
character_mesh = first(character_assets, unreal.SkeletalMesh)
if not character_mesh:
    raise RuntimeError('UE character model import failed')
skeleton = character_mesh.get_editor_property('skeleton')

animation_assets = import_fbx(MANIFEST['CharacterAnimation'], CONTENT_ROOT + '/Animation', False, True, skeleton)
animation = first(animation_assets, unreal.AnimSequence)
if not animation:
    # UE can omit the returned path for animation-only FBX; inspect the folder.
    animation = next((unreal.load_asset(path) for path in unreal.EditorAssetLibrary.list_assets(
        CONTENT_ROOT + '/Animation', recursive=True, include_folder=False)
        if isinstance(unreal.load_asset(path), unreal.AnimSequence)), None)
if not animation:
    raise RuntimeError('UE character animation import failed')

# Reference geometry is carried only so UE's legacy factory reliably
# classifies this FBX as skeletal animation. UE 5.8 can still leave an
# auxiliary SkeletalMesh even with ImportMesh disabled. Keep it in the UE
# preview package: deleting it leaves a stale package reference that the
# Sequencer UI can dereference when opening the generated Level Sequence.

weapon_mesh = None
if MANIFEST.get('WeaponModel'):
    weapon_assets = import_fbx(MANIFEST['WeaponModel'], CONTENT_ROOT + '/Weapon', True, False)
    weapon_mesh = first(weapon_assets, unreal.SkeletalMesh)

asset_tools = unreal.AssetToolsHelpers.get_asset_tools()
sequence = asset_tools.create_asset('LS_' + asset_name(MANIFEST['Name']), CONTENT_ROOT,
    unreal.LevelSequence, unreal.LevelSequenceFactoryNew())
sequence.set_display_rate(unreal.FrameRate(MANIFEST['FrameRate'], 1))
sequence.set_tick_resolution_directly(unreal.FrameRate(MANIFEST['FrameRate'] * 1000, 1))
sequence.set_playback_start(0)
sequence.set_playback_end(MANIFEST['EndFrame'] + 1)

actor_subsystem = unreal.get_editor_subsystem(unreal.EditorActorSubsystem)
character_binding = sequence.add_spawnable_from_class(unreal.SkeletalMeshActor)
character_binding.set_display_name('DWOrigins Character')
character_actor = character_binding.get_object_template()
character_actor.set_actor_label('DWOrigins_Character')
character_component = character_actor.get_editor_property('skeletal_mesh_component')
character_component.set_skeletal_mesh_asset(character_mesh)

# Bind directly to the Spawnable. A possessable created from the temporary
# editor-world component serializes /Temp/Untitled_1 into the Sequence.
animation_track = character_binding.add_track(unreal.MovieSceneSkeletalAnimationTrack)
animation_section = animation_track.add_section()
animation_section.params.animation = animation
animation_section.set_range(0, MANIFEST['EndFrame'] + 1)

if weapon_mesh:
    weapon_binding = sequence.add_spawnable_from_class(unreal.SkeletalMeshActor)
    weapon_binding.set_display_name('DWOrigins Weapon Baked (' + MANIFEST['WeaponSocket'] + ')')
    weapon_actor = weapon_binding.get_object_template()
    weapon_actor.set_actor_label('DWOrigins_Weapon')
    weapon_component = weapon_actor.get_editor_property('skeletal_mesh_component')
    weapon_component.set_skeletal_mesh_asset(weapon_mesh)
    weapon_component.set_relative_location(unreal.Vector(0, 0, 0), False, False)
    weapon_component.set_relative_rotation(unreal.Rotator(0, 0, 0), False, False)
    weapon_component.set_relative_scale3d(unreal.Vector(1, 1, 1))
    # Do not use MovieScene3DAttachTrack here. UE 5.8's Sequencer UI can
    # dereference stale component bindings on generated Spawnables when opening
    # the Level Sequence. Bake the weapon socket motion into a normal Transform
    # track instead; this keeps the exported weapon as an independent FBX while
    # matching the same runtime hand/socket motion.
    with open(os.path.join(PACKAGE_DIR, 'animation_validation_60fps.json'), 'r', encoding='utf-8') as stream:
        weapon_socket_source = json.load(stream)
    try:
        weapon_socket_index = weapon_socket_source['BoneNames'].index(MANIFEST['WeaponSocket'])
    except ValueError:
        weapon_socket_index = -1
    if weapon_socket_index >= 0:
        weapon_transform_track = weapon_binding.add_track(unreal.MovieScene3DTransformTrack)
        weapon_transform_section = weapon_transform_track.add_section()
        weapon_transform_section.set_range(0, MANIFEST['EndFrame'] + 1)
        weapon_transform_channels = weapon_transform_section.get_all_channels()
        weapon_offset = weapon_socket_index * 12
        for frame_index, expected_frame in enumerate(weapon_socket_source['Frames']):
            values_source = expected_frame['Values']
            position = unreal.Vector(values_source[weapon_offset],
                                     values_source[weapon_offset + 1],
                                     values_source[weapon_offset + 2])
            axis_x = unreal.Vector(values_source[weapon_offset + 3],
                                   values_source[weapon_offset + 4],
                                   values_source[weapon_offset + 5])
            axis_z = unreal.Vector(values_source[weapon_offset + 9],
                                   values_source[weapon_offset + 10],
                                   values_source[weapon_offset + 11])
            axis_x.normalize()
            axis_z.normalize()
            rotation = unreal.MathLibrary.make_rot_from_xz(axis_x, axis_z)
            weapon_values = (position.x, position.y, position.z,
                             rotation.roll, rotation.pitch, rotation.yaw,
                             1.0, 1.0, 1.0)
            key_time = unreal.FrameNumber(value=frame_index)
            for channel, value in zip(weapon_transform_channels, weapon_values):
                channel.add_key(key_time, float(value), 0.0, unreal.MovieSceneTimeUnit.DISPLAY_RATE,
                                unreal.MovieSceneKeyInterpolation.LINEAR)
    else:
        unreal.log_warning('DWO_WEAPON_SOCKET_BAKE_SKIPPED missing socket=' + MANIFEST['WeaponSocket'])

world = unreal.get_editor_subsystem(unreal.UnrealEditorSubsystem).get_editor_world()
# Create a package-owned camera Spawnable first. Let the FBX importer match and
# populate it; create_cameras=True serializes the current /Temp map into the
# Sequence and makes the result non-portable.
camera_binding = sequence.add_spawnable_from_class(unreal.CineCameraActor)
camera_binding.set_display_name('ActionCamera')
camera_actor = camera_binding.get_object_template()
camera_actor.set_actor_label('ActionCamera')
camera_component = camera_actor.get_editor_property('camera_component')
filmback = camera_component.get_editor_property('filmback')
filmback.set_editor_property('sensor_height', 24.0)
filmback.set_editor_property('sensor_width', 24.0 * MANIFEST['CameraAspectRatio'])
camera_component.set_editor_property('filmback', filmback)
try:
    focus_settings = camera_component.get_editor_property('focus_settings')
    focus_settings.set_editor_property('focus_method', unreal.CameraFocusMethod.DISABLE)
    camera_component.set_editor_property('focus_settings', focus_settings)
except Exception:
    pass
try:
    camera_component.set_editor_property('current_aperture', 22.0)
except Exception:
    pass
camera_cut_track = sequence.add_track(unreal.MovieSceneCameraCutTrack)
camera_cut_section = camera_cut_track.add_section()
camera_cut_section.set_range(0, MANIFEST['EndFrame'] + 1)
camera_binding_id = sequence.get_binding_id(camera_binding)
try:
    camera_cut_section.set_camera_binding_id(camera_binding_id)
except Exception:
    camera_cut_section.set_editor_property('CameraBindingID', camera_binding_id)
camera_result = True
with open(os.path.join(PACKAGE_DIR, 'camera_validation_60fps.json'), 'r', encoding='utf-8') as stream:
    baked_camera_frames = json.load(stream)['Frames']
transform_track = camera_binding.add_track(unreal.MovieScene3DTransformTrack)
transform_section = transform_track.add_section()
transform_section.set_range(0, MANIFEST['EndFrame'] + 1)
transform_channels = transform_section.get_all_channels()
focal_track = camera_binding.add_track(unreal.MovieSceneFloatTrack)
focal_track.set_property_name_and_path('CurrentFocalLength', 'CameraComponent.CurrentFocalLength')
focal_section = focal_track.add_section()
focal_section.set_range(0, MANIFEST['EndFrame'] + 1)
focal_channel = focal_section.get_all_channels()[0]
for frame_index, frame in enumerate(baked_camera_frames):
    position = frame['Position']
    forward = unreal.Vector(*frame['Forward'])
    up = unreal.Vector(*frame['Up'])
    rotation = unreal.MathLibrary.make_rot_from_xz(forward, up)
    # Transform channel order is Location XYZ, then Rotator positional order
    # Roll/Pitch/Yaw (UE's Rotator constructor maps arguments this way).
    values = (position[0], position[1], position[2], rotation.roll, rotation.pitch,
              rotation.yaw, 1.0, 1.0, 1.0)
    key_time = unreal.FrameNumber(value=frame_index)
    for channel, value in zip(transform_channels, values):
        channel.add_key(key_time, float(value), 0.0, unreal.MovieSceneTimeUnit.DISPLAY_RATE,
                        unreal.MovieSceneKeyInterpolation.LINEAR)
    focal_length = 24.0 / (2.0 * math.tan(math.radians(frame['VerticalFov']) * 0.5))
    focal_channel.add_key(key_time, focal_length, 0.0, unreal.MovieSceneTimeUnit.DISPLAY_RATE,
                          unreal.MovieSceneKeyInterpolation.LINEAR)
if not camera_result:
    raise RuntimeError('UE camera FBX import failed')

def validate_wysiwyg_camera():
    expected_frames = baked_camera_frames
    camera_cut_tracks = sequence.find_tracks_by_type(unreal.MovieSceneCameraCutTrack)
    if not camera_cut_tracks or not camera_cut_tracks[0].get_sections():
        raise RuntimeError('UE Camera Cuts validation failed: missing Camera Cuts track')
    camera_cut_binding_id = camera_cut_tracks[0].get_sections()[0].get_camera_binding_id()
    try:
        resolved_camera_binding = sequence.resolve_binding_id(camera_cut_binding_id)
    except Exception:
        resolved_camera_binding = None
    if resolved_camera_binding is None:
        raise RuntimeError('UE Camera Cuts validation failed: Camera Cuts binding cannot be resolved')
    if not any(isinstance(track, unreal.MovieScene3DTransformTrack) for track in camera_binding.get_tracks()):
        raise RuntimeError('UE camera animation validation failed: ActionCamera is missing Transform track')
    if not any(str(track.get_display_name()) == 'CurrentFocalLength'
               for track in camera_binding.get_tracks()):
        raise RuntimeError('UE camera animation validation failed: ActionCamera is missing CurrentFocalLength track')
    # Keep both transform and focal-length curves on the package-owned camera
    # Spawnable. Do not add a possessable for the Spawnable template component:
    # UE 5.8's Sequencer details panel can inspect that stale component binding
    # and assert when opening the generated Level Sequence in the full editor.
    transform_track = next((track for track in camera_binding.get_tracks()
        if isinstance(track, unreal.MovieScene3DTransformTrack)), None)
    focal_track = next((track for track in camera_binding.get_tracks()
        if str(track.get_display_name()) == 'CurrentFocalLength'), None)
    if transform_track is None or focal_track is None:
        raise RuntimeError('UE camera validation failed: missing Transform or CurrentFocalLength track')
    transform_sections = transform_track.get_sections()
    focal_sections = focal_track.get_sections()
    if not transform_sections or not focal_sections:
        raise RuntimeError('UE camera animation validation failed: camera animation sections were not created')
    transform_values = [[key.get_value() for key in channel.get_keys()]
        for channel in transform_sections[0].get_all_channels()]
    focal_values = [key.get_value() for key in
        focal_sections[0].get_all_channels()[0].get_keys()]
    counts = [len(expected_frames), len(focal_values)] + [len(values) for values in transform_values]
    if len(transform_values) < 9 or any(count != len(expected_frames) for count in counts):
        raise RuntimeError('UE camera validation failed: 60 FPS baked frame counts mismatch ' + str(counts))
    max_position_error = 0.0
    max_forward_error = 0.0
    max_up_error = 0.0
    max_fov_error = 0.0
    for index, expected in enumerate(expected_frames):
        actual_position = unreal.Vector(transform_values[0][index], transform_values[1][index],
            transform_values[2][index])
        expected_position = unreal.Vector(*expected['Position'])
        max_position_error = max(max_position_error, (actual_position - expected_position).length())
        rotation = unreal.Rotator(transform_values[3][index], transform_values[4][index],
            transform_values[5][index])
        actual_forward = rotation.get_forward_vector()
        actual_up = rotation.get_up_vector()
        expected_forward = unreal.Vector(*expected['Forward'])
        expected_forward.normalize()
        raw_up = unreal.Vector(*expected['Up'])
        raw_up.normalize()
        expected_right = expected_forward.cross(raw_up)
        expected_right.normalize()
        expected_up = expected_right.cross(expected_forward)
        expected_up.normalize()
        max_forward_error = max(max_forward_error, math.degrees(math.acos(max(-1.0, min(1.0,
            actual_forward.dot(expected_forward))))))
        max_up_error = max(max_up_error, math.degrees(math.acos(max(-1.0, min(1.0,
            abs(actual_up.dot(expected_up)))))))
        actual_fov = math.degrees(2.0 * math.atan(24.0 / (2.0 * focal_values[index])))
        max_fov_error = max(max_fov_error, abs(actual_fov - expected['VerticalFov']))
    if max_position_error > 0.01 or max_forward_error > 0.01 or max_up_error > 0.01 or max_fov_error > 0.01:
        raise RuntimeError('UE camera validation failed: position %.6f cm, forward %.6f deg, up %.6f deg, fov %.6f deg' %
            (max_position_error, max_forward_error, max_up_error, max_fov_error))
    unreal.log_warning('DWO_CAMERA_SEQUENCE_OK frames=%d transform_channels=%d focal_keys=%d max_position_cm=%.6f max_forward_deg=%.6f max_up_deg=%.6f max_fov_deg=%.6f' %
        (len(expected_frames), len(transform_values), len(focal_values),
         max_position_error, max_forward_error, max_up_error, max_fov_error))
    unreal.log_warning('DWOrigins WYSIWYG camera validation passed: %d frames, position %.6f cm, forward %.6f deg, up %.6f deg, fov %.6f deg' %
        (len(expected_frames), max_position_error, max_forward_error, max_up_error, max_fov_error))

validate_wysiwyg_camera()

def validate_character_animation():
    with open(os.path.join(PACKAGE_DIR, 'animation_validation_60fps.json'), 'r', encoding='utf-8') as stream:
        expected = json.load(stream)
    bone_names = expected['BoneNames']
    frames = expected['Frames']
    if abs(animation.get_play_length() - MANIFEST['DurationSeconds']) > 0.0001:
        raise RuntimeError('UE character animation validation failed: duration mismatch %.9f / %.9f' %
            (animation.get_play_length(), MANIFEST['DurationSeconds']))
    results = []
    for evaluation_type in (unreal.AnimDataEvalType.SOURCE, unreal.AnimDataEvalType.RAW,
                            unreal.AnimDataEvalType.COMPRESSED):
      options = unreal.AnimPoseEvaluationOptions()
      options.set_editor_property('evaluation_type', evaluation_type)
      max_position_error = 0.0
      max_axis_error = 0.0
      max_position_at = ''
      max_axis_at = ''
      attachment_position_error = 0.0
      attachment_axis_error = 0.0
      for frame_index, expected_frame in enumerate(frames):
        pose = unreal.AnimPoseExtensions.get_anim_pose_at_time(animation, expected_frame['Time'], options)
        if not unreal.AnimPoseExtensions.is_valid(pose):
            raise RuntimeError('UE character animation validation failed: cannot evaluate pose at %.6f seconds' %
                expected_frame['Time'])
        values = expected_frame['Values']
        for bone_index, bone_name in enumerate(bone_names):
            transform = unreal.AnimPoseExtensions.get_bone_pose(pose, unreal.Name(bone_name),
                unreal.AnimPoseSpaces.WORLD)
            offset = bone_index * 12
            expected_position = unreal.Vector(values[offset], values[offset + 1], values[offset + 2])
            position_error = (transform.translation - expected_position).length()
            if bone_name == MANIFEST['WeaponSocket']:
                attachment_position_error = max(attachment_position_error, position_error)
            if position_error > max_position_error:
                max_position_error = position_error
                max_position_at = '%d:%s' % (frame_index, bone_name)
            actual_axes = (transform.rotation.get_axis_x(), transform.rotation.get_axis_y(),
                transform.rotation.get_axis_z())
            for axis_index, actual_axis in enumerate(actual_axes):
                expected_axis = unreal.Vector(values[offset + 3 + axis_index * 3],
                    values[offset + 4 + axis_index * 3], values[offset + 5 + axis_index * 3])
                expected_axis.normalize()
                axis_error = math.degrees(math.acos(max(-1.0, min(1.0,
                    actual_axis.dot(expected_axis)))))
                if bone_name == MANIFEST['WeaponSocket']:
                    attachment_axis_error = max(attachment_axis_error, axis_error)
                if axis_error > max_axis_error:
                    max_axis_error = axis_error
                    max_axis_at = '%d:%s:%d' % (frame_index, bone_name, axis_index)
      results.append((str(evaluation_type), max_position_error, max_axis_error,
                      max_position_at, max_axis_at, attachment_position_error,
                      attachment_axis_error))
    unreal.log_warning('DWO_CHARACTER_ANIMATION_DIAGNOSTIC=' + ' | '.join('%s position %.6f axis %.6f @ %s / %s' %
        (result[0], result[1], result[2], result[3], result[4]) for result in results))
    runtime_result = results[-1]
    if runtime_result[1] > 0.1 or runtime_result[2] > 0.5:
        raise RuntimeError('UE character animation validation failed: runtime bone position %.6f cm, axis %.6f deg' %
            (runtime_result[1], runtime_result[2]))
    unreal.log_warning('DWO_CHARACTER_ANIMATION_OK frames=%d bones=%d runtime_position_cm=%.6f runtime_axis_deg=%.6f' %
        (len(frames), len(bone_names), runtime_result[1], runtime_result[2]))
    unreal.log_warning('DWO_WEAPON_SOCKET_OK socket=%s runtime_position_cm=%.6f runtime_axis_deg=%.6f' %
        (MANIFEST['WeaponSocket'], runtime_result[5], runtime_result[6]))

validate_character_animation()

sequence.set_playback_start(0)
sequence.set_playback_end(MANIFEST['EndFrame'] + 1)
unreal.EditorAssetLibrary.save_loaded_asset(sequence, False)
# FBX skeletal import creates companion Skeleton and internal animation assets
# that are not always listed in AssetImportTask.imported_object_paths. Save the
# complete generated folder recursively; otherwise the Sequence can work in the
# current editor session but lose dependencies after Unreal is restarted.
if not unreal.EditorAssetLibrary.save_directory(CONTENT_ROOT, False, True):
    raise RuntimeError('UE preview package save failed: could not save all character, skeleton, animation, weapon and sequence assets')
unreal.log_warning('DWO_PREVIEW_IMPORT_COMPLETE=' + sequence.get_path_name())
unreal.log_warning('DWO_PREVIEW_MODE=' + MANIFEST['CameraMode'] + ' | 60 FPS | EndFrame=' + str(MANIFEST['EndFrame']))
""";
}

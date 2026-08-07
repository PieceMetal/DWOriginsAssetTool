import bpy
import sys
import json
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index('--') + 1:]
if len(args) not in (2, 3):
    raise RuntimeError('expected source.gltf, destination.fbx, and optional camera_fov.json')
source, destination = args[:2]
camera_only = False
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.context.scene.unit_settings.system = 'METRIC'
bpy.context.scene.unit_settings.scale_length = 1.0
# G1A and the tool preview are sampled at 60 Hz.  Set Blender's time base
# before glTF import so transform animation and the separately keyed camera
# lens use the same frame numbers and end on the same frame.
bpy.context.scene.render.fps = 60
bpy.context.scene.render.fps_base = 1.0
bpy.context.scene.frame_start = 0
bpy.ops.import_scene.gltf(filepath=source)
# Blender's glTF importer can create a helper mesh for an armature even when
# the source intentionally contains no mesh data. Animation-only FBX must have
# zero mesh objects, so distinguish that mode from a real model by inspecting
# the glTF document and remove importer helpers before FBX export.
with open(source, 'r', encoding='utf-8') as source_file:
    source_document = json.load(source_file)
if not source_document.get('meshes'):
    for helper_mesh in [obj for obj in bpy.data.objects if obj.type == 'MESH']:
        bpy.data.objects.remove(helper_mesh, do_unlink=True)
# UE's FBX skeletal importer treats an object named exactly "Armature" as the
# Blender conversion container and removes it from the imported bone tree.
# Leaving the glTF scene/model name here creates one fake parent bone above the
# real game root (bone_0000). Keep model and animation exports identical.
armatures = [obj for obj in bpy.data.objects if obj.type == 'ARMATURE']
if armatures:
    primary_armature = armatures[0]
    primary_armature.name = 'Armature'
    primary_armature.data.name = 'Armature'
if len(args) == 3:
    import math
    with open(args[2], 'r', encoding='utf-8') as f:
        camera_data = json.load(f)
    cameras = [o for o in bpy.context.scene.objects if o.type == 'CAMERA']
    if cameras:
        camera = cameras[0]
        values = camera_data.get('fieldOfViewDegrees', [])
        fps = camera_data.get('sampleRate', 60)
        aspect = max(0.1, float(camera_data.get('aspectRatio', 1.5)))
        sensor_height = float(camera_data.get('sensorHeightMm', 24.0))
        camera_only = bool(camera_data.get('cameraOnly', False))
        camera.data.sensor_fit = 'VERTICAL'
        camera.data.sensor_height = sensor_height
        camera.data.sensor_width = sensor_height * aspect
        transforms = camera_data.get('transforms', [])
        bpy.context.scene.render.fps = fps
        bpy.context.scene.render.fps_base = 1.0
        # Blender's factory scene ends at frame 250.  Longer game actions were
        # therefore exported with the lens curve cut off at frame 250, leaving
        # UE on an intermediate narrow FOV instead of the real final value.
        if values:
            bpy.context.scene.frame_start = 0
            bpy.context.scene.frame_end = len(values) - 1
        # Rebuild one explicit camera action.  The glTF importer may split object
        # transform animation and camera-data animation into separate Actions;
        # exporting only the active Action then silently drops camera rotation.
        if camera.animation_data is None:
            camera.animation_data_create()
        camera.animation_data.action = bpy.data.actions.new('ActionCamera_Transform')
        camera.rotation_mode = 'QUATERNION'
        camera.data.lens = camera.data.sensor_height / (2.0 * math.tan(math.radians(values[0]) * 0.5)) if values else camera.data.lens
        for frame, fov in enumerate(values):
            if frame < len(transforms):
                transform = transforms[frame]
                source_position = Vector(transform['position'])
                source_forward = Vector(transform['forward'])
                source_up = Vector(transform['up'])
                # Match the character FBX basis in Unreal: game (X,Y,Z) becomes
                # UE (X,Z,Y).  The earlier -X mapping mirrored every shot across
                # the character centre, putting a camera authored on the right
                # onto the left.  The camera-only FBX conversion negates Blender
                # Y on Sequencer import, so author (X,-Z,Y) here.
                # author meters and let Unreal perform its one cm conversion.
                position = Vector((source_position.x, -source_position.z, source_position.y)) * 0.01
                forward = Vector((source_forward.x, -source_forward.z, source_forward.y)).normalized()
                up = Vector((source_up.x, -source_up.z, source_up.y)).normalized()
                # glTF uses a camera looking down local -Z with local +Y as up.
                camera.location = position
                # Sequencer represents the FBX meter-to-centimeter conversion
                # on the imported actor scale.  Compensate once so UE receives
                # the camera at Scale 1 while projection remains unchanged.
                camera.scale = Vector((0.01, 0.01, 0.01))
                # Build the complete basis instead of using to_track_quat alone,
                # so the authored roll contained in the Up vector is preserved.
                right = forward.cross(up)
                if right.length_squared < 1e-12:
                    right = Vector((1.0, 0.0, 0.0))
                right.normalize()
                camera_up = right.cross(forward).normalized()
                camera.rotation_quaternion = Matrix((right, camera_up, -forward)).transposed().to_quaternion()
                camera.keyframe_insert(data_path='location', frame=frame, group='Transform')
                camera.keyframe_insert(data_path='rotation_quaternion', frame=frame, group='Transform')
            camera.data.lens = camera.data.sensor_height / (2.0 * math.tan(math.radians(max(1.0, fov)) * 0.5))
            camera.data.keyframe_insert(data_path='lens', frame=frame)
        if camera_only:
            bpy.ops.object.select_all(action='DESELECT')
            camera.select_set(True)
            bpy.context.view_layer.objects.active = camera
export_axis_forward = '-Y' if camera_only else '-Z'
export_axis_up = 'Z' if camera_only else 'Y'
export_apply_units = not camera_only
export_scale_mode = 'FBX_SCALE_ALL' if export_apply_units else 'FBX_SCALE_NONE'
bpy.ops.export_scene.fbx(
    filepath=destination,
    use_selection=camera_only,
    # Keep cameras in the scene.  UE's Sequencer FBX importer only creates a
    # camera track when it finds a real FBX camera node/attribute; an Empty
    # carrying camera curves is not sufficient.
    object_types={'ARMATURE', 'MESH', 'EMPTY', 'CAMERA'},
    add_leaf_bones=False,
    armature_nodetype='NULL',
    bake_anim=True,
    bake_anim_use_all_bones=True,
    bake_anim_use_all_actions=False,
    bake_anim_use_nla_strips=False,
    bake_anim_force_startend_keying=True,
    # Camera cuts and fast attack moves cannot tolerate Blender's default
    # key-reduction pass.  It was deleting authored 60 Hz keys and produced a
    # 71 cm position error plus a near-180 degree direction error on frame 71.
    bake_anim_simplify_factor=0.0,
    path_mode='AUTO',
    axis_forward=export_axis_forward,
    axis_up=export_axis_up,
    apply_unit_scale=export_apply_units,
    apply_scale_options=export_scale_mode,
    bake_space_transform=False,
)

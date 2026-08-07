import bpy
import sys
import json
import math
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index('--') + 1:]
if len(args) not in (2, 3):
    raise RuntimeError('expected source.gltf, destination.fbx, and optional camera_fov.json or --weapon-attachment')
source, destination = args[:2]
camera_only = False
camera_data = None
weapon_attachment = len(args) == 3 and args[2] == '--weapon-attachment'
if len(args) == 3 and not weapon_attachment:
    with open(args[2], 'r', encoding='utf-8') as f:
        camera_data = json.load(f)
    camera_only = bool(camera_data.get('cameraOnly', False))
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

# Blender's factory scene ends at frame 250. The FBX exporter bakes only the
# scene range, so long 60 FPS actions were silently cut to 250 frames. Expand
# the scene to the complete imported Action before exporting any asset type.
action_end = max((action.frame_range[1] for action in bpy.data.actions), default=0.0)
if action_end > 0:
    bpy.context.scene.frame_end = max(1, int(math.ceil(action_end)))

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
if armatures and not camera_only:
    primary_armature = armatures[0]
    primary_armature.name = 'Armature'
    primary_armature.data.name = 'Armature'

# The glTF bridge is authored in meters. Unreal and the original G1 data use
# centimeters, so convert the complete skeletal scene to centimeter-valued
# coordinates before writing FBX and declare the FBX scene as centimeters.
# This keeps the conversion out of bone_0000: UE receives a correctly sized
# mesh with a root scale of 1 instead of a meter scene represented as 100x on
# the root bone. Pose-bone translation curves need the same conversion because
# applying an armature object scale does not alter their keyed values.
if armatures and not camera_only:
    coordinate_scale = 100.0
    for root_object in [obj for obj in bpy.context.scene.objects if obj.parent is None]:
        root_object.scale *= coordinate_scale
    bpy.ops.object.select_all(action='SELECT')
    bpy.context.view_layer.objects.active = armatures[0]
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    for action in bpy.data.actions:
        # Blender 4.x exposes legacy Action.fcurves, while Blender 5.x stores
        # them in layered Action channel bags. Support both user installations.
        if hasattr(action, 'fcurves'):
            action_curves = list(action.fcurves)
        else:
            action_curves = []
            for layer in action.layers:
                for strip in layer.strips:
                    for slot in action.slots:
                        channel_bag = strip.channelbag(slot)
                        if channel_bag is not None:
                            action_curves.extend(channel_bag.fcurves)
        for curve in action_curves:
            if curve.data_path.endswith('.location'):
                for point in curve.keyframe_points:
                    point.co.y *= coordinate_scale
                    point.handle_left.y *= coordinate_scale
                    point.handle_right.y *= coordinate_scale

    # Blender's bone convention contributes an X -90 degree conversion to the
    # first exported deform bone. Move the inverse basis into the armature data
    # while preserving every descendant's world-space rest matrix. UE can then
    # remove the conventional "Armature" container and import the actual game
    # root as identity instead of leaving X -90 on bone_0000.
    primary_armature = armatures[0]
    rest_world = {bone.name: bone.matrix_local.copy() for bone in primary_armature.data.bones}
    bpy.context.view_layer.objects.active = primary_armature
    bpy.ops.object.mode_set(mode='EDIT')
    root_basis = Matrix.Rotation(-math.pi * 0.5, 4, 'X')
    for root_bone in [bone for bone in primary_armature.data.edit_bones if bone.parent is None]:
        root_bone.matrix = root_basis @ root_bone.matrix
    for edit_bone in primary_armature.data.edit_bones:
        if edit_bone.parent is not None:
            edit_bone.matrix = rest_world[edit_bone.name]
    bpy.ops.object.mode_set(mode='OBJECT')

    # Weapon skeletal meshes are attached to the character's bone_0104 in UE.
    # Their game-space mesh basis differs from that socket by X -90 degrees.
    # Bake the verified attachment correction into mesh coordinates only, so
    # the exported game root remains identity and UE components can use
    # Relative Rotation 0 / Relative Scale 1 after attachment.
    if weapon_attachment:
        weapon_basis = Matrix.Rotation(-math.pi * 0.5, 4, 'X')
        for mesh_object in [obj for obj in bpy.context.scene.objects if obj.type == 'MESH']:
            mesh_object.data.transform(weapon_basis)
            mesh_object.data.update()

bpy.context.scene.unit_settings.system = 'METRIC'
bpy.context.scene.unit_settings.scale_length = 1.0 if camera_only else 0.01
if len(args) == 3 and not weapon_attachment:
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
export_scale_mode = 'FBX_SCALE_UNITS' if export_apply_units else 'FBX_SCALE_NONE'
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

"""Build Rain's realistic adult avatar from CC0 MakeHuman anatomy and attributed CC-BY hair.
Blender -b --factory-startup -P build-rain.py -- SOURCE_DIRECTORY OUTPUT.glb
See README.md for the pinned source manifest and asset preparation.
"""
from pathlib import Path
import sys, math, json
import bpy
from mathutils import Vector, Matrix, Quaternion
sys.path.insert(0, str(Path(__file__).parent))
import human_mesh as human
args = sys.argv[sys.argv.index('--') + 1:]
human.SOURCE = Path(args[0]).resolve()
out = Path(args[1]).resolve()
bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
sculpt = [
    ('caucasian-female-young.target', 1),
    ('universal-female-young-averagemuscle-averageweight.target', 1),
    ('head-oval.target', .65), ('chin-width-decr.target', .3),
    ('chin-height-decr.target', .22), ('chin-bones-decr.target', .3),
    ('mouth-upperlip-volume-incr.target', .32), ('mouth-upperlip-height-incr.target', .24), ('mouth-lowerlip-volume-incr.target', .2)
]
for side in ['l', 'r']:
    sculpt += [(side+'-cheek-bones-incr.target', .15), (side+'-eye-scale-incr.target', .08)]
rig = human.build_character(targets=sculpt,
    height_metres=1.72, skin_path=human.SOURCE/'system/skins/young_caucasian_female/young_lightskinned_female_diffuse.png',
    asset_specs=[('Ivory blouse', 'clothes/female_elegantsuit01', 'female_elegantsuit01', .75, False),
                 ('Chestnut hair', 'hair/elvs_hazel_hair', 'elvs_hazel_hair', .5, True),
                 ('Eyebrows', 'eyebrows/eyebrow001', 'eyebrow001', .85, True),
                 ('Eyelashes', 'eyelashes/eyelashes01', 'eyelashes01', .8, True),
                 ('Teeth', 'teeth/teeth_base', 'teeth_base', .4, False),
                 ('Tongue', 'tongue/tongue01', 'tongue01', .65, False)])

# Hair follows the head, not the shoulders sampled by generic clothing weights.
hair = bpy.data.objects['Chestnut hair']
hair.vertex_groups.clear()
hair.vertex_groups.new(name='head').add(list(range(len(hair.data.vertices))), 1, 'REPLACE')

def rotate(name, axis, angle):
    bone = rig.pose.bones[name]
    bpy.context.view_layer.update()
    origin = bone.matrix.translation.copy()
    bone.matrix = Matrix.Translation(origin) @ Quaternion(axis, angle).to_matrix().to_4x4() @ Matrix.Translation(-origin) @ bone.matrix

# Relax shoulders, elbows and hands instead of keeping the source's A-pose.
for side, sign in [('L', 1), ('R', -1)]:
    for part, direction in [('upperarm01', (sign*.075, -.015, -1)), ('lowerarm01', (sign*.035, -.1, -1))]:
        bone = rig.pose.bones[part+'.'+side]
        bpy.context.view_layer.update()
        delta = (bone.tail-bone.head).normalized().rotation_difference(Vector(direction).normalized())
        rotate(bone.name, delta.axis, delta.angle)

# Use the source character's authored facial units, including cheek/lip muscles.
face_poses=json.loads(Path(__file__).with_name('face-poses.json').read_text())['poses']
def expression(name, weight=1):
    for bone_name, angles in face_poses[name].items():
        bone=rig.pose.bones[bone_name]
        rotation=Matrix.Identity(3)
        for axis in 'XYZ': rotation=rotation @ Matrix.Rotation(math.radians(angles.get(axis,0)),3,axis)
        rotation=Quaternion().slerp(rotation.to_quaternion(),weight).to_matrix()
        rest=bone.bone.matrix_local.to_3x3()
        bone.matrix_basis=bone.matrix_basis @ (rest.inverted() @ rotation @ rest).to_4x4()
    bpy.context.view_layer.update()

# Closed-mouth resting smile: opening the lips and jaw belongs only to speech.
for side in ['Left','Right']:
    expression('Mouth'+side+'PullUp',.28)
    expression(side+'CheekUp',.08)
    expression(side+'InnerBrowUp',.08)
    expression(side+'OuterBrowUp',.12)
rotate('head',(0,1,0),.025)

# Keep high-resolution skin, soft highlights and textured chestnut hair.
for name, tint in [('Skin', (.94,.86,.76)), ('Eyebrows', (.42,.29,.19))]:
    mat = bpy.data.materials[name]; nodes=mat.node_tree.nodes; links=mat.node_tree.links
    bsdf=nodes.get('Principled BSDF'); tex=next(n for n in nodes if n.type=='TEX_IMAGE')
    mix=nodes.new('ShaderNodeMix'); mix.data_type='RGBA'; mix.blend_type='MULTIPLY'; mix.inputs['Factor'].default_value=1
    links.new(tex.outputs['Color'],mix.inputs[6]); mix.inputs[7].default_value=(*tint,1); links.new(mix.outputs[2],bsdf.inputs['Base Color'])
    bsdf.inputs['Roughness'].default_value=.58 if name=='Skin' else .8
    if name=='Skin': bsdf.inputs['Subsurface Weight'].default_value=.06

# Retain strand normal/alpha maps, with a uniform warm brown base instead of red dye.
hair_material=bpy.data.materials['Chestnut hair']
hair_shader=hair_material.node_tree.nodes.get('Principled BSDF')
for link in list(hair_shader.inputs['Base Color'].links): hair_material.node_tree.links.remove(link)
hair_shader.inputs['Base Color'].default_value=(.045,.022,.013,1)
hair_shader.inputs['Specular IOR Level'].default_value=.3
hair_shader.inputs['Roughness'].default_value=.46

eye=bpy.data.materials['Brown eyes'].node_tree.nodes.get('Principled BSDF')
eye.inputs['Roughness'].default_value=.14
eye.inputs['Specular IOR Level'].default_value=.5
eye.inputs['Coat Weight'].default_value=.45
eye.inputs['Coat Roughness'].default_value=.08

# A quiet ivory fabric keeps attention on the face; retain the authored cloth normal map.
blouse=bpy.data.materials['Ivory blouse']; bsdf=blouse.node_tree.nodes.get('Principled BSDF')
for link in list(bsdf.inputs['Base Color'].links): blouse.node_tree.links.remove(link)
bsdf.inputs['Base Color'].default_value=(.70,.62,.49,1)
# Dark scalp below the transparent hair roots avoids a pale rim at the crown.
body=bpy.data.objects['Face, neck and hands']
scalp=bpy.data.materials.new('Hair roots');scalp.diffuse_color=(.055,.026,.012,1);scalp.use_nodes=True
scalp.node_tree.nodes.get('Principled BSDF').inputs['Base Color'].default_value=(.055,.026,.012,1)
scalp.node_tree.nodes.get('Principled BSDF').inputs['Roughness'].default_value=.7
body.data.materials.append(scalp)
for poly in body.data.polygons:
    if all(body.data.vertices[i].co.z > 1.692 or (body.data.vertices[i].co.z > 1.62 and body.data.vertices[i].co.y > -.04) for i in poly.vertices): poly.material_index=1

originals = [o for o in bpy.data.collections['Player'].objects if o.type=='MESH']
for obj in originals:
    if obj.name in ['Face, neck and hands', 'Eyes', 'Ivory blouse', 'Chestnut hair']:
        mod=obj.modifiers.new('Portrait smoothing','SUBSURF'); mod.levels=1; mod.render_levels=1
bpy.context.view_layer.update(); deps=bpy.context.evaluated_depsgraph_get()
exported=[]
for obj in originals:
    mesh=bpy.data.meshes.new_from_object(obj.evaluated_get(deps),depsgraph=deps)
    baked=bpy.data.objects.new('Rain '+obj.name,mesh); bpy.context.scene.collection.objects.link(baked)
    baked.shape_key_add(name='Basis'); exported.append((obj,baked))

# Expressions are baked from anatomical skin weights, preserving teeth, lids and hair.
poses = {
    'jawOpen': lambda: expression('JawDrop',.65),
    'blink': lambda: [expression(side+part) for side in ['Left','Right'] for part in ['UpperLidClosed','LowerLidUp']],
    'smile': lambda: [expression('Mouth'+side+'PullUp',.2) for side in ['Left','Right']],
    'nod': lambda: rotate('head',(1,0,0),.065),
    'turn': lambda: rotate('head',(0,0,1),.07)
}
neutral = {b.name:b.matrix_basis.copy() for b in rig.pose.bones}
for name,pose in poses.items():
    pose(); bpy.context.view_layer.update(); deps.update()
    for original,obj in exported:
        evaluated=original.evaluated_get(deps); mesh=evaluated.to_mesh()
        if any((v.co-obj.data.vertices[i].co).length>.000001 for i,v in enumerate(mesh.vertices)):
            key=obj.shape_key_add(name=name)
            for i,v in enumerate(mesh.vertices): key.data[i].co=v.co
        evaluated.to_mesh_clear()
    for b in rig.pose.bones: b.matrix_basis=neutral[b.name]
    bpy.context.view_layer.update(); deps.update()

# Blender initializes newly added shape keys to one; neutral must be explicit.
for _,obj in exported:
    for key in obj.data.shape_keys.key_blocks: key.value=0
bpy.context.view_layer.update()
bpy.ops.object.select_all(action='DESELECT')
for _,obj in exported:
    obj.select_set(True)
    obj['source']='MakeHuman CC0 anatomy; Hazel Hair by Elvaerwyn (CC-BY); adapted for GlobeGlotter. See LICENSE.txt.'
bpy.context.view_layer.objects.active=exported[0][1]
out.parent.mkdir(parents=True,exist_ok=True)
bpy.ops.export_scene.gltf(filepath=str(out),export_format='GLB',use_selection=True,export_animations=False,
    export_morph=True,export_morph_normal=True,export_extras=True,export_image_format='JPEG',export_jpeg_quality=88)
# Optional reproducible portrait render for reviewing geometry and expressions.
if len(args)>2:
    for original,_ in exported: original.hide_render=True
    camera_data=bpy.data.cameras.new('Portrait'); camera=bpy.data.objects.new('Portrait',camera_data); bpy.context.scene.collection.objects.link(camera)
    camera.location=(0,-2.5,1.47); target=Vector((0,-.015,1.40)); camera.rotation_euler=(target-camera.location).to_track_quat('-Z','Y').to_euler(); camera_data.type='ORTHO'; camera_data.ortho_scale=.9
    scene=bpy.context.scene; scene.camera=camera
    for name,location,energy,size,color in [('Key',(-1,-2,3),110,2.5,(1,.91,.81)),('Fill',(1,-1.5,2),60,2,(.85,.91,1)),('Rim',(0,1,2.3),25,2.5,(1,.88,.7))]:
        data=bpy.data.lights.new(name,'AREA'); data.energy=energy; data.shape='DISK'; data.size=size; data.color=color
        obj=bpy.data.objects.new(name,data);scene.collection.objects.link(obj);obj.location=location;obj.rotation_euler=(target-obj.location).to_track_quat('-Z','Y').to_euler()
    scene.world.color=(.18,.18,.18);scene.render.engine='CYCLES';scene.cycles.samples=32
    scene.render.resolution_x=900;scene.render.resolution_y=1000;scene.render.resolution_percentage=100
    scene.render.image_settings.file_format='PNG';scene.render.filepath=args[2]
    bpy.ops.wm.save_as_mainfile(filepath=str(Path(args[2]).with_suffix('.blend')))
    bpy.ops.render.render(write_still=True)
print('EXPORTED',out)

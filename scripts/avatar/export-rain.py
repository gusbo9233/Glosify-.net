"""Export Rain v3.3 to a self-contained GLB; run with Blender --factory-startup --disable-autoexec.
Usage: blender -b --disable-autoexec rain_v3.2.blend -P export-rain.py -- output.glb
The source rig is evaluated, then baked to mesh poses. Its embedded scripts are never executed.
"""
import bpy, sys, math
from pathlib import Path
from mathutils import Matrix, Vector
out = Path(sys.argv[sys.argv.index('--') + 1]).resolve()
source = Path(bpy.data.filepath).parent
if bpy.context.object and bpy.context.object.mode != 'OBJECT': bpy.ops.object.mode_set(mode='OBJECT')
rig = bpy.data.objects['RIG-rain']
for o in bpy.data.objects:
    if o.type == 'MESH':
        for m in o.modifiers:
            if m.type == 'SUBSURF': m.levels = 1; m.render_levels = 1
# Put the hands alongside the hips using the existing IK controls.
print('HAND CONTROLS', [b.name for b in rig.pose.bones if 'IK' in b.name and ('Hand' in b.name or 'Wrist' in b.name)])
for side, x in [('L', .26), ('R', -.26)]:
    for name in [f'IK-Hand.{side}', f'IK-MSTR-Hand.{side}', f'IK-Wrist.{side}']:
        bone = rig.pose.bones.get(name)
        if bone:
            mat = bone.matrix.copy(); mat.translation = Vector((x, -.02, .84)); bone.matrix = mat
            break
bpy.context.view_layer.update()
originals = [o for o in bpy.data.objects if o.type == 'MESH' and o.name.startswith('GEO-rain-')
    and not o.hide_render and not o.hide_viewport and o.name not in ['GEO-rain-eye_cornea','GEO-rain-eye_dots']]
# Baked neutral geometry includes the rig's corrective deformers and smooth normals.
exported = []
deps = bpy.context.evaluated_depsgraph_get()
for original in originals:
    evaluated = original.evaluated_get(deps)
    mesh = bpy.data.meshes.new_from_object(evaluated, depsgraph=deps)
    obj = bpy.data.objects.new(original.name.replace('GEO-rain-', ''), mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj.matrix_world = original.matrix_world.copy()
    obj.shape_key_add(name='Basis')
    exported.append((original, obj))
# Each expression is sampled from the production facial rig, not a hand-scaled jaw.
poses = {
    'jawOpen': [('MSTR-Jaw', 'rotation_euler', 0, .26)],
    'blink': [('ACT-Eyelid_Upper.L','location',1,-.029),('ACT-Eyelid_Upper.R','location',1,-.029),('ACT-Eyelid_Lower.L','location',1,.006),('ACT-Eyelid_Lower.R','location',1,.006)],
    'smile': [('ACT-Lips_Corner.L','location',0,.007),('ACT-Lips_Corner.R','location',0,-.007)],
    'nod': [('FK-Head','rotation_euler',0,.065)],
    'turn': [('FK-Head','rotation_euler',1,.07)]
}
for pose, controls in poses.items():
    previous = []
    for name, prop, axis, value in controls:
        bone = rig.pose.bones.get(name)
        if not bone: continue
        previous.append((bone, prop, getattr(bone, prop).copy()))
        getattr(bone, prop)[axis] += value
    rig.update_tag(); bpy.context.view_layer.update(); deps.update()
    for original, obj in exported:
        evaluated = original.evaluated_get(deps)
        mesh = evaluated.to_mesh()
        if len(mesh.vertices) != len(obj.data.vertices): raise RuntimeError('Pose topology changed: ' + original.name)
        if any((v.co - obj.data.vertices[i].co).length > .00001 for i, v in enumerate(mesh.vertices)):
            key = obj.shape_key_add(name=pose)
            for i, vertex in enumerate(mesh.vertices): key.data[i].co = vertex.co
        evaluated.to_mesh_clear()
    for bone, prop, value in previous: setattr(bone, prop, value)
    rig.update_tag(); bpy.context.view_layer.update(); deps.update()

materials = {}
def material(name, color, texture=None, rough=.65):
    if name in materials: return materials[name]
    mat = bpy.data.materials.new('Avatar.' + name); mat.use_nodes = True
    bsdf = mat.node_tree.nodes.get('Principled BSDF'); bsdf.inputs['Base Color'].default_value = (*color,1); bsdf.inputs['Roughness'].default_value = rough
    if texture:
        path = source/'textures'/texture
        image=bpy.data.images.load(str(path),check_existing=True)
        # Bound texture footprint while preserving face detail.
        if max(image.size) > 2048: image.scale(2048,2048)
        node=mat.node_tree.nodes.new('ShaderNodeTexImage');node.image=image
        mat.node_tree.links.new(node.outputs['Color'],bsdf.inputs['Base Color'])
    materials[name]=mat;return mat
palette = {
 'head': ('skin-head',(.65,.39,.26),'TEX-rain_body_diffuse.1003.png',.58),
 'hair_main': ('hair',(.08,.035,.02),'TEX-rain_hair_diffuse.png',.5),
 'hair_ponytail': ('hair',(.08,.035,.02),'TEX-rain_hair_diffuse.png',.5),
 'hair_strand': ('hair',(.08,.035,.02),'TEX-rain_hair_diffuse.png',.5),
 'eyes': ('eyes',(.9,.9,.9),'TEX-rain_eyes.png',.22),
 'eyebrows': ('brows',(.09,.035,.019),None,.9), 'eyelashes': ('brows',(.09,.035,.019),None,.9),
 'top': ('top',(.045,.12,.13),None,.9), 'scarf': ('scarf',(.39,.16,.075),None,.86),
 'jeans': ('jeans',(.055,.11,.13),'TEX-rain_jeans_diffuse.png',.9),
 'shoes': ('shoes',(.09,.065,.05),None,.85), 'hairband': ('hairband',(.32,.09,.035),None,.7),
 'gums_lower': ('teeth',(.84,.76,.64),None,.4), 'gums_upper': ('teeth',(.84,.76,.64),None,.4),
 'tongue': ('tongue',(.4,.09,.08),None,.7)
}
for original,obj in exported:
    obj.data.materials.clear()
    if obj.name == 'body':
        obj.data.materials.append(material('body1',(.65,.4,.28),'TEX-rain_body_diffuse.1001.png'))
        obj.data.materials.append(material('body2',(.65,.4,.28),'TEX-rain_body_diffuse.1002.png'))
        uv=obj.data.uv_layers.active.data
        for polygon in obj.data.polygons:
            tile=min(1,max(0,math.floor(sum(uv[i].uv.x for i in polygon.loop_indices)/len(polygon.loop_indices))))
            polygon.material_index=tile
            for i in polygon.loop_indices:uv[i].uv.x-=tile
    else:
        obj.data.materials.append(material(*palette.get(obj.name,('skin',(.65,.4,.28),None,.65))))
        for p in obj.data.polygons: p.material_index=0
        if obj.name=='head':
            for uv in obj.data.uv_layers.active.data: uv.uv.x-=2
    for polygon in obj.data.polygons: polygon.use_smooth=True
    obj['source']='Rain Rig (CC) Blender Foundation | studio.blender.org; adapted for GlobeGlotter'
# Export only the baked meshes; the 2,166-bone production rig stays out of the browser.
bpy.ops.object.select_all(action='DESELECT')
for _,obj in exported: obj.select_set(True)
bpy.context.view_layer.objects.active=exported[0][1]
out.parent.mkdir(parents=True,exist_ok=True)
bpy.ops.export_scene.gltf(filepath=str(out),export_format='GLB',use_selection=True,export_animations=False,export_morph=True,export_morph_normal=True,export_extras=True,export_image_format='JPEG',export_jpeg_quality=85)
print('EXPORTED',out, 'vertices',sum(len(o.data.vertices) for _,o in exported))
print('MORPHS',[(o.name,[k.name for k in o.data.shape_keys.key_blocks]) for _,o in exported])

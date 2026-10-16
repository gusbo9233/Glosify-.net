"""Build Mei from MakeHuman anatomy, with original hair and clothing.
blender --factory-startup -b -P scripts/avatar/build-mei.py -- SOURCE_DIRECTORY OUTPUT.glb [PORTRAIT.png]
See README.md for the pinned source manifest and asset preparation.
"""
from pathlib import Path
import sys, math, argparse
import numpy as np
import bpy
import bmesh
from mathutils import Vector
from mathutils.bvhtree import BVHTree
sys.path.insert(0, str(Path(__file__).parent))
import human_mesh as human
from avatar_textures import clamp, smooth, blur, rasterize, image_pixels, new_image
from avatar_rig import Poser, morph_poses, bake, export, render_portrait
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('source'); parser.add_argument('output'); parser.add_argument('portrait', nargs='?')
settings = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
character_name = 'Mei'
human.SOURCE = Path(settings.source).resolve()
out = Path(settings.output).resolve()
bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)

# Mid thirties: MakeHuman's East Asian female base, blending the young and old macro targets
# (age 25 -> 0, 90 -> 1), with only a gently oval head, slightly larger eyes and ears that sit
# close to the head.
OLD = .1
sculpt = [('asian-female-young.target', 1-OLD), ('asian-female-old.target', OLD),
          ('universal-female-young-averagemuscle-averageweight.target', 1-OLD),
          ('universal-female-old-averagemuscle-averageweight.target', OLD),
          ('head-oval.target', .4)]
for side in 'lr':
    sculpt += [(side+'-eye-scale-incr.target', .1), (side+'-ear-flap-decr.target', .6)]
SKIN = human.SOURCE/'system/skins/young_asian_female/young_lightskinned_female_diffuse3.png'
def character(assets):
    return human.build_character(targets=sculpt, eye_color='brownlight', height_metres=1.72, skin_path=SKIN, asset_specs=assets)

# Build the unclothed figure first: the blazer is slimmed against it, and it supplies the skin
# above the blouse neckline that the suit's fitting would otherwise hide.
character([])
full = bpy.data.objects['Face, neck and hands']; full.name = 'Unclothed figure'
for c in list(full.users_collection): c.objects.unlink(full)
bpy.context.scene.collection.objects.link(full)
full_mesh = full.data.copy()
rig = character([('Dark blazer', 'clothes/male_elegantsuit01', 'male_elegantsuit01', .8, False),
                 ('Eyebrows', 'eyebrows/eyebrow012', 'eyebrow012', .85, True),
                 ('Eyelashes', 'eyelashes/eyelashes01', 'eyelashes01', .8, True),
                 ('Teeth', 'teeth/teeth_base', 'teeth_base', .4, False),
                 ('Tongue', 'tongue/tongue01', 'tongue01', .65, False)])
from mei_outfit import dress, neckline
clothed = bpy.data.objects['Face, neck and hands']
visible = {tuple(round(c, 4) for c in p.center) for p in clothed.data.polygons}
bm = bmesh.new(); bm.from_mesh(full.data)
hidden = [f for f in bm.faces if tuple(round(c, 4) for c in f.calc_center_median()) not in visible]
bmesh.ops.delete(bm, geom=[f for f in hidden if max(v.co.z - neckline(v.co.x, v.co.y) for v in f.verts) < -.004], context='FACES')
bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
bm.to_mesh(full.data); bm.free(); full.data.update()
skin_material = clothed.data.materials[0]
bpy.data.objects.remove(clothed)
body = full; body.name = 'Face, neck and hands'
for c in list(body.users_collection): c.objects.unlink(body)
bpy.data.collections['Player'].objects.link(body); body.parent = rig
body.data.materials[0] = skin_material
next(m for m in body.modifiers if m.type == 'ARMATURE').object = rig

# ---------------------------------------------------------------------------------
poser = Poser(rig)
rotate, expression, both = poser.rotate, poser.expression, poser.both
poser.relax_arms()

# ---------------------------------------------------------------------------------
from mei_hair import grow_hair, hair_mask, theta
hair_objects = grow_hair(body, rig, bpy.data.collections['Player'])

# A warm, closed-mouth resting smile with softly lifted brows.
resting_lid = .04
for side in ['Left', 'Right']:
    expression('Mouth'+side+'PullUp', .42)
    expression(side+'CheekUp', .22)
    expression(side+'InnerBrowUp', .06)
    expression(side+'OuterBrowUp', .08)
    expression(side+'UpperLidClosed', resting_lid)
rotate('head', (0, 1, 0), -.015)

for name, tint in [('Skin', (1, .982, .962)), ('Eyebrows', (.13, .10, .085))]:
    mat = bpy.data.materials[name] if name != 'Skin' else skin_material
    nodes = mat.node_tree.nodes; links = mat.node_tree.links
    bsdf = nodes.get('Principled BSDF'); tex = next(n for n in nodes if n.type == 'TEX_IMAGE')
    mix = nodes.new('ShaderNodeMix'); mix.data_type = 'RGBA'; mix.blend_type = 'MULTIPLY'; mix.inputs['Factor'].default_value = 1
    links.new(tex.outputs['Color'], mix.inputs[6]); mix.inputs[7].default_value = (*tint, 1); links.new(mix.outputs[2], bsdf.inputs['Base Color'])
    bsdf.inputs['Roughness'].default_value = .5 if name == 'Skin' else .8
    if name == 'Skin': bsdf.inputs['Specular IOR Level'].default_value = .3
    if name == 'Skin': bsdf.inputs['Subsurface Weight'].default_value = .06
# Slightly fuller, softly tapered brows.
brows = bpy.data.objects['Eyebrows']
center_z = sum(v.co.z for v in brows.data.vertices)/len(brows.data.vertices)
for vertex in brows.data.vertices: vertex.co.z = center_z + (vertex.co.z-center_z)*.95
brows.data.update()

# Skin under the hair is darkened so gaps at the hairline and part read as roots; lips get a
# natural rose tone (no added makeup).
skin_image = next(n for n in skin_material.node_tree.nodes if n.type == 'TEX_IMAGE').image
SKIN_W, SKIN_H = skin_image.size
mesh = body.data
# Solid dark under the hair, plus a faint band just outside the hairline that softens its cut edge.
scalp, _ = rasterize(mesh, [max(hair_mask(v.co, .004, .005), .28*hair_mask(v.co, .005, -.003)) for v in mesh.vertices], SKIN_W, SKIN_H,
                     keep=lambda poly: max(mesh.vertices[i].co.z for i in poly.vertices) > 1.5)
# A soft contact shadow where the swept bangs lie over the forehead (the web renderer has no AO).
bangs = bpy.data.objects['Swept bangs'].data
over = BVHTree.FromPolygons([v.co.copy() for v in bangs.vertices], [p.vertices[:] for p in bangs.polygons])
shadow, _ = rasterize(mesh, [.32*(1-smooth(.003, .010, over.find_nearest(v.co)[3])) if v.co.z > 1.62 and v.co.y < -.06 else 0 for v in mesh.vertices],
                      SKIN_W, SKIN_H, keep=lambda poly: max(mesh.vertices[i].co.z for i in poly.vertices) > 1.5, soften=3)
pixels = image_pixels(skin_image); rgb = pixels[:, :, :3]
rgb *= 1 - shadow[:, :, None]
rgb += (np.array([.05, .038, .032], np.float32) - rgb)*(scalp*.85)[:, :, None]
mouth = [v.co for v in mesh.vertices if abs(v.co.x) < .03 and v.co.y < -.12 and 1.53 < v.co.z < 1.575]
mouth_z = sum(c.z for c in mouth)/len(mouth)
lips, _ = rasterize(mesh, [1-smooth(.7, 1.2, math.hypot(v.co.x/.027, (v.co.z-mouth_z)/.016)) if v.co.y < -.13 else 0 for v in mesh.vertices],
                    SKIN_W, SKIN_H, keep=lambda poly: max(mesh.vertices[i].co.z for i in poly.vertices) > 1.5, soften=2)
redness = np.clip((rgb[:, :, 0]-rgb[:, :, 1] - .06)/.08, 0, 1)
rgb *= 1 - (lips*redness)[:, :, None]*np.array([.04, .16, .13], np.float32)
# Even the facial skin (soften blotches, keep fine texture) and bake soft occlusion from her own
# geometry into it: the web renderer has no ambient occlusion, so cheekbones, sockets and jaw need it.
head_only = lambda poly: max(mesh.vertices[i].co.z for i in poly.vertices) > 1.44
face, _ = rasterize(mesh, [smooth(1.47, 1.50, v.co.z)*smooth(.0, -.04, v.co.y) for v in mesh.vertices], SKIN_W, SKIN_H, keep=head_only, soften=8)
low = np.stack([blur(rgb[:, :, c], 6) for c in range(3)], axis=2)
rgb += ((low + (rgb-low)*.45) - rgb)*(face*.85)[:, :, None]
occluders = BVHTree.FromPolygons([v.co.copy() for v in mesh.vertices], [p.vertices[:] for p in mesh.polygons])
rays = np.random.default_rng(5).normal(0, 1, (28, 3)); rays /= np.linalg.norm(rays, axis=1)[:, None]
def occlusion(v):
    if v.co.z < 1.44: return 0.
    n = v.normal; hits = total = 0
    for r in rays:
        d = Vector(r)
        if d.dot(n) < 0: d = -d
        weight = d.dot(n); total += weight
        if occluders.ray_cast(v.co + n*.0006, d, .035)[0] is not None: hits += weight
    return hits/total
ao, _ = rasterize(mesh, [occlusion(v) for v in mesh.vertices], SKIN_W, SKIN_H, keep=head_only, soften=4)
rgb *= 1 - .42*np.clip(ao, 0, 1)[:, :, None]
pixels[:, :, :3] = np.clip(rgb, 0, 1)
skin_image.pixels.foreach_set(pixels.ravel()); skin_image.update()

# Deepen the light-brown irises to a warm dark brown, leaving sclera and pupils unchanged.
eye_image = next(n for n in bpy.data.objects['Eyes'].active_material.node_tree.nodes if n.type == 'TEX_IMAGE').image
width, height = eye_image.size
pixels = image_pixels(eye_image)
rows, columns = np.mgrid[:height, :width]
pupils = (pixels[:, :, :3] @ np.array([.3, .59, .11], np.float32) < .12) & (abs(columns/width-.5) < .35) & (abs(rows/height-.5) < .35)
for half in (columns < width/2, columns >= width/2):
    cy, cx = rows[pupils & half].mean(), columns[pupils & half].mean()
    iris = np.clip((.118 - np.hypot(rows-cy, columns-cx)/width)/.014, 0, 1)[:, :, None]
    pixels[:, :, :3] = np.clip(pixels[:, :, :3]*(1 + (np.array([.62, .52, .46], np.float32)-1)*iris), 0, 1)
eye_image.pixels.foreach_set(pixels.ravel()); eye_image.update()
eye = bpy.data.objects['Eyes'].active_material.node_tree.nodes.get('Principled BSDF')
eye.inputs['Roughness'].default_value = .14
eye.inputs['Specular IOR Level'].default_value = .5
eye.inputs['Coat Weight'].default_value = .45
eye.inputs['Coat Roughness'].default_value = .08

dress(bpy.data.objects['Dark blazer'], rig, full_mesh, body, human.SOURCE/'system/clothes/male_elegantsuit01/male_elegantsuit01_diffuse.png')

originals = [o for o in bpy.data.collections['Player'].objects if o.type == 'MESH']
for obj in originals:
    if obj.name in ['Face, neck and hands', 'Eyes', 'Dark blazer', 'Silk blouse']:
        mod = obj.modifiers.new('Portrait smoothing', 'SUBSURF'); mod.levels = 1; mod.render_levels = 1

# Bake the shared morph set, so performance.js drives Mei like every other character.
exported = bake(rig, originals, morph_poses(poser, resting_lid=resting_lid), character_name)
export(exported, out, 'MakeHuman CC0 anatomy, skin and suit; original GlobeGlotter hair, blouse and pin. See LICENSE.txt.', cutout=('Sleek hair', 'Swept bangs'))
if settings.portrait: render_portrait(exported, settings.portrait)
print('EXPORTED', out)

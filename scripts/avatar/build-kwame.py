"""Build Kwame from MakeHuman anatomy, with original hair and clothing.
blender --factory-startup -b -P scripts/avatar/build-kwame.py -- SOURCE_DIRECTORY OUTPUT.glb [PORTRAIT.png]
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
from avatar_textures import clamp, smooth, blur, rasterize, image_pixels, new_image, normal_map, splat
from avatar_rig import Poser, morph_poses, bake, export, render_portrait
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('source'); parser.add_argument('output'); parser.add_argument('portrait', nargs='?')
settings = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
character_name = 'Kwame'
human.SOURCE = Path(settings.source).resolve()
out = Path(settings.output).resolve()
bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)

# Early forties: blend the young and old MakeHuman macro targets (age 25 -> 0, 90 -> 1),
# then a defined jaw and cheekbones, fuller cheeks and a slightly shorter face.
OLD = .27
sculpt = [
    ('african-male-young.target', 1-OLD), ('african-male-old.target', OLD),
    ('universal-male-young-averagemuscle-averageweight.target', 1-OLD),
    ('universal-male-old-averagemuscle-averageweight.target', OLD),
    ('head-square.target', .22), ('head-scale-vert-decr.target', .12), ('forehead-scale-vert-decr.target', .15),
    ('chin-width-incr.target', .22), ('chin-prominent-incr.target', .12), ('chin-height-decr.target', .08),
    ('nose-scale-vert-decr.target', .10), ('neck-scale-horiz-incr.target', .25),
    ('mouth-upperlip-volume-incr.target', .08), ('mouth-lowerlip-volume-incr.target', .12),
]
for side in ['l', 'r']:
    sculpt += [(side+'-cheek-bones-incr.target', .30), (side+'-cheek-volume-incr.target', .15),
               (side+'-eye-bag-decr.target', .30), (side+'-eye-scale-incr.target', .04)]
rig = human.build_character(targets=sculpt, eye_color='brownlight', height_metres=1.72,
    skin_path=human.SOURCE/'system/skins/middleage_african_male/middleage_darkskinned_male_diffuse.png',
    asset_specs=[('Tweed blazer', 'clothes/male_elegantsuit01', 'male_elegantsuit01', .8, False),
                 ('Eyebrows', 'eyebrows/eyebrow003', 'eyebrow003', .85, True),
                 ('Eyelashes', 'eyelashes/eyelashes01', 'eyelashes01', .8, True),
                 ('Teeth', 'teeth/teeth_base', 'teeth_base', .4, False),
                 ('Tongue', 'tongue/tongue01', 'tongue01', .65, False)])
body = bpy.data.objects['Face, neck and hands']

# ---------------------------------------------------------------------------------
# Hair follows the skin texture's own painted close crop; the beard and sideburns are
# shaped here. theta is 0 at the face, 90 at the ears and 180 at the nape.
HEAD_Y = -.05
def theta(x, y): return math.degrees(math.atan2(abs(x), -(y-HEAD_Y)))
head_skin = BVHTree.FromPolygons([v.co.copy() for v in body.data.vertices], [p.vertices[:] for p in body.data.polygons])
def skull_x(y, z):
    """Outer skin x at (y, z), cast from inside the head so the ear never interferes."""
    hit = head_skin.ray_cast(Vector((0, y, z)), Vector((1, 0, 0)), .2)[0]
    return hit.x if hit is not None else .07
def ear(x, y, z):
    return 75 < theta(x, y) < 125 and 1.56 < z < 1.675 and abs(x) > skull_x(y, z) + .004
def hair_allowed(x, y, z):
    """Where the painted scalp counts as hair: never the brows, eyes or ears."""
    if ear(x, y, z): return 0.
    return smooth(1.595, 1.61, z)*max(smooth(45, 55, theta(x, y)), smooth(1.675, 1.69, z))
# A classic tapered close crop; the painted hairline adds natural irregularity where it is lower.
CROP = ([0, 20, 32, 44, 56, 62, 70, 80, 84, 95, 110, 116, 125, 150, 180],
        [1.703, 1.704, 1.705, 1.698, 1.678, 1.660, 1.600, 1.600, 1.640, 1.650, 1.645, 1.600, 1.565, 1.545, 1.535])
def crop(x, y, z):
    edge = float(np.interp(theta(x, y), *CROP)); return smooth(edge-.002, edge+.004, z)
BEARD_TOP = ([0, 10, 16, 22, 30, 42, 55, 65, 72, 80], [1.581, 1.580, 1.575, 1.571, 1.574, 1.584, 1.594, 1.601, 1.604, 1.605])
BEARD_BOTTOM = ([0, 20, 40, 55, 65, 75, 85], [1.506, 1.508, 1.515, 1.530, 1.549, 1.568, 1.586])
def beard(x, y, z):
    t = theta(x, y)
    top = float(np.interp(t, *BEARD_TOP)); bottom = float(np.interp(t, *BEARD_BOTTOM))
    inside = smooth(top+.003, top-.003, z)*smooth(bottom-.004, bottom+.004, z)*smooth(86, 80, t)
    # Short sideburns join the beard to the cropped hair in front of the ears.
    sideburn = smooth(64, 69, t)*smooth(84, 80, t)*smooth(1.598, 1.604, z)*smooth(1.668, 1.658, z)
    lips = 1 - smooth(.86, 1.08, math.hypot(abs(x)/.027, (z-1.561)/.0165)) if y < -.125 else 0
    return max(inside, sideburn)*(1-lips)
def grey_rate(x, y, z):
    """A touch of grey: silver strands at the temples and a few at the chin."""
    t = theta(x, y)
    temples = smooth(48, 58, t)*smooth(104, 92, t)*smooth(1.598, 1.615, z)*smooth(1.70, 1.68, z)
    chin = smooth(28, 16, t)*smooth(1.540, 1.528, z)
    return .006 + .16*temples + .05*chin

# ---------------------------------------------------------------------------------
poser = Poser(rig)
rotate, expression, both = poser.rotate, poser.expression, poser.both
poser.relax_arms()

# ---------------------------------------------------------------------------------
# Close-cropped hair and a groomed short beard: one thin shell grown from the scalp and
# jaw skin. It copies the skin's UVs and bone weights, so speech and expressions move it;
# an alpha cutout from the painted hairline and beard shape gives a natural, soft edge.
skin_image = next(n for n in bpy.data.materials['Skin'].node_tree.nodes if n.type == 'TEX_IMAGE').image
SKIN_W, SKIN_H = skin_image.size
skin_luminance = blur(image_pixels(skin_image)[:, :, :3].mean(2), 5)
painted_hair = np.clip((.185-skin_luminance)/.06, 0, 1); painted_hair = painted_hair*painted_hair*(3-2*painted_hair)
mesh = body.data; skin_uv = mesh.uv_layers.active.data
hair_value = np.zeros(len(mesh.vertices), np.float32)
for poly in mesh.polygons:
    for loop, index in zip(poly.loop_indices, poly.vertices):
        u, v = skin_uv[loop].uv
        hair_value[index] = max(hair_value[index], painted_hair[min(SKIN_H-1, int(v*SKIN_H)), min(SKIN_W-1, int(u*SKIN_W))])
allowed = np.array([hair_allowed(*v.co) for v in mesh.vertices], np.float32)
shape = np.array([crop(*v.co) for v in mesh.vertices], np.float32)
painted_value = hair_value*allowed
hair_value = shape*allowed
beard_value = np.array([beard(*v.co) for v in mesh.vertices], np.float32)
cover = np.maximum(hair_value, beard_value)
chosen = [p for p in mesh.polygons if max(cover[i] for i in p.vertices) > .05 and not any(ear(*mesh.vertices[i].co) for i in p.vertices)]
used = sorted({i for p in chosen for i in p.vertices}); lookup = {old: new for new, old in enumerate(used)}
def thickness(i):
    z = mesh.vertices[i].co.z
    return .0004 + max((.0010 + .0026*smooth(1.66, 1.72, z))*smooth(.15, .95, hair_value[i]), .0010*smooth(.3, .9, beard_value[i]))
coords = [mesh.vertices[i].co + mesh.vertices[i].normal*thickness(i) for i in used]
data = bpy.data.meshes.new('Close-cropped hair and beard')
data.from_pydata(coords, [], [[lookup[i] for i in p.vertices] for p in chosen]); data.update()
# The shell's texture is the cropped skin-UV region it covers.
corners = [skin_uv[l].uv.copy() for p in chosen for l in p.loop_indices]
u0, v0 = min(c.x for c in corners)-.002, min(c.y for c in corners)-.002
u1, v1 = max(c.x for c in corners)+.002, max(c.y for c in corners)+.002
layer = data.uv_layers.new(name='UVMap')
for poly, source in zip(data.polygons, chosen):
    poly.use_smooth = True
    for loop, source_loop in zip(poly.loop_indices, source.loop_indices):
        u, v = skin_uv[source_loop].uv; layer.data[loop].uv = ((u-u0)/(u1-u0), (v-v0)/(v1-v0))
hair = bpy.data.objects.new('Close-cropped hair and beard', data); bpy.data.collections['Player'].objects.link(hair); hair.parent = rig
names = {g.index: g.name for g in body.vertex_groups}; groups = {}
for new, old in enumerate(used):
    for g in mesh.vertices[old].groups:
        group = groups.get(g.group) or groups.setdefault(g.group, hair.vertex_groups.new(name=names[g.group]))
        group.add([new], g.weight, 'REPLACE')
hair.modifiers.new('Skin deformation', 'ARMATURE').object = rig

# Tight coils live in the normal map; colour is near-black with sparse silver strands.
TW = int(round((u1-u0)*SKIN_W*.75/4))*4; TH = int(round((v1-v0)*SKIN_H*.75/4))*4
columns = np.clip(((u0 + (np.arange(TW)+.5)/TW*(u1-u0))*SKIN_W).astype(int), 0, SKIN_W-1)
rows = np.clip(((v0 + (np.arange(TH)+.5)/TH*(v1-v0))*SKIN_H).astype(int), 0, SKIN_H-1)
hair_field = painted_hair[rows[:, None], columns[None, :]]
shell_allowed, _ = rasterize(data, [allowed[i] for i in used], TW, TH, soften=0)
shell_shape, _ = rasterize(data, [shape[i] for i in used], TW, TH, soften=0)
shell_beard, _ = rasterize(data, [beard_value[i] for i in used], TW, TH, soften=0)
shell_grey, _ = rasterize(data, [grey_rate(*mesh.vertices[i].co) for i in used], TW, TH, soften=0)
rng = np.random.default_rng(31)
count = int(TW*TH/5.5)
points = rng.random((count, 2))*np.array([TW, TH])
height_field = splat((TH, TW), points, .9, rng.uniform(.6, 1.2, count))
height_field /= np.percentile(height_field, 99)
coverage = np.maximum(shell_shape*shell_allowed, shell_beard)
edge_noise = blur(rng.random((TH, TW)).astype(np.float32), 1)
hair_alpha = ((shell_shape*shell_allowed + .3*(edge_noise-.5) + .15*(np.clip(height_field, 0, 1.2)-.6)) > .5)
# The beard is solid inside and thins to stubble over its last few millimetres.
beard_alpha = (shell_beard + .55*(rng.random((TH, TW)).astype(np.float32)-.5)) > .62
alpha = (hair_alpha | beard_alpha).astype(np.float32)
beard_tone = np.clip(shell_beard*2, 0, 1)*(1-np.clip(shell_shape*shell_allowed*2, 0, 1))
picked = rng.random(count) < shell_grey[np.clip(points[:, 1].astype(int), 0, TH-1), np.clip(points[:, 0].astype(int), 0, TW-1)]
silver = splat((TH, TW), points[picked], .75)
rgb = np.array([.020, .016, .013], np.float32) + np.round(np.clip(height_field, 0, 1.2)*4)[:, :, None]/4*np.array([.022, .018, .015], np.float32)
rgb += (np.array([.075, .058, .048], np.float32) - np.array([.020, .016, .013], np.float32))*beard_tone[:, :, None]
rgb += (np.array([.30, .29, .28], np.float32) - rgb)*np.clip(silver, 0, .9)[:, :, None]
coil_color = new_image('Close coils', rgb, alpha=alpha)
coil_normal = new_image('Close coil normals', normal_map(blur(height_field, 1), 2.5), data=True)
hair_material = bpy.data.materials.new('Close-cropped hair'); hair_material.use_nodes = True
nodes = hair_material.node_tree.nodes; links = hair_material.node_tree.links; shader = nodes.get('Principled BSDF')
texture = nodes.new('ShaderNodeTexImage'); texture.image = coil_color
links.new(texture.outputs['Color'], shader.inputs['Base Color']); links.new(texture.outputs['Alpha'], shader.inputs['Alpha'])
normal_texture = nodes.new('ShaderNodeTexImage'); normal_texture.image = coil_normal
normal_node = nodes.new('ShaderNodeNormalMap'); normal_node.inputs['Strength'].default_value = .35
links.new(normal_texture.outputs['Color'], normal_node.inputs['Color']); links.new(normal_node.outputs['Normal'], shader.inputs['Normal'])
shader.inputs['Roughness'].default_value = .74; shader.inputs['Specular IOR Level'].default_value = .22
hair_material.surface_render_method = 'DITHERED'
data.materials.append(hair_material)

# ---------------------------------------------------------------------------------
# Original glasses: rounded tortoiseshell frames, a keyhole bridge and straight temples
# over the ears. Frames only (no lens glass), rigidly attached to the head.
FRAME_Y, WRAP, TILT = -.1528, 3.2, .12
LENS_X, LENS_Z, LENS_A, LENS_B = .032, 1.6255, .0235, .0175
def frame_y(x, z): return FRAME_Y + WRAP*x*x - TILT*(z-LENS_Z)
def tube(points, radii, *, outward=None, closed=False, sides=10):
    """Sweep an elliptical section (in-plane radius, depth radius) along points."""
    coords, faces, uvs = [], [], []
    n = len(points); length = 0
    frame = None
    for i, p in enumerate(points):
        a = points[(i-1) % n] if closed or i > 0 else p; b = points[(i+1) % n] if closed or i < n-1 else p
        tangent = (b-a).normalized()
        if outward:
            normal = outward(p, tangent)
        else:
            if frame is None: frame = Vector((0, 0, 1)) - tangent*tangent.z
            normal = (frame - tangent*frame.dot(tangent)).normalized(); frame = normal
        binormal = tangent.cross(normal).normalized()
        if i: length += (p-points[i-1]).length
        r1, r2 = radii(i, p)
        for k in range(sides):
            phi = 2*math.pi*k/sides
            coords.append(p + normal*math.cos(phi)*r1 + binormal*math.sin(phi)*r2)
            uvs.append((length/.03, k/sides))
    rings = n if closed else n-1
    for i in range(rings):
        for k in range(sides):
            a, b = i*sides + k, i*sides + (k+1) % sides
            c, d = ((i+1) % n)*sides + (k+1) % sides, ((i+1) % n)*sides + k
            faces.append((a, b, c, d))
    if not closed:
        faces.append(tuple(range(sides))[::-1]); faces.append(tuple((n-1)*sides + k for k in range(sides)))
    return coords, faces, uvs

parts = []
for sign in (1, -1):
    # Rims: superellipse lenses, slightly flatter along the top, heavier upper rim.
    rim = []
    for k in range(96):
        t = 2*math.pi*k/96
        c, s = math.cos(t), math.sin(t)
        x = LENS_X + LENS_A*math.copysign(abs(c)**(2/3.6), c)
        zz = LENS_Z + LENS_B*math.copysign(abs(s)**(2/(4.2 if s > 0 else 3.2)), s)
        rim.append(Vector((sign*x, frame_y(x, zz), zz)))
    center = Vector((sign*LENS_X, frame_y(LENS_X, LENS_Z), LENS_Z))
    def outward(p, tangent, center=center):
        o = p-center; o -= tangent*o.dot(tangent); return o.normalized()
    parts.append(tube(rim, lambda i, p: (.0021 + .0011*clamp((p.z-LENS_Z)/LENS_B), .0016), outward=outward, closed=True))
    # End piece from the outer rim back to the hinge, then the temple to behind the ear.
    hinge = Vector((sign*.0625, frame_y(.0625, 1.638)+.004, 1.638))
    end = [Vector((sign*(LENS_X+LENS_A-.001), frame_y(LENS_X+LENS_A, 1.638), 1.638)), hinge]
    parts.append(tube(end, lambda i, p: (.0023, .0017)))
    temple = []
    ear = Vector((skull_x(-.05, 1.648)+.0028, -.05, 1.648))
    for k in range(25):
        f = k/24
        y = hinge.y + (ear.y-hinge.y)*f; zz = hinge.z + (ear.z-hinge.z)*f
        x = abs(hinge.x) + (ear.x-abs(hinge.x))*f
        x = max(x, skull_x(y, zz)+.0035)
        temple.append(Vector((sign*x, y, zz)))
    for y, zz in [(-.045, 1.648), (-.040, 1.643), (-.037, 1.636)]:
        temple.append(Vector((sign*(skull_x(y, zz)+.0018), y, zz)))
    tip = len(temple)-1
    parts.append(tube(temple, lambda i, p: (.0016*(1-.45*smooth(tip-3, tip, i)), .0013*(1-.45*smooth(tip-3, tip, i)))))
# Keyhole bridge arching between the inner rims.
bridge = []
for k in range(13):
    x = -.0105 + .021*k/12
    zz = 1.6345 + .0035*math.cos(math.pi*x/.021)
    bridge.append(Vector((x, frame_y(x, zz)-.0005, zz)))
parts.append(tube(bridge, lambda i, p: (.0015, .0014)))
coords, faces, uvs = [], [], []
for c, f, uv in parts:
    offset = len(coords); coords += c; uvs += uv; faces += [tuple(i+offset for i in face) for face in f]
data = bpy.data.meshes.new('Glasses'); data.from_pydata(coords, [], faces); data.update()
layer = data.uv_layers.new(name='UVMap')
for poly in data.polygons:
    poly.use_smooth = True
    for loop, index in zip(poly.loop_indices, poly.vertices): layer.data[loop].uv = uvs[index]
glasses = bpy.data.objects.new('Glasses', data); bpy.data.collections['Player'].objects.link(glasses); glasses.parent = rig
glasses.vertex_groups.new(name='head').add(list(range(len(data.vertices))), 1, 'REPLACE')
glasses.modifiers.new('Skin deformation', 'ARMATURE').object = rig
tortoise_noise = blur(rng.random((64, 256)).astype(np.float32), 4)
tortoise_noise = (tortoise_noise-tortoise_noise.min())/(tortoise_noise.max()-tortoise_noise.min())
amber = np.clip((tortoise_noise-.45)*3, 0, 1)
tortoise = np.array([.035, .016, .007], np.float32) + amber[:, :, None]*np.array([.36, .17, .05], np.float32)
frame_material = bpy.data.materials.new('Tortoiseshell frames'); frame_material.use_nodes = True
nodes = frame_material.node_tree.nodes; shader = nodes.get('Principled BSDF')
texture = nodes.new('ShaderNodeTexImage'); texture.image = new_image('Tortoiseshell', tortoise)
frame_material.node_tree.links.new(texture.outputs['Color'], shader.inputs['Base Color'])
shader.inputs['Roughness'].default_value = .3; shader.inputs['Specular IOR Level'].default_value = .45
shader.inputs['Coat Weight'].default_value = .25; shader.inputs['Coat Roughness'].default_value = .15
data.materials.append(frame_material)

# ---------------------------------------------------------------------------------
# A warm, closed-mouth resting smile with relaxed brows and lightly lowered lids.
resting_lid = .06
for side in ['Left', 'Right']:
    expression('Mouth'+side+'PullUp', .32)
    expression(side+'CheekUp', .18)
    expression(side+'InnerBrowUp', .04)
    expression(side+'OuterBrowUp', .06)
    expression(side+'UpperLidClosed', resting_lid)
rotate('head', (0, 1, 0), .02)

for name, tint in [('Skin', (1, .985, .975)), ('Eyebrows', (.05, .04, .035))]:
    mat = bpy.data.materials[name]; nodes = mat.node_tree.nodes; links = mat.node_tree.links
    bsdf = nodes.get('Principled BSDF'); tex = next(n for n in nodes if n.type == 'TEX_IMAGE')
    mix = nodes.new('ShaderNodeMix'); mix.data_type = 'RGBA'; mix.blend_type = 'MULTIPLY'; mix.inputs['Factor'].default_value = 1
    links.new(tex.outputs['Color'], mix.inputs[6]); mix.inputs[7].default_value = (*tint, 1); links.new(mix.outputs[2], bsdf.inputs['Base Color'])
    bsdf.inputs['Roughness'].default_value = .44 if name == 'Skin' else .8
    if name == 'Skin': bsdf.inputs['Specular IOR Level'].default_value = .4
    if name == 'Skin': bsdf.inputs['Subsurface Weight'].default_value = .05

# Darken the skin under the beard (and slightly under the crop) so cutout gaps read as roots.
mask, _ = rasterize(mesh, [max(.6*beard_value[i], .5*hair_value[i], .4*painted_value[i]) for i in range(len(mesh.vertices))], SKIN_W, SKIN_H,
                    keep=lambda poly: max(mesh.vertices[i].co.z for i in poly.vertices) > 1.42)
mask *= .9
follicles = (np.random.default_rng(7).random((SKIN_H, SKIN_W)) > .55).astype(np.float32)
follicles = .6*follicles + .4*blur(follicles, 1)
pixels = image_pixels(skin_image); rgb = pixels[:, :, :3]
rgb *= 1.08  # Lift the skin slightly for the shared web lighting.
rgb *= 1 - mask[:, :, None]*.72
rgb += (rgb*.35 - rgb)*(mask*follicles)[:, :, None]
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
    pixels[:, :, :3] = np.clip(pixels[:, :, :3]*(1 + (np.array([.44, .36, .32], np.float32)-1)*iris), 0, 1)
eye_image.pixels.foreach_set(pixels.ravel()); eye_image.update()
eye = bpy.data.objects['Eyes'].active_material.node_tree.nodes.get('Principled BSDF')
eye.inputs['Roughness'].default_value = .14
eye.inputs['Specular IOR Level'].default_value = .5
eye.inputs['Coat Weight'].default_value = .45
eye.inputs['Coat Roughness'].default_value = .08

from kwame_outfit import dress
dress(bpy.data.objects['Tweed blazer'], rig, human.SOURCE/'system/clothes/male_elegantsuit01/male_elegantsuit01_diffuse.png')

originals = [o for o in bpy.data.collections['Player'].objects if o.type == 'MESH']
for obj in originals:
    if obj.name in ['Face, neck and hands', 'Eyes', 'Tweed blazer', 'Close-cropped hair and beard']:
        mod = obj.modifiers.new('Portrait smoothing', 'SUBSURF'); mod.levels = 1; mod.render_levels = 1

# Bake the shared morph set, so performance.js drives Kwame like every other character.
exported = bake(rig, originals, morph_poses(poser, resting_lid=resting_lid), character_name)
export(exported, out, 'MakeHuman CC0 anatomy, skin and suit; original GlobeGlotter hair, beard, glasses and tweed. See LICENSE.txt.', cutout=('Close-cropped hair',))
if settings.portrait: render_portrait(exported, settings.portrait)
print('EXPORTED', out)

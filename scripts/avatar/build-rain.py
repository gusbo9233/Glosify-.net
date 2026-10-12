"""Build Rain or Rowan from MakeHuman anatomy, with character-specific clothing and hair.
Blender -b --factory-startup -P build-rain.py -- SOURCE_DIRECTORY OUTPUT.glb
See README.md for the pinned source manifest and asset preparation.
"""
from pathlib import Path
import sys, math, json, struct, argparse
import numpy as np
import bpy
import bmesh
from mathutils import Vector, Matrix, Quaternion
sys.path.insert(0, str(Path(__file__).parent))
import human_mesh as human
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('source'); parser.add_argument('output'); parser.add_argument('portrait', nargs='?')
parser.add_argument('--character', choices=['rain', 'rowan'], default='rain')
settings = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
male = settings.character == 'rowan'
character_name = 'Rowan' if male else 'Rain'
human.SOURCE = Path(settings.source).resolve()
out = Path(settings.output).resolve()
bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete(use_global=False)
sculpt = [
    ('caucasian-female-young.target', 1),
    ('universal-female-young-averagemuscle-averageweight.target', 1),
    ('head-oval.target', .65), ('chin-width-decr.target', .3),
    ('chin-height-decr.target', .22), ('chin-bones-decr.target', .3),
    ('mouth-upperlip-volume-incr.target', .32), ('mouth-upperlip-height-incr.target', .24), ('mouth-lowerlip-volume-incr.target', .2)
]
if male:
    # A young-adult face: high cheekbones over lean cheeks, a jaw tapering to a
    # narrower cleft chin, low brows over hooded eyes, a long straight nose and
    # full lips. Fit the eyes, brows and facial rig to this anatomy.
    sculpt = [
        ('caucasian-male-young.target', 1), ('universal-male-young-averagemuscle-averageweight.target', 1),
        ('head-square.target', .10), ('head-invertedtriangular.target', .30), ('head-fat-decr.target', .20),
        ('chin-width-decr.target', .25), ('chin-prominent-incr.target', .20),
        ('chin-cleft-incr.target', .20), ('chin-height-incr.target', .05),
        ('mouth-scale-horiz-incr.target', .10), ('mouth-cupidsbow-incr.target', .18),
        ('mouth-upperlip-volume-incr.target', .30), ('mouth-lowerlip-volume-incr.target', .50),
        ('eyebrows-trans-down.target', .30),
        ('nose-scale-vert-incr.target', .20), ('nose-point-width-decr.target', .30),
        ('nose-width1-decr.target', .10), ('nose-width2-decr.target', .12), ('nose-width3-decr.target', .10)
    ]
for side in ['l', 'r']:
    sculpt += [(side+'-cheek-bones-incr.target', .60 if male else .15),
               (side+'-eye-scale-incr.target', .04 if male else .08)]
    if male:
        sculpt += [(side+'-cheek-volume-decr.target', .35), (side+'-cheek-inner-decr.target', .15),
                   (side+'-cheek-trans-up.target', .16), (side+'-eye-height1-decr.target', .10),
                   (side+'-eye-height2-decr.target', .15), (side+'-eye-corner2-up.target', .12),
                   (side+'-eye-eyefold-down.target', .20)]
rig = human.build_character(targets=sculpt, eye_color='grey' if male else 'brownlight',
    height_metres=1.72, skin_path=human.SOURCE/('system/skins/young_caucasian_male/young_lightskinned_male_diffuse.png' if male else 'system/skins/young_caucasian_female/young_lightskinned_female_diffuse.png'),
    asset_specs=[('Ivory blouse', 'clothes/female_elegantsuit01', 'female_elegantsuit01', .75, False),
                 ('Chestnut hair', 'hair/short02' if male else 'hair/elvs_hazel_hair', 'short02' if male else 'elvs_hazel_hair', .5, True),
                 ('Eyebrows', 'eyebrows/eyebrow009' if male else 'eyebrows/eyebrow001', 'eyebrow009' if male else 'eyebrow001', .85, True),
                 ('Eyelashes', 'eyelashes/eyelashes01', 'eyelashes01', .8, True),
                 ('Teeth', 'teeth/teeth_base', 'teeth_base', .4, False),
                 ('Tongue', 'tongue/tongue01', 'tongue01', .65, False)])

# Hair follows the head, not the shoulders sampled by generic clothing weights.
hair = bpy.data.objects['Chestnut hair']
def clamp(value): return max(0., min(1., value))
if male:
    # Shape the cap into a close-fitting mop: settle the crown and sides nearer the
    # head, drop the fringe to just above the brows (longer and swept to his left),
    # and let the sides fall over the ear tops. Lock cards below break up its surface.
    from mathutils.bvhtree import BVHTree
    head = bpy.data.objects['Face, neck and hands'].data
    skin = BVHTree.FromPolygons([v.co.copy() for v in head.vertices], [p.vertices[:] for p in head.polygons])
    center = Vector((0, -.065, 1.625))
    for vertex in hair.data.vertices:
        co = vertex.co.copy(); x, y, z = co
        outward = (co-center).normalized()
        top = clamp((z-1.64)/.07)
        co -= outward*(.004*top + .002*clamp((abs(x)-.03)/.05)*(1-top))
        front = clamp((-y-.095)/.04)
        low = clamp((1.705-z)/.055)
        lean = .75 + .55*clamp((x+.04)/.09)
        co.z -= .04*front*low*lean
        if abs(x) < .055 and z > 1.642: co.z = max(co.z, 1.642)  # Keep the brows clear.
        co.x += .016*front*low*max(0, 1-abs(x)/.09)
        sides = clamp((abs(x)-.055)/.025)*clamp((1.67-z)/.05)*(1-front)
        co.z -= .008*sides
        co += outward*.001*sides
        vertex.co = co
    # Rest every strand outside the skin, and lay the fringe close on the forehead.
    for vertex in hair.data.vertices:
        location, normal, _, _ = skin.find_nearest(vertex.co)
        x, y, z = vertex.co
        offset = (vertex.co-location).dot(normal)
        fringe = clamp((-y-.095)/.04)*clamp((1.69-z)/.03)
        target = max(.003, offset - max(0, offset-.007)*fringe)
        vertex.co += normal*(target-offset)
    hair.data.update()

    # Original hair cards over the mop: tapered locks grown from the crown whorl
    # forward over the top, down the sides and back, with a dense row along the
    # hairline so the fringe falls to the brows in separate pointed locks.
    rng = np.random.default_rng(11)
    shell = BVHTree.FromPolygons([v.co.copy() for v in hair.data.vertices], [p.vertices[:] for p in hair.data.polygons])
    # Lock atlas: four vertical strips with roots at the top (v=1). Each strip holds
    # one to three solid locks tapering to pointed, lightly frayed tips; strand detail
    # lives in colour streaks so the alpha cutout keeps clean edges in the browser.
    size, strips = 1024, 4
    strip_width = size//strips
    alpha = np.zeros((size, size), np.float32)
    rows, columns = np.mgrid[:size, :size].astype(np.float32)
    along = rows/(size-1)  # 0 at the tip row, 1 at the root row
    def smooth(a, b, t):
        t = np.clip((t-a)/(b-a), 0, 1); return t*t*(3-2*t)
    for s in range(strips):
        x0 = s*strip_width
        count = [1, 2, 2, 3][s]
        for k in range(count):
            cx = x0 + strip_width*(k+.5)/count + rng.normal(0, strip_width*.02)
            tip = rng.uniform(0, .22)
            t = np.clip((along-tip)/(1-tip), 0, 1)
            half = strip_width/count*.46*(.08 + .92*t**.55)
            middle = cx + rng.uniform(-6, 6)*(1-t)**2
            alpha = np.maximum(alpha, smooth(1.2, -1.2, abs(columns-middle)-half)*(along > tip)*(abs(columns-x0-strip_width/2) < strip_width/2))
        for _ in range(4):  # A few stray strands break off past the tips.
            cx = x0 + rng.uniform(.15, .85)*strip_width
            index = np.nonzero(rows[:, 0] >= rng.uniform(0, .12)*(size-1))[0]
            x = cx + rng.normal(0, 2)*(1-along[index, 0])*8
            for dx in (-1, 0, 1):
                column = np.clip(np.round(x).astype(int)+dx, x0, x0+strip_width-1)
                alpha[index, column] = np.maximum(alpha[index, column], np.clip(1.4-abs(column-x)/.9, 0, 1)*(along[index, 0] < .45))
    # Streaks along each lock read as strands, drifting slightly towards the tips.
    streaks = sum(np.interp(np.arange(size), np.linspace(0, size, f*4), rng.normal(0, 1, f*4))/np.sqrt(f) for f in (3, 7, 17, 41, 97))
    streaks = (streaks-streaks.min())/(streaks.max()-streaks.min())
    drift = (1-along)*rng.normal(0, 4)
    shade = .7 + .9*np.interp((columns+drift).ravel() % size, np.arange(size), streaks).reshape(size, size)
    # Fade in unevenly at the root so card tops never show as straight edges.
    start = 1 - .16*np.interp(np.arange(size), np.linspace(0, size, 64), rng.uniform(.5, 1, 64))
    alpha *= np.clip((start[None, :]-along)/.04, 0, 1)
    pixels = np.zeros((size, size, 4), np.float32)
    pixels[:, :, :3] = np.array([.14, .105, .085], np.float32)*shade[:, :, None]
    pixels[:, :, 3] = alpha
    strands = bpy.data.images.new('Hair lock strands', size, size, alpha=True)
    strands.pixels.foreach_set(pixels.ravel()); strands.pack()

    whorl = Vector((-.012, .02, 1.765))
    def surface(point):
        location, normal, _, distance = shell.find_nearest(point)
        return location, normal if normal.dot(location-center) > 0 else -normal, distance
    def flow(point, normal):
        # Away from the whorl, swept to his left at the front, falling lower down.
        d = point-whorl; d.z *= .35; d = d.normalized()
        d += Vector((.45*clamp((-point.y-.06)/.06), 0, 0))
        d += Vector((0, 0, -1))*(.25 + .9*clamp((1.70-point.z)/.06))
        d -= normal*d.dot(normal)
        return d.normalized() if d.length > 1e-6 else Vector((0, 0, -1))
    def limit(point):
        # Locks end just above the brows in front, above the ears at the temples,
        # over the ear tops at the sides and at the nape behind.
        front = clamp((-point.y-.10)/.03)*clamp((.075-abs(point.x))/.02)
        z = 1.60 + (1.505-1.60)*clamp((point.y+.02)/.06)
        z += (1.625-z)*clamp((-point.y-.04)/.03)*(1-front)
        return z + (1.652-z)*front
    shell_points = [v.co.copy() for v in hair.data.vertices]
    roots = []
    hairline = [v for v in shell_points if 1.655 < v.z < 1.705 and v.y < -.115 and abs(v.x) < .07]
    for i in rng.permutation(len(hairline)):
        c = hairline[i] + Vector(rng.normal(0, .003, 3))
        if all((c-r).length > .007 for r, _ in roots): roots.append((c, (.05, .085)))
    upper = [v for v in shell_points if v.z > 1.57]
    for i in rng.permutation(len(upper)):
        c = upper[i] + Vector(rng.normal(0, .004, 3))
        if (c-whorl).length < .03: continue  # Direction is undefined at the whorl.
        if all((c-r).length > .013 for r, _ in roots): roots.append((c, (.07, .12)))
    corners, quads, uvs = [], [], []
    step = .004
    for root, lengths in roots:
        location, normal, _ = surface(root)
        lift = rng.uniform(.001, .003)
        point = location + normal*lift
        points = [point.copy()]
        target = rng.uniform(*lengths)
        travelled = 0
        while travelled < target and len(points) < 60:
            location, normal, distance = surface(point)
            on_shell = distance < lift + .004
            d = flow(point, normal) if on_shell else (flow(point, normal)*.35 + Vector((0, 0, -1))*.65).normalized()
            point = point + d*step
            location, normal, distance = surface(point)
            if distance < lift + .004 and on_shell: point = location + normal*lift
            location, normal, _, _ = skin.find_nearest(point)
            offset = (point-location).dot(normal)
            if offset < .003: point += normal*(.003-offset)
            travelled += step
            points.append(point.copy())
            if point.z < limit(point): break
        if len(points) < 6: continue
        width = rng.uniform(.010, .020)
        strip = rng.integers(0, strips)
        first = len(corners)
        for k, q in enumerate(points):
            t = k/(len(points)-1)
            tangent = (points[min(k+1, len(points)-1)]-points[max(k-1, 0)]).normalized()
            _, normal, _ = surface(q)
            across = normal.cross(tangent).normalized()*width*(1-.65*t)/2
            q = q + normal*.0015*t*t  # Tips lift slightly away from the mop.
            corners += [q-across, q+across]
            uvs += [(strip/strips+.01, 1-.98*t), ((strip+1)/strips-.01, 1-.98*t)]
        quads += [(first+2*k, first+2*k+1, first+2*k+3, first+2*k+2) for k in range(len(points)-1)]
    mesh = bpy.data.meshes.new('Hair locks')
    mesh.from_pydata([tuple(c) for c in corners], [], quads); mesh.update()
    layer = mesh.uv_layers.new(name='UVMap')
    for poly in mesh.polygons:
        poly.use_smooth = True
        for loop, index in zip(poly.loop_indices, poly.vertices): layer.data[loop].uv = uvs[index]
    lock_material = bpy.data.materials.new('Hair locks'); lock_material.use_nodes = True
    nodes = lock_material.node_tree.nodes; bsdf = nodes.get('Principled BSDF')
    texture = nodes.new('ShaderNodeTexImage'); texture.image = strands
    lock_material.node_tree.links.new(texture.outputs['Color'], bsdf.inputs['Base Color'])
    lock_material.node_tree.links.new(texture.outputs['Alpha'], bsdf.inputs['Alpha'])
    bsdf.inputs['Roughness'].default_value = .7; bsdf.inputs['Specular IOR Level'].default_value = .35
    lock_material.surface_render_method = 'DITHERED'; lock_material.use_backface_culling = False
    mesh.materials.append(lock_material)
    locks = bpy.data.objects.new('Hair locks', mesh)
    bpy.data.collections['Player'].objects.link(locks); locks.parent = rig
    locks.vertex_groups.new(name='head').add(list(range(len(mesh.vertices))), 1, 'REPLACE')
    locks.modifiers.new('Skin deformation', 'ARMATURE').object = rig

    # Heavy dark brows, tapered at the ends rather than a uniform strip.
    brows = bpy.data.objects['Eyebrows']
    center_z = sum(v.co.z for v in brows.data.vertices)/len(brows.data.vertices)
    for vertex in brows.data.vertices:
        vertex.co.z = center_z + (vertex.co.z-center_z)*1.35
    brows.data.update()
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
def both(unit, weight=1):
    for side in ['Left','Right']: expression(side+unit, weight)
def shoulders(angle):
    # Raise each collarbone about the sternum; the arm and blouse follow.
    for side in ['L','R']:
        bone=rig.pose.bones['clavicle.'+side]
        rotate(bone.name,(0,1,0),-math.copysign(angle,bone.tail.x-bone.head.x))

# Closed-mouth resting smile: opening the lips and jaw belongs only to speech.
# Rowan rests with a steady gaze: low brows and lightly hooded, narrowed lids.
resting_lid = .12 if male else 0
for side in ['Left','Right']:
    expression('Mouth'+side+'PullUp',.37 if male else .28)
    expression(side+'CheekUp',.23 if male else .08)
    expression(side+'InnerBrowUp',.025 if male else .08)
    expression(side+'OuterBrowUp',.05 if male else .12)
    if male:
        expression(side+'BrowDown',.3)
        expression(side+'UpperLidClosed',resting_lid)
        expression(side+'LowerLidUp',.35)
rotate('head',(0,1,0),.025)

# Keep high-resolution skin, soft highlights and textured chestnut hair.
for name, tint in [('Skin', (.96,.90,.84) if male else (.94,.86,.76)),
                   ('Eyebrows', (.05,.04,.035) if male else (.42,.29,.19))]:
    mat = bpy.data.materials[name]; nodes=mat.node_tree.nodes; links=mat.node_tree.links
    bsdf=nodes.get('Principled BSDF'); tex=next(n for n in nodes if n.type=='TEX_IMAGE')
    mix=nodes.new('ShaderNodeMix'); mix.data_type='RGBA'; mix.blend_type='MULTIPLY'; mix.inputs['Factor'].default_value=1
    links.new(tex.outputs['Color'],mix.inputs[6]); mix.inputs[7].default_value=(*tint,1); links.new(mix.outputs[2],bsdf.inputs['Base Color'])
    bsdf.inputs['Roughness'].default_value=.58 if name=='Skin' else .8
    if name=='Skin': bsdf.inputs['Subsurface Weight'].default_value=.06

def blur(values, radius):
    # Separable box blur over a 2D array, clamped at the edges.
    for axis in (0, 1):
        total = np.cumsum(np.pad(values, [(radius+1, radius) if i == axis else (0, 0) for i in range(2)], mode='edge'), axis=axis)
        values = (np.take(total, range(2*radius+1, total.shape[axis]), axis=axis)
                  - np.take(total, range(0, total.shape[axis]-2*radius-1), axis=axis))/(2*radius+1)
    return values

if male:
    # Paint light stubble into the skin texture: upper lip, chin, jawline, lower
    # cheeks and sideburns, with a short neck beard and clean lips. A per-vertex
    # mask from the sculpted head is rasterised into UV space, softened only
    # within painted skin so UV seams stay invisible, then shaded with fine follicles.
    def smooth(a, b, t):
        t = clamp((t-a)/(b-a)); return t*t*(3-2*t)
    def lerp(a, b, t): return a + (b-a)*clamp(t)
    def beard(x, y, z):
        ax = abs(x)
        edge = 1.578 if ax < .018 else lerp(1.578, 1.572, (ax-.018)/.017) if ax < .035 else lerp(1.572, 1.606, (ax-.035)/.033)
        edge += .02*smooth(-.075, -.05, y)  # Sideburns in front of the ears.
        upper = 1 - smooth(edge-.014, edge+.004, z)
        jaw = 1.484 + .4*max(0., ax-.012)  # None down the sides of the neck.
        lower = smooth(jaw-.012, jaw+.008, z)
        front = smooth(-.03, -.062, y)
        lips = 1 - smooth(.82, 1.08, math.hypot(ax/.026, (z-1.5525)/.0158)) if y < -.125 else 0
        density = .8 + .2*smooth(.055, .02, ax)
        neck = lerp(.45, 1, (z-1.472)/.03)
        return upper*lower*front*(1-lips)*density*neck
    head = bpy.data.objects['Face, neck and hands'].data
    skin_image = next(n for n in bpy.data.materials['Skin'].node_tree.nodes if n.type=='TEX_IMAGE').image
    width, height = skin_image.size
    values = [beard(*v.co) for v in head.vertices]
    uv = head.uv_layers.active.data
    mask = np.zeros((height, width), np.float32); cover = np.zeros((height, width), np.float32)
    for poly in head.polygons:
        if max(head.vertices[i].co.z for i in poly.vertices) < 1.42: continue
        for k in range(1, len(poly.vertices)-1):
            corners = [0, k, k+1]
            p = np.array([(uv[poly.loop_indices[c]].uv[0]*width, uv[poly.loop_indices[c]].uv[1]*height) for c in corners])
            m = np.array([values[poly.vertices[c]] for c in corners])
            x0, y0 = np.maximum(np.floor(p.min(0)).astype(int), 0)
            x1, y1 = np.minimum(np.ceil(p.max(0)).astype(int), (width-1, height-1))
            area = (p[1,1]-p[2,1])*(p[0,0]-p[2,0]) + (p[2,0]-p[1,0])*(p[0,1]-p[2,1])
            if x1 < x0 or y1 < y0 or abs(area) < 1e-9: continue
            gx, gy = np.meshgrid(np.arange(x0, x1+1)+.5, np.arange(y0, y1+1)+.5)
            a = ((p[1,1]-p[2,1])*(gx-p[2,0]) + (p[2,0]-p[1,0])*(gy-p[2,1]))/area
            b = ((p[2,1]-p[0,1])*(gx-p[2,0]) + (p[0,0]-p[2,0])*(gy-p[2,1]))/area
            inside = (a >= -.02) & (b >= -.02) & (1-a-b >= -.02)
            region = mask[y0:y1+1, x0:x1+1]
            region[inside] = np.maximum(region[inside], (a*m[0] + b*m[1] + (1-a-b)*m[2])[inside])
            cover[y0:y1+1, x0:x1+1][inside] = 1
    mask = np.clip(blur(mask, 5)/np.maximum(blur(cover, 5), 1e-3), 0, 1)*cover*.45
    follicles = (np.random.default_rng(7).random((height, width)) > .6).astype(np.float32)
    follicles = .6*follicles + .4*blur(follicles, 1)
    pixels = np.array(skin_image.pixels[:], np.float32).reshape(height, width, 4)
    rgb = pixels[:, :, :3]
    rgb *= 1 - mask[:, :, None]*(1-np.array([.80, .81, .86], np.float32))
    rgb += (rgb*np.array([.36, .36, .42], np.float32) - rgb)*(mask*follicles)[:, :, None]
    skin_image.pixels.foreach_set(pixels.ravel()); skin_image.update()

# Keep Rowan's strand texture under a near-black espresso tint; Rain retains her warm base.
hair_material=bpy.data.materials['Chestnut hair']
hair_shader=hair_material.node_tree.nodes.get('Principled BSDF')
for link in list(hair_shader.inputs['Base Color'].links): hair_material.node_tree.links.remove(link)
if male:
    texture=next(n for n in hair_material.node_tree.nodes if n.type=='TEX_IMAGE')
    tint=hair_material.node_tree.nodes.new('ShaderNodeMix')
    tint.data_type='RGBA'; tint.blend_type='MULTIPLY'; tint.inputs['Factor'].default_value=1
    tint.inputs[7].default_value=(.20,.17,.15,1)
    hair_material.node_tree.links.new(texture.outputs['Color'],tint.inputs[6])
    hair_material.node_tree.links.new(tint.outputs[2],hair_shader.inputs['Base Color'])
else:
    hair_shader.inputs['Base Color'].default_value=(.045,.022,.013,1)
hair_shader.inputs['Specular IOR Level'].default_value=.3
hair_shader.inputs['Roughness'].default_value=.62 if male else .46

if male:
    # Cool the grey irises toward a pale blue, leaving sclera and pupils unchanged.
    eye_image = next(n for n in bpy.data.objects['Eyes'].active_material.node_tree.nodes if n.type=='TEX_IMAGE').image
    width, height = eye_image.size
    pixels = np.array(eye_image.pixels[:], np.float32).reshape(height, width, 4)
    rows, columns = np.mgrid[:height, :width]
    pupils = (pixels[:, :, :3] @ np.array([.3, .59, .11], np.float32) < .12) & (abs(columns/width-.5) < .35) & (abs(rows/height-.5) < .35)
    for half in (columns < width/2, columns >= width/2):
        cy, cx = rows[pupils & half].mean(), columns[pupils & half].mean()
        iris = np.clip((.118 - np.hypot(rows-cy, columns-cx)/width)/.014, 0, 1)[:, :, None]
        pixels[:, :, :3] = np.clip(pixels[:, :, :3]*(1 + (np.array([.84, .95, 1.10], np.float32)-1)*iris), 0, 1)
    eye_image.pixels.foreach_set(pixels.ravel()); eye_image.update()

eye=bpy.data.objects['Eyes'].active_material.node_tree.nodes.get('Principled BSDF')
eye.inputs['Roughness'].default_value=.14
eye.inputs['Specular IOR Level'].default_value=.5
eye.inputs['Coat Weight'].default_value=.45
eye.inputs['Coat Roughness'].default_value=.08

# A quiet ivory fabric keeps attention on the face; retain the authored cloth normal map.
blouse=bpy.data.materials['Ivory blouse']; bsdf=blouse.node_tree.nodes.get('Principled BSDF')
for link in list(bsdf.inputs['Base Color'].links): blouse.node_tree.links.remove(link)
bsdf.inputs['Base Color'].default_value=(.038,.031,.026,1) if male else (.78,.74,.64,1)

if male:
    # A waist-length cardigan follows the torso rather than the coat's broad,
    # extended silhouette. Keep the source sleeves and anatomical skin weights.
    cloth=bpy.data.objects['Ivory blouse']
    groups={g.index:g.name.lower() for g in cloth.vertex_groups}
    for vertex in cloth.data.vertices:
        x,y,z=vertex.co
        arm=sum(g.weight for g in vertex.groups if any(part in groups[g.group] for part in ('arm','hand')))
        neck=min(1,max(0,(1.52-z)/.09))
        vertex.co.x *= 1+neck*.075
        vertex.co.y *= 1+neck*.10
        vertex.co += vertex.normal*.005*neck
        # Keep the lower edge at the hip, with a gently gathered knit silhouette.
        hem=max(0,min(1,(1.10-z)/.13))*(1-arm)
        vertex.co.x *= 1-.045*hem
    cloth.data.update()
    # Cut the front along two exact diagonal planes, not a row of whole faces.
    # This leaves a clean V neck for the ribbed border and the shirt underneath.
    bm=bmesh.new(); bm.from_mesh(cloth.data)
    for sign in (-1,1):
        front_faces=[f for f in bm.faces if f.calc_center_median().y < -.015
                     and f.calc_center_median().z > 1.24]
        geom=list(set(front_faces+[e for f in front_faces for e in f.edges]+[v for f in front_faces for v in f.verts]))
        bmesh.ops.bisect_plane(bm,geom=geom,dist=.00001,
                              plane_co=(0,0,1.265),plane_no=(sign,0,-.40))
    opening=[f for f in bm.faces if f.calc_center_median().y < -.015
             and f.calc_center_median().z > 1.265
             and abs(f.calc_center_median().x) < .40*(f.calc_center_median().z-1.265)+.0001]
    bmesh.ops.delete(bm,geom=opening,context='FACES'); bm.to_mesh(cloth.data); bm.free()
    cloth.data.update()
    blouse.name='Charcoal knit cardigan'
    # A baked knit texture is cheap to render and survives the glTF export.
    size=512; yy,xx=np.mgrid[:size,:size]
    stitch_x=(xx%16)/16; stitch_y=(yy%16)/16
    yarn=np.cos(2*math.pi*(stitch_x-.32*np.abs(stitch_y*2-1)))
    grain=np.sin(xx*2.4+yy*1.7)*.015
    shade=.92+.07*yarn+.025*np.cos(2*math.pi*stitch_y)+grain
    pixels=np.ones((size,size,4),dtype=np.float32)
    pixels[:,:,:3]=shade[:,:,None]*np.array([.29,.27,.235])
    knit=bpy.data.images.new('Charcoal knitted stitches',width=size,height=size)
    knit.pixels.foreach_set(pixels.ravel()); knit.pack()
    texture=blouse.node_tree.nodes.new('ShaderNodeTexImage'); texture.image=knit
    blouse.node_tree.links.new(texture.outputs['Color'],bsdf.inputs['Base Color'])
    bsdf.inputs['Roughness'].default_value=.94
    # Replace the broad suit wrinkles with fine knitted yarn relief.
    for uv in cloth.data.uv_layers.active.data: uv.uv *= 4
    dx=np.roll(shade,-1,axis=1)-np.roll(shade,1,axis=1)
    dy=np.roll(shade,-1,axis=0)-np.roll(shade,1,axis=0)
    normals=np.stack((-dx*2,-dy*2,np.ones_like(dx)),axis=2)
    normals/=np.linalg.norm(normals,axis=2)[:,:,None]
    normal_pixels=np.ones((size,size,4),dtype=np.float32)
    normal_pixels[:,:,:3]=normals*.5+.5
    normal_image=bpy.data.images.new('Knitted yarn normals',width=size,height=size)
    normal_image.colorspace_settings.name='Non-Color'
    normal_image.pixels.foreach_set(normal_pixels.ravel()); normal_image.pack()
    normal_texture=blouse.node_tree.nodes.new('ShaderNodeTexImage'); normal_texture.image=normal_image
    normal_map=blouse.node_tree.nodes.new('ShaderNodeNormalMap'); normal_map.inputs['Strength'].default_value=.35
    blouse.node_tree.links.new(normal_texture.outputs['Color'],normal_map.inputs['Color'])
    blouse.node_tree.links.new(normal_map.outputs['Normal'],bsdf.inputs['Normal'])

# Original layered outfit, fitted to the source blouse and included in every body morph.
from academia_outfit import dress
dress(bpy.data.objects['Ivory blouse'], rig, male=male)
# Dark scalp below the transparent hair roots avoids a pale rim at the crown.
body=bpy.data.objects['Face, neck and hands']
scalp_color=(.024,.016,.012,1) if male else (.055,.026,.012,1)
scalp=bpy.data.materials.new('Hair roots');scalp.diffuse_color=scalp_color;scalp.use_nodes=True
scalp.node_tree.nodes.get('Principled BSDF').inputs['Base Color'].default_value=scalp_color
scalp.node_tree.nodes.get('Principled BSDF').inputs['Roughness'].default_value=.7
body.data.materials.append(scalp)
# Keep Rowan's painted roots behind the textured hairline so no dark scalp
# triangles show through the fine strands around the temples.
root_top = 1.702 if male else 1.692
root_back = -.035 if male else -.04
for poly in body.data.polygons:
    if all(body.data.vertices[i].co.z > root_top or (body.data.vertices[i].co.z > 1.62 and body.data.vertices[i].co.y > root_back) for i in poly.vertices): poly.material_index=1

originals = [o for o in bpy.data.collections['Player'].objects if o.type=='MESH']
for obj in originals:
    if obj.name in ['Face, neck and hands', 'Eyes', 'Ivory blouse', 'Chestnut hair']:
        mod=obj.modifiers.new('Portrait smoothing','SUBSURF'); mod.levels=1; mod.render_levels=1
bpy.context.view_layer.update(); deps=bpy.context.evaluated_depsgraph_get()
exported=[]
for obj in originals:
    mesh=bpy.data.meshes.new_from_object(obj.evaluated_get(deps),depsgraph=deps)
    baked=bpy.data.objects.new(character_name+' '+obj.name,mesh); bpy.context.scene.collection.objects.link(baked)
    baked.shape_key_add(name='Basis'); exported.append((obj,baked))

# Expressions are baked from anatomical skin weights, preserving teeth, lids and hair.
# The viewer blends mouth shapes from speech audio, and expression, gaze, head and
# shoulder units for body language; one-sided units allow asymmetric expressions.
poses = {
    'jawOpen': lambda: expression('JawDrop',.65),
    'mouthWide': lambda: [expression('Mouth'+side+'PullSide',.5) for side in ['Left','Right']],
    'mouthRound': lambda: expression('LipsKiss'),
    'lipsPart': lambda: [expression('UpperLipUp',.6), expression('lowerLipDown',.25)],
    'mouthPress': lambda: expression('lowerLipUp',.35),
    # Close from the resting lid without overshooting into the lower lid.
    'blink': lambda: [[expression(side+'UpperLidClosed',1-resting_lid), expression(side+'LowerLidUp')] for side in ['Left','Right']],
    'smileLeft': lambda: [expression('MouthLeftPullUp',.42), expression('LeftCheekUp',.6), expression('LeftLowerLidUp',.15)],
    'smileRight': lambda: [expression('MouthRightPullUp',.42), expression('RightCheekUp',.6), expression('RightLowerLidUp',.15)],
    'browRaiseLeft': lambda: [expression('LeftInnerBrowUp',.6), expression('LeftOuterBrowUp',.8)],
    'browRaiseRight': lambda: [expression('RightInnerBrowUp',.6), expression('RightOuterBrowUp',.8)],
    'browInner': lambda: both('InnerBrowUp',.8),
    'browDown': lambda: both('BrowDown',.7),
    'squint': lambda: both('LowerLidUp',.45),
    'eyeWide': lambda: both('UpperLidOpen',.45),
    'lookUp': lambda: both('EyeUp'),
    'lookDown': lambda: both('EyeDown'),
    'lookLeft': lambda: both('EyeturnLeft'),
    'lookRight': lambda: both('EyeturnRight'),
    'nod': lambda: rotate('head',(1,0,0),.065),
    'turn': lambda: rotate('head',(0,0,1),.07),
    'tilt': lambda: rotate('head',(0,1,0),.07),
    'shrug': lambda: shoulders(.08),
    'breathe': lambda: shoulders(.022)
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
    obj['source']='MakeHuman CC0 anatomy and short hair; original GlobeGlotter outfit. See LICENSE.txt.' if male else 'MakeHuman CC0 anatomy; Hazel Hair by Elvaerwyn (CC-BY); adapted for GlobeGlotter. See LICENSE.txt.'
bpy.context.view_layer.objects.active=exported[0][1]
out.parent.mkdir(parents=True,exist_ok=True)
bpy.ops.export_scene.gltf(filepath=str(out),export_format='GLB',use_selection=True,export_animations=False,
    export_morph=True,export_morph_normal=True,export_extras=True,export_image_format='JPEG',export_jpeg_quality=88)

def compact_morph_targets(path, tolerances={'POSITION':1e-5,'NORMAL':1e-3}):
    """Keep only measurable morph deltas as sparse accessors.
    The exporter writes float noise for every vertex (normals in particular), which
    otherwise stores each of the many face units densely."""
    raw=path.read_bytes(); json_length=struct.unpack_from('<I',raw,12)[0]
    gltf=json.loads(raw[20:20+json_length]); binary=raw[28+json_length:28+json_length+struct.unpack_from('<I',raw,20+json_length)[0]]
    if male:
        # Short hair uses overlapping cards. Cutout depth avoids transparent-card
        # sorting artifacts at the crown in the web renderer.
        for material in gltf['materials']:
            if material['name'] in ('Chestnut hair', 'Hair locks'):
                material['alphaMode']='MASK'; material['alphaCutoff']=.35
    old_views,views,out,kept=gltf['bufferViews'],[],bytearray(),{}
    kinds={5121:np.uint8,5123:np.uint16,5125:np.uint32}
    def read(view,dtype,count,offset=0):
        v=old_views[view]; assert 'byteStride' not in v
        return np.frombuffer(binary,dtype,count,v.get('byteOffset',0)+offset)
    def add(data,source=None):
        out.extend(bytes((-len(out))%4)); view={'buffer':0,'byteOffset':len(out),'byteLength':len(data)}
        view.update({k:source[k] for k in ('byteStride','target') if source and k in source})
        views.append(view); out.extend(data); return len(views)-1
    def keep(index):
        if index not in kept:
            v=old_views[index]; start=v.get('byteOffset',0)
            kept[index]=add(binary[start:start+v['byteLength']],v)
        return kept[index]
    def dense(index):
        a=gltf['accessors'][index]; assert a['componentType']==5126 and a['type']=='VEC3'
        values=read(a['bufferView'],np.float32,a['count']*3,a.get('byteOffset',0)).reshape(-1,3).copy() if 'bufferView' in a else np.zeros((a['count'],3),np.float32)
        if 'sparse' in a:
            s=a['sparse']; i=read(s['indices']['bufferView'],kinds[s['indices']['componentType']],s['count'],s['indices'].get('byteOffset',0))
            values[i]=read(s['values']['bufferView'],np.float32,s['count']*3,s['values'].get('byteOffset',0)).reshape(-1,3)
        return values
    replaced={}
    for mesh in gltf['meshes']:
        for primitive in mesh['primitives']:
            for target in primitive.get('targets',[]):
                deltas={name:dense(index) for name,index in target.items()}
                for name,values in deltas.items(): values[np.linalg.norm(values,axis=1)<=tolerances.get(name,1e-3)]=0
                changed=np.flatnonzero(np.any([np.any(v!=0,axis=1) for v in deltas.values()],axis=0))
                if not len(changed): changed=np.array([0])
                component=5123 if len(next(iter(deltas.values())))<65536 else 5125
                # POSITION and NORMAL share one index list per target.
                indices=(len(replaced),changed.astype(kinds[component]).tobytes())
                for name,index in target.items():
                    accessor={'componentType':5126,'count':len(deltas[name]),'type':'VEC3','sparse':{'count':len(changed),
                        'indices':{'bufferView':indices,'componentType':component},'values':{'bufferView':('v',deltas[name][changed].tobytes())}}}
                    if name=='POSITION': accessor.update(min=deltas[name].min(0).tolist(),max=deltas[name].max(0).tolist())
                    replaced[index]=accessor
    shared={}
    for index,accessor in enumerate(gltf['accessors']):
        if index in replaced:
            accessor=gltf['accessors'][index]=replaced[index]; s=accessor['sparse']
            key=s['indices']['bufferView'][0]
            if key not in shared: shared[key]=add(s['indices']['bufferView'][1])
            s['indices']['bufferView']=shared[key]; s['values']['bufferView']=add(s['values']['bufferView'][1])
            continue
        if 'bufferView' in accessor: accessor['bufferView']=keep(accessor['bufferView'])
        for part in ('indices','values'):
            if 'sparse' in accessor: accessor['sparse'][part]['bufferView']=keep(accessor['sparse'][part]['bufferView'])
    for image in gltf.get('images',[]):
        if 'bufferView' in image: image['bufferView']=keep(image['bufferView'])
    out.extend(bytes((-len(out))%4)); gltf['bufferViews']=views; gltf['buffers'][0]['byteLength']=len(out)
    text=json.dumps(gltf,separators=(',',':')).encode(); text+=b' '*((-len(text))%4)
    path.write_bytes(struct.pack('<III',0x46546C67,2,28+len(text)+len(out))+struct.pack('<II',len(text),0x4E4F534A)+text
        +struct.pack('<II',len(out),0x004E4942)+bytes(out))
compact_morph_targets(out)
# Optional reproducible portrait render for reviewing geometry and expressions.
if settings.portrait:
    for original,_ in exported: original.hide_render=True
    camera_data=bpy.data.cameras.new('Portrait'); camera=bpy.data.objects.new('Portrait',camera_data); bpy.context.scene.collection.objects.link(camera)
    camera.location=(0,-2.5,1.47); target=Vector((0,-.015,1.40)); camera.rotation_euler=(target-camera.location).to_track_quat('-Z','Y').to_euler(); camera_data.type='ORTHO'; camera_data.ortho_scale=.9
    scene=bpy.context.scene; scene.camera=camera
    for name,location,energy,size,color in [('Key',(-1,-2,3),110,2.5,(1,.91,.81)),('Fill',(1,-1.5,2),60,2,(.85,.91,1)),('Rim',(0,1,2.3),25,2.5,(1,.88,.7))]:
        data=bpy.data.lights.new(name,'AREA'); data.energy=energy; data.shape='DISK'; data.size=size; data.color=color
        obj=bpy.data.objects.new(name,data);scene.collection.objects.link(obj);obj.location=location;obj.rotation_euler=(target-obj.location).to_track_quat('-Z','Y').to_euler()
    scene.world.color=(.18,.18,.18);scene.render.engine='CYCLES';scene.cycles.samples=32
    scene.render.resolution_x=900;scene.render.resolution_y=1000;scene.render.resolution_percentage=100
    scene.render.image_settings.file_format='PNG';scene.render.filepath=settings.portrait
    bpy.ops.wm.save_as_mainfile(filepath=str(Path(settings.portrait).with_suffix('.blend')))
    bpy.ops.render.render(write_still=True)
print('EXPORTED',out)

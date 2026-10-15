"""Mei's sleek low bun: an original strand-textured scalp shell, a coiled bun, side-swept
bangs. All geometry is generated here.

Strands run in planes that contain one axis from the face to the bun, so brushed-back hair
fans out from the face and converges into the bun. The shell is a grid in (phi, psi): phi
turns about that axis and psi sweeps within each plane, so every texture column is a strand
and the hairline is a staggered row of strand tips.
"""
import math
import numpy as np
import bpy
import bmesh
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree
from avatar_textures import clamp, smooth, blur, new_image, normal_map

FACE = Vector((0, -.12, 1.625))      # inside the head, behind the bridge of the nose
BUN_ROOT = Vector((0, .02, 1.595))   # inside the head, under the bun
AXIS = (BUN_ROOT - FACE).normalized()
ORIGIN = FACE + AXIS*(Vector((0, -.05, 1.645)) - FACE).dot(AXIS)
UP = (Vector((0, 0, 1)) - AXIS*AXIS.z).normalized()
SIDE = AXIS.cross(UP)                # +x, the character's left
HEAD_Y = -.05
PART_X = -.03                        # side part over the character's right eye
# Front hairline (theta: 0 at the face, 90 at the ears, 180 at the nape) -> height above which hair grows.
HAIRLINE = ([0, 15, 30, 42, 50, 56, 61, 65, 69, 74, 85, 95, 100, 108, 120, 150, 180],
            [1.692, 1.692, 1.688, 1.680, 1.668, 1.650, 1.632, 1.618, 1.620, 1.628, 1.632, 1.630, 1.600, 1.572, 1.556, 1.548, 1.545])
DARK = np.array([.075, .055, .045], np.float32)   # sRGB texture values: a soft dark brown
LIGHT = np.array([.25, .185, .145], np.float32)


def smooth_array(a, b, t):
    t = np.clip((t-a)/(b-a), 0, 1); return t*t*(3-2*t)


def theta(x, y): return math.degrees(math.atan2(abs(x), -(y-HEAD_Y)))


# Front heights below are set on a reference forehead (brow top 1.635, crown 1.7334). They are
# rescaled between this head's brow and crown, so the hairline follows the face's proportions.
REF_BROW, REF_TOP = 1.635, 1.7334
BROW, TOP = REF_BROW, REF_TOP


def fit_height(z, bearing):
    front = smooth(75, 58, abs(bearing))
    scaled = BROW + (z-REF_BROW)*(TOP-BROW)/(REF_TOP-REF_BROW)
    return z + (scaled-z)*front


def hair_mask(p, soft=.003, inset=0.):
    """1 where hair grows; `inset` moves the edge into the hair (all hairlines run with height)."""
    t = theta(p.x, p.y)
    edge = fit_height(float(np.interp(t, *HAIRLINE)), t) + inset
    return smooth(edge-soft, edge+soft, p.z)


def direction(phi, psi):
    return AXIS*math.cos(psi) + (UP*math.cos(phi) + SIDE*math.sin(phi))*math.sin(psi)


def coordinates(p):
    """(phi, psi) of a point, as used by the shell grid."""
    d = p - ORIGIN
    along = d.dot(AXIS); across = d - AXIS*along
    return math.atan2(across.dot(SIDE), across.dot(UP)), math.atan2(across.length, along)


def strands(width, height, rng, *, wave=2):
    """Column streaks: brightness per strand at several scales, with gentle integer waviness."""
    columns = np.zeros(width, np.float32)
    for frequency, gain in [(6, .12), (24, .25), (96, .55), (384, 1.), (width, .9)]:
        knots = rng.normal(0, 1, frequency)
        columns += gain*np.interp(np.arange(width)*frequency/width, np.arange(frequency+1), np.append(knots, knots[0]))
    columns = (columns-columns.min())/(columns.max()-columns.min())
    drift = blur(rng.normal(0, 1, (height, width//16)).astype(np.float32), 6)
    drift = np.repeat(drift/np.abs(drift).max(), 16, axis=1)
    shift = np.round(drift*wave).astype(int)
    index = (np.arange(width)[None, :] + shift) % width
    return columns[index]


def follow(obj, rig, bones):
    """Rigidly attach generated hair to the head (or blend weights from `bones`)."""
    obj.parent = rig
    if bones is None:
        obj.vertex_groups.new(name='head').add(list(range(len(obj.data.vertices))), 1, 'REPLACE')
    else:
        groups = {}
        for index, weights in enumerate(bones):
            for name, weight in weights:
                group = groups.get(name) or groups.setdefault(name, obj.vertex_groups.new(name=name))
                group.add([index], weight, 'REPLACE')
    obj.modifiers.new('Skin deformation', 'ARMATURE').object = rig


def material(name, image, normal_image, *, roughness, specular):
    mat = bpy.data.materials.new(name); mat.use_nodes = True
    nodes = mat.node_tree.nodes; links = mat.node_tree.links; shader = nodes.get('Principled BSDF')
    texture = nodes.new('ShaderNodeTexImage'); texture.image = image
    links.new(texture.outputs['Color'], shader.inputs['Base Color']); links.new(texture.outputs['Alpha'], shader.inputs['Alpha'])
    if normal_image:
        normal_texture = nodes.new('ShaderNodeTexImage'); normal_texture.image = normal_image
        node = nodes.new('ShaderNodeNormalMap'); node.inputs['Strength'].default_value = .5
        links.new(normal_texture.outputs['Color'], node.inputs['Color']); links.new(node.outputs['Normal'], shader.inputs['Normal'])
    shader.inputs['Roughness'].default_value = roughness; shader.inputs['Specular IOR Level'].default_value = specular
    mat.surface_render_method = 'DITHERED'; mat.use_backface_culling = False
    return mat


def grow_hair(body, rig, collection):
    mesh = body.data
    global BROW, TOP
    TOP = max(v.co.z for v in mesh.vertices)
    brows = bpy.data.objects.get('Eyebrows')
    if brows: BROW = max(v.co.z for v in brows.data.vertices)
    skin = BVHTree.FromPolygons([v.co.copy() for v in mesh.vertices], [p.vertices[:] for p in mesh.polygons])
    nearest = KDTree(len(mesh.vertices))
    for v in mesh.vertices: nearest.insert(v.co, v.index)
    nearest.balance()
    names = {g.index: g.name for g in body.vertex_groups}
    def weights_at(point):
        _, index, _ = nearest.find(point)
        return [(names[g.group], g.weight) for g in mesh.vertices[index].groups if g.weight > .001]
    rng = np.random.default_rng(41)
    part_phi, _ = coordinates(Vector((PART_X, -.12, 1.71)))

    # Ears: skin that stands out from the skull at the side of the head.
    def skull_x(y, z, sign):
        hit = skin.ray_cast(Vector((0, y, z)), Vector((sign, 0, 0)), .2)[0]
        return abs(hit.x) if hit is not None else .07
    ear_points = [v.co.copy() for v in mesh.vertices if 65 < theta(v.co.x, v.co.y) < 130 and 1.55 < v.co.z < 1.69
                  and abs(v.co.x) > skull_x(v.co.y, v.co.z, math.copysign(1, v.co.x)) + .004]
    ears = KDTree(max(1, len(ear_points)))
    for i, co in enumerate(ear_points): ears.insert(co, i)
    ears.balance()
    def clear_of_ears(point):
        if not ear_points: return 1.
        return smooth(.003, .009, ears.find(point)[2])

    # ---- Shell grid ------------------------------------------------------------------
    NU, NV = 192, 110
    PSI0, PSI1 = math.radians(2), math.radians(176)
    grid = {}
    for j in range(NV+1):
        psi = PSI0 + (PSI1-PSI0)*j/NV
        for i in range(NU+1):
            phi = -math.pi + 2*math.pi*i/NU
            hit, normal, _, distance = skin.ray_cast(ORIGIN, direction(phi, psi), .2)
            if hit is None or distance > .14: continue
            if normal.dot(hit-ORIGIN) < 0: normal = -normal
            grid[i, j] = (hit, normal.normalized(), hair_mask(hit, .006)*clear_of_ears(hit))
    mask = np.zeros((NV+1, NU+1), np.float32)
    for (i, j), (_, _, m) in grid.items(): mask[j, i] = m

    def thickness(p, m, phi):
        top = smooth(1.67, 1.73, p.z)*smooth(.02, -.08, p.y)
        part = math.exp(-((phi-part_phi)/.12)**2)*smooth(-.02, -.10, p.y)
        return (.0022 + .0008*top + .0008*part)*(.3 + .7*smooth(0, .7, m))

    def shell(name, lift, v0, v1):
        coords, faces, uvs, index = [], [], [], {}
        for (i, j), (hit, normal, m) in grid.items():
            phi = -math.pi + 2*math.pi*i/NU
            index[i, j] = len(coords)
            coords.append(hit + normal*(thickness(hit, m, phi) + lift))
            uvs.append((i/NU, v0 + (v1-v0)*j/NV))
        for j in range(NV):
            for i in range(NU):
                quad = [(i, j), (i+1, j), (i+1, j+1), (i, j+1)]
                if not all(q in index for q in quad): continue
                if max(mask[q[1], q[0]] for q in quad) < .01: continue
                points = [coords[index[q]] for q in quad]
                if max((a-b).length for a, b in zip(points, points[1:]+points[:1])) > .02: continue
                faces.append([index[q] for q in quad])
        data = bpy.data.meshes.new(name); data.from_pydata(coords, [], faces)
        layer = data.uv_layers.new(name='UVMap')
        for poly in data.polygons:
            poly.use_smooth = True
            for loop, vertex in zip(poly.loop_indices, poly.vertices): layer.data[loop].uv = uvs[vertex]
        # Drop unused grid points.
        used = {v for f in faces for v in f}
        obj = bpy.data.objects.new(name, data); collection.objects.link(obj)
        bm = bmesh.new(); bm.from_mesh(data)
        bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.index not in used], context='VERTS')
        bm.to_mesh(data); bm.free(); data.update()
        follow(obj, rig, [weights_at(v.co) for v in data.vertices])
        return obj

    inner = shell('Sleek hair', 0, .004, .496)
    outer = shell('Sleek hair strands', .0009, .504, .996)
    outer_data = outer.data
    inner.data.materials.clear()

    # ---- Texture: inner (bottom half) and outer (top half) ----------------------------
    TW, TH = 1024, 1024; half = TH//2
    rows_v = np.linspace(0, NV, half); cols_u = np.linspace(0, NU, TW)
    def upsample(values):
        stage = np.array([np.interp(rows_v, np.arange(NV+1), values[:, i]) for i in range(NU+1)]).T
        return np.array([np.interp(cols_u, np.arange(NU+1), stage[r]) for r in range(half)])
    coverage = upsample(mask)
    phi_grid = -math.pi + 2*math.pi*np.arange(TW)/TW
    psi_rows = PSI0 + (PSI1-PSI0)*rows_v/NV

    streak = strands(TW, half, rng)
    # Each strand ends at its own point, in loose clumps, so the hairline is fine and irregular.
    clumps = np.interp(np.arange(TW), np.arange(0, TW+1, 8), rng.random(TW//8+1))
    tips = .6*rng.random(TW) + .4*clumps
    inner_alpha = (coverage + .45*(tips[None, :]-.5) + .22*(streak-.5)) > .5
    inner_rgb = DARK + (LIGHT-DARK)*np.round(np.clip(streak*1.15-.1, 0, 1)*6)[:, :, None]/6
    # A few fine baby hairs stand out past the hairline.
    wisps = (tips[None, :] > .93) & (coverage > .25) & (coverage < .5)
    inner_alpha |= wisps & (streak > .5)

    streak_outer = strands(TW, half, rng, wave=3)
    rows = np.arange(half, dtype=np.float32)[:, None]
    segments = np.sin(rows*rng.uniform(.012, .03, TW)[None, :] + rng.uniform(0, 2*math.pi, TW)[None, :])
    outer_alpha = (streak_outer > .6) & (segments > -.5) & (coverage > .85)
    outer_rgb = DARK + (LIGHT-DARK)*np.round(np.clip(streak_outer*1.1, 0, 1)*6)[:, :, None]/6

    rgb = np.concatenate([inner_rgb, outer_rgb], axis=0)
    alpha = np.concatenate([inner_alpha, outer_alpha], axis=0).astype(np.float32)
    height_field = np.concatenate([streak, streak_outer], axis=0)
    image = new_image('Sleek hair strands', rgb, alpha=alpha)
    normals = new_image('Sleek hair normals', normal_map(blur(height_field, 1), 1.2), data=True)
    sleek = material('Sleek hair', image, normals, roughness=.5, specular=.32)
    inner.data.materials.append(sleek); outer_data.materials.append(sleek)

    # ---- Coiled bun ----------------------------------------------------------------------
    hit, normal, _, _ = skin.ray_cast(ORIGIN, AXIS, .2)
    if normal.dot(AXIS) < 0: normal = -normal
    n = (normal*.5 + AXIS*.5).normalized()
    up = (Vector((0, 0, 1)) - n*n.z).normalized(); right = n.cross(up)
    base = hit + n*.002
    # A smooth chignon: a low dome whose strands swirl into the centre, with shallow spiral grooves.
    RINGS, SEGMENTS = 40, 96
    W, H, D = .034, .028, .021
    coords, faces, uvs = [], [], []
    for i in range(RINGS+1):
        r = i/RINGS
        for m in range(SEGMENTS+1):
            a = 2*math.pi*m/SEGMENTS
            phase = a/(2*math.pi) + 2.3*r
            groove = .0008*math.sin(math.pi*min(1, r*1.1))*math.cos(2*math.pi*phase)
            dome = D*(1-r*r)**.65 + groove - .004*smooth(.85, 1, r)
            coords.append(base + n*dome + right*math.cos(a)*r*W + up*math.sin(a)*r*H)
            uvs.append(((a/(2*math.pi) - 2.3*r)*2, .01 + .08*r))
    for i in range(RINGS):
        for m in range(SEGMENTS):
            a, b = i*(SEGMENTS+1)+m, (i+1)*(SEGMENTS+1)+m
            faces.append((a, a+1, b+1, b))
    data = bpy.data.meshes.new('Hair bun'); data.from_pydata(coords, [], faces); data.update()
    layer = data.uv_layers.new(name='UVMap')
    for poly in data.polygons:
        poly.use_smooth = True
        for loop, vertex in zip(poly.loop_indices, poly.vertices): layer.data[loop].uv = uvs[vertex]
    data.materials.append(sleek)
    bun = bpy.data.objects.new('Hair bun', data); collection.objects.link(bun); follow(bun, rig, None)

    # ---- Side-swept bangs ------------------------------------------------------------------
    def surface(azimuth, z):
        """Skin point at a bearing (0 = face, +90 = character's left) and height."""
        a = math.radians(azimuth)
        return skin.ray_cast(Vector((0, HEAD_Y, fit_height(z, azimuth))), Vector((math.sin(a), -math.cos(a), 0)), .2)[0]
    def crown(x, y):
        return skin.ray_cast(Vector((x, y, 1.9)), Vector((0, 0, -1)), .3)[0]
    def spline(controls, count):
        """Catmull-Rom through the control points."""
        points = []
        c = [controls[0]] + controls + [controls[-1]]
        for k in range(count):
            t = k/(count-1)*(len(controls)-1); i = min(int(t), len(controls)-2); f = t-i
            p0, p1, p2, p3 = c[i], c[i+1], c[i+2], c[i+3]
            points.append(.5*((2*p1) + (-p0+p2)*f + (2*p0-5*p1+4*p2-p3)*f*f + (-p0+3*p1-3*p2+p3)*f*f*f))
        return points
    def lay(points, lift):
        """Rest each point above the skin: clear of the hair shell where it grows, close over the forehead."""
        laid = []
        for k, q in enumerate(points):
            hit, normal, _, _ = skin.find_nearest(q)
            if normal.dot(q-ORIGIN) < 0: normal = -normal
            over_hair = hair_mask(hit, .006)
            phi, _ = coordinates(hit)
            shell = thickness(hit, over_hair, phi) + .0022
            laid.append((hit + normal*(.0026 + (shell-.0026)*over_hair + lift(k/(len(points)-1))), normal))
        return laid
    # Side-swept bangs: one sheet of strands from the side part, across the forehead to the left
    # temple and tucked back. Each across-row is one strand path; the front-most runs lowest.
    def strand_path(w, count, spread=0):
        controls = [crown(PART_X + .003, -.124 + .03*w), surface(-4+spread, 1.692 + .022*w), surface(14+spread, 1.668 + .036*w),
                    surface(32+spread, 1.652 + .046*w), surface(50+spread, 1.648 + .048*w), surface(66+spread, 1.652 + .038*w),
                    surface(82+spread, 1.658 + .022*w), surface(100, 1.660 + .008*w)]
        return None if any(c is None for c in controls) else spline(controls, count)
    NA, NW = 56, 9
    def bangs_lift(w):
        # Volume over the forehead; the upper rows and the tucked tail sink under the outer strands.
        return lambda t: (.0012*math.sin(math.pi*min(1, t*1.2)) + .0010)*(1-w) - .0010*w - .0016*smooth(.45, .7, t) - .0024*smooth(.75, 1, t)
    rows = [lay(strand_path(j/(NW-1), NA), bangs_lift(j/(NW-1))) for j in range(NW)]
    coords, faces, uvs = [], [], []
    for j, row in enumerate(rows):
        for i, (q, _) in enumerate(row):
            coords.append(q); uvs.append((j/(NW-1), i/(NA-1)))
    for j in range(NW-1):
        for i in range(NA-1):
            faces.append((j*NA+i, j*NA+i+1, (j+1)*NA+i+1, (j+1)*NA+i))
    data = bpy.data.meshes.new('Swept bangs'); data.from_pydata(coords, [], faces); data.update()
    layer = data.uv_layers.new(name='UVMap')
    for poly in data.polygons:
        poly.use_smooth = True
        for loop, vertex in zip(poly.loop_indices, poly.vertices): layer.data[loop].uv = uvs[vertex]
    # Strands run along v; each ends at its own point in the tucked tail, and the outer strands
    # thin out along the lower and upper edges so neither edge reads as a cut line.
    BW, BH = 256, 512
    bang_streak = strands(BW, BH, rng, wave=1)
    along = np.linspace(0, 1, BH)[:, None]; across = np.linspace(0, 1, BW)
    ends = .78 + .2*np.interp(np.arange(BW), np.arange(0, BW+1, 2), rng.random(BW//2+1))
    keep = rng.random(BW) > np.maximum(.65*smooth_array(.14, 0, across), .55*smooth_array(.82, 1, across))
    bang_alpha = ((along < ends[None, :]) & (along > .004) & keep[None, :]).astype(np.float32)
    bang_rgb = DARK + (LIGHT-DARK)*np.round(np.clip(bang_streak*1.15-.1, 0, 1)*6)[:, :, None]/6
    bang_material = material('Swept bangs', new_image('Swept bang strands', bang_rgb, alpha=bang_alpha),
                             new_image('Swept bang normals', normal_map(blur(bang_streak, 1), 1.2), data=True), roughness=.5, specular=.32)
    data.materials.append(bang_material)
    bangs = bpy.data.objects.new('Swept bangs', data); collection.objects.link(bangs); follow(bangs, rig, None)
    return [inner, outer, bun, bangs]

"""Mei's dark lightweight blazer over an ivory silk blouse, with a small gold GG lapel pin.
The blazer is the CC0 MakeHuman male_elegantsuit01 slimmed to her figure (tie and shirt
removed). The blouse and pin are original geometry.
"""
import math
import bpy
import bmesh
import numpy as np
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree
from avatar_textures import image_pixels, new_image, blur, smooth, clamp, normal_map

NECK_Y = -.024
# Soft V neckline at the front, rising to a low round collar behind the neck, where it fills the
# space inside the blazer's collar: angle around the neck (0 at the front) -> blouse edge height.
NECKLINE = ([0, 10, 22, 35, 50, 62, 75, 90, 120, 180], [1.392, 1.402, 1.424, 1.447, 1.464, 1.482, 1.498, 1.506, 1.512, 1.514])


def neckline(x, y):
    return float(np.interp(math.degrees(math.atan2(abs(x), -(y-NECK_Y))), *NECKLINE))


def fine_wool(height, width, rng, colour):
    """Lightweight worsted: a faint 2/1 twill and soft yarn variation, almost solid."""
    yy, xx = np.mgrid[:height, :width]
    twill = ((xx + yy) % 3 == 0).astype(np.float32)
    rgb = np.array(colour, np.float32)*(.94 + .07*twill + .03*rng.random((height, width)).astype(np.float32))[:, :, None]
    drift = blur(rng.normal(0, 1, (height, width)).astype(np.float32), 6)
    return rgb*(1 + .05*drift/np.abs(drift).max())[:, :, None]


def weights_from(source, nearest):
    names = {g.index: g.name for g in source.vertex_groups}
    def lookup(point):
        _, index, _ = nearest.find(point)
        return [(names[g.group], g.weight) for g in source.data.vertices[index].groups if g.weight > .001]
    return lookup


def attach(obj, rig, lookup, collection, label):
    for c in list(obj.users_collection): c.objects.unlink(obj)
    collection.objects.link(obj); obj.parent = rig
    groups = {}
    for v in obj.data.vertices:
        for name, weight in lookup(v.co):
            group = groups.get(name) or groups.setdefault(name, obj.vertex_groups.new(name=name))
            group.add([v.index], weight, 'REPLACE')
    obj.modifiers.new(label, 'ARMATURE').object = rig
    return obj


def dress(suit, rig, full_body, body, source_texture):
    collection = bpy.data.collections['Player']
    rng = np.random.default_rng(29)
    full = BVHTree.FromPolygons([v.co.copy() for v in full_body.vertices], [p.vertices[:] for p in full_body.polygons])

    # Classify the authored atlas: navy striped tie, white shirt, charcoal jacket and buttons.
    original = bpy.data.images.load(str(source_texture), check_existing=True)
    width, height = original.size
    source = image_pixels(original)[:, :, :3]
    lum = source.mean(2)
    navy = ((source[:, :, 2]-source[:, :, 0]) > .06).astype(np.float32)
    tie = blur((blur(navy, 9) > .02).astype(np.float32), 9) > .98
    tie = blur(tie.astype(np.float32), 3) > .01
    shirt = (lum > .42) & ~tie
    buttons = (lum < .10)
    jacket = ~(tie | shirt | buttons)

    # Keep the jacket to the hip, and replace the tie, shirt collar and shirt front with the blouse.
    bm = bmesh.new(); bm.from_mesh(suit.data); bm.faces.ensure_lookup_table()
    uv = bm.loops.layers.uv.active
    def uv_centre(face):
        return (sum(l[uv].uv.x for l in face.loops)/len(face.loops), sum(l[uv].uv.y for l in face.loops)/len(face.loops))
    from bpy_extras.bmesh_utils import bmesh_linked_uv_islands
    islands = bmesh_linked_uv_islands(bm, uv)
    def texel(face):
        u, v = uv_centre(face); return min(height-1, int(v*height)), min(width-1, int(u*width))
    # The shirt collar, front and tie (with the knot's dark shading) form islands that are mostly
    # shirt or tie; the cuffs are shirt islands too but sit low on the sleeves, so they stay.
    collar = set()
    for members in islands:
        light = np.mean([shirt[texel(f)] or tie[texel(f)] for f in members])
        if light > .5 and np.mean([f.calc_center_median().z for f in members]) > 1.3: collar.update(members)
    def collar_island(face): return face in collar
    dead = [f for f in bm.faces if all(v.co.z < .98 for v in f.verts) or collar_island(f)]
    bmesh.ops.delete(bm, geom=dead, context='FACES')
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
    # Slim the cut to her figure. The excess over a thin offset is smoothed spatially before it
    # is removed, so the lapels stay layered over the fronts; then the cloth clears the blouse.
    offsets = []
    for v in bm.verts:
        hit, normal, _, _ = full.find_nearest(v.co)
        offsets.append((hit, normal, (v.co-hit).dot(normal)))
    excess = np.array([max(0., side - (.007 + .006*smooth(.12, .20, abs(v.co.x)))) for v, (_, _, side) in zip(bm.verts, offsets)])
    tree = KDTree(len(bm.verts))
    for i, v in enumerate(bm.verts): tree.insert(v.co, i)
    tree.balance()
    smoothed = np.array([np.mean([excess[j] for _, j, _ in tree.find_range(v.co, .015)]) for v in bm.verts])
    for v, (hit, normal, side), cut in zip(bm.verts, offsets, smoothed):
        v.co -= normal*min(cut, max(0., side-.004))
        side = (v.co-hit).dot(normal)
        if side < .0085: v.co += normal*(.0085-side)
    bm.to_mesh(suit.data); bm.free(); suit.data.update()

    # Dark charcoal fine wool, keeping the authored fold shading; ivory silk cuffs; horn buttons.
    reference = np.median(lum[jacket & (lum > .12)])
    shading = np.clip(lum/reference, .35, 1.7)[:, :, None]
    rgb = fine_wool(height, width, rng, (.175, .172, .185))*shading
    shirt_shading = np.clip(lum/np.percentile(lum[shirt], 90), .5, 1.05)[:, :, None]
    rgb[shirt] = (np.array([.84, .78, .68], np.float32)*shirt_shading)[shirt]
    rgb[buttons] = (np.array([.035, .028, .024], np.float32)*np.clip(lum/.08, .5, 1.3)[:, :, None])[buttons]
    wool = new_image('Charcoal wool blazer', rgb)
    material = suit.active_material; material.name = 'Charcoal wool blazer'
    nodes = material.node_tree.nodes
    next(n for n in nodes if n.type == 'TEX_IMAGE').image = wool
    shader = nodes.get('Principled BSDF')
    shader.inputs['Roughness'].default_value = .86; shader.inputs['Specular IOR Level'].default_value = .2

    # ---- Ivory silk blouse: the torso surface 2.5 mm out, cut cleanly along a soft V neckline.
    # Only the V and the neck can show: elsewhere the blazer covers it, so leave it out there.
    keep = [p for p in full_body.polygons if any(1.12 < full_body.vertices[i].co.z < 1.53 for i in p.vertices)
            and all(abs(full_body.vertices[i].co.x) < .19 for i in p.vertices)
            and any(abs(full_body.vertices[i].co.x) < .075 or full_body.vertices[i].co.z > 1.40 for i in p.vertices)]
    bm = bmesh.new()
    lookup = {}
    for p in keep:
        verts = []
        for i in p.vertices:
            if i not in lookup:
                co = full_body.vertices[i].co
                lookup[i] = bm.verts.new(co + full_body.vertices[i].normal*.002)
            verts.append(lookup[i])
        try: bm.faces.new(verts)
        except ValueError: pass
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=1, use_grid_fill=True)
    # Clip each triangle against the neckline (keep below it), splitting edges exactly.
    field = {v: v.co.z - neckline(v.co.x, v.co.y) for v in bm.verts}
    cut = {}
    def crossing(a, b):
        key = (a.index, b.index) if a.index < b.index else (b.index, a.index)
        if key not in cut:
            t = field[a]/(field[a]-field[b])
            cut[key] = a.co.lerp(b.co, t)
        return cut[key]
    bm.verts.index_update()
    polygons = []
    for f in list(bm.faces):
        ring = list(f.verts); out = []
        for a, b in zip(ring, ring[1:]+ring[:1]):
            if field[a] <= 0: out.append(a.co.copy())
            if (field[a] <= 0) != (field[b] <= 0): out.append(crossing(a, b).copy())
        if len(out) >= 3: polygons.append(out)
    bm.free()
    coords, faces, index = [], [], {}
    for poly in polygons:
        face = []
        for co in poly:
            key = tuple(round(c, 6) for c in co)
            if key not in index: index[key] = len(coords); coords.append(co)
            face.append(index[key])
        faces.append(face)
    data = bpy.data.meshes.new('Silk blouse'); data.from_pydata(coords, [], faces); data.update()
    # A narrow rolled hem turns under along the neckline so the edge has thickness.
    bm = bmesh.new(); bm.from_mesh(data)
    bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=1e-5)
    edge = [e for e in bm.edges if e.is_boundary and min(v.co.z for v in e.verts) > 1.3]
    hem = bmesh.ops.extrude_edge_only(bm, edges=edge)
    for v in [g for g in hem['geom'] if isinstance(g, bmesh.types.BMVert)]:
        hit, normal, _, _ = full.find_nearest(v.co)
        v.co = hit + normal*.0008 - Vector((0, 0, .0015))
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    bm.to_mesh(data); bm.free(); data.update()
    # Cylindrical UVs for the silk: u around the body, v up.
    layer = data.uv_layers.new(name='UVMap')
    for poly in data.polygons:
        poly.use_smooth = True
        for loop, vertex in zip(poly.loop_indices, poly.vertices):
            co = data.vertices[vertex].co
            layer.data[loop].uv = (.5 + math.atan2(co.x, -(co.y-NECK_Y))/(2*math.pi), (co.z-1.1)/.45)
    blouse = bpy.data.objects.new('Silk blouse', data)
    # Soft drape: long vertical folds that gather toward the V, and a gentle satin variation.
    size = 512
    yy, xx = np.mgrid[:size, :size].astype(np.float32)/size
    folds = sum(np.sin(2*math.pi*(xx*f + rng.random()) + 1.5*np.sin(2*math.pi*yy*.7 + rng.random()))/f for f in (9, 17, 31))
    folds = blur(folds.astype(np.float32), 3); folds = (folds-folds.min())/(folds.max()-folds.min())
    silk = np.array([.86, .80, .70], np.float32)*(.90 + .14*folds)[:, :, None]
    silk_material = bpy.data.materials.new('Ivory silk blouse'); silk_material.use_nodes = True
    nodes = silk_material.node_tree.nodes; links = silk_material.node_tree.links; s = nodes.get('Principled BSDF')
    texture = nodes.new('ShaderNodeTexImage'); texture.image = new_image('Ivory silk', silk)
    links.new(texture.outputs['Color'], s.inputs['Base Color'])
    normal_texture = nodes.new('ShaderNodeTexImage'); normal_texture.image = new_image('Ivory silk folds', normal_map(folds, 6.0), data=True)
    node = nodes.new('ShaderNodeNormalMap'); node.inputs['Strength'].default_value = .6
    links.new(normal_texture.outputs['Color'], node.inputs['Color']); links.new(node.outputs['Normal'], s.inputs['Normal'])
    s.inputs['Roughness'].default_value = .34; s.inputs['Specular IOR Level'].default_value = .55
    s.inputs['Sheen Weight'].default_value = .2; s.inputs['Sheen Tint'].default_value = (1, .95, .85, 1)
    silk_material.use_backface_culling = False
    data.materials.append(silk_material)
    nearest = KDTree(len(body.data.vertices))
    for v in body.data.vertices: nearest.insert(v.co, v.index)
    nearest.balance()
    # The body under the blouse is hidden by the suit, so borrow the suit's own torso weights.
    suit_nearest = KDTree(len(suit.data.vertices))
    for v in suit.data.vertices: suit_nearest.insert(v.co, v.index)
    suit_nearest.balance()
    from_suit = weights_from(suit, suit_nearest); from_body = weights_from(body, nearest)
    def blend(point):
        # Near the neck, follow the neck skin so the neckline stays put during nods.
        neck = smooth(1.43, 1.47, point.z)
        merged = {}
        for weights, mix in ((from_suit(point), 1-neck), (from_body(point), neck)):
            total = sum(w for _, w in weights) or 1
            for name, w in weights: merged[name] = merged.get(name, 0) + w/total*mix
        return list(merged.items())
    attach(blouse, rig, blend, collection, 'Follow blazer')

    # ---- Small gold GG pin on her left lapel ------------------------------------------------
    surface = BVHTree.FromPolygons([v.co.copy() for v in suit.data.vertices], [list(p.vertices) for p in suit.data.polygons])
    hit, normal, _, _ = surface.ray_cast(Vector((.078, -1, 1.372)), Vector((0, 1, 0)))
    if hit is None: raise ValueError('Lapel pin misses the blazer')
    if normal.y > 0: normal = -normal
    gold = bpy.data.materials.new('Polished gold pin'); gold.use_nodes = True
    g = gold.node_tree.nodes.get('Principled BSDF')
    g.inputs['Base Color'].default_value = (.83, .62, .30, 1); g.inputs['Metallic'].default_value = 1; g.inputs['Roughness'].default_value = .28
    up = (Vector((0, 0, 1)) - normal*normal.z).normalized(); right = up.cross(normal)
    coords, faces = [], []
    sides = 28
    for ring, (radius, lift) in enumerate([(.0095, .0004), (.0095, .0018), (.0085, .0023)]):
        for k in range(sides):
            a = 2*math.pi*k/sides
            coords.append(hit + normal*lift + (right*math.cos(a) + up*math.sin(a))*radius)
    for ring in range(2):
        for k in range(sides):
            a, b = ring*sides+k, ring*sides+(k+1) % sides
            faces.append((a, b, b+sides, a+sides))
    faces.append(tuple(2*sides+k for k in range(sides)))
    data = bpy.data.meshes.new('GG lapel pin'); data.from_pydata(coords, [], faces); data.update()
    for poly in data.polygons: poly.use_smooth = True
    data.materials.append(gold)
    pin = bpy.data.objects.new('GG lapel pin', data); collection.objects.link(pin)
    enamel = bpy.data.materials.new('Dark enamel'); enamel.use_nodes = True
    e = enamel.node_tree.nodes.get('Principled BSDF')
    e.inputs['Base Color'].default_value = (.03, .16, .09, 1); e.inputs['Roughness'].default_value = .2
    data.materials.append(enamel); data.polygons[-1].material_index = 1
    lettering = bpy.data.curves.new('GG lapel pin letters', 'FONT'); lettering.body = 'GG'
    lettering.align_x = 'CENTER'; lettering.align_y = 'CENTER'; lettering.size = .0098
    lettering.extrude = .0003; lettering.resolution_u = 3
    text = bpy.data.objects.new('GG lapel pin letters', lettering); collection.objects.link(text)
    text.location = hit + normal*.0024
    text.rotation_euler = Matrix((right, up, normal)).transposed().to_euler()
    text.data.materials.append(gold)
    bpy.ops.object.select_all(action='DESELECT'); text.select_set(True); bpy.context.view_layer.objects.active = text
    bpy.ops.object.convert(target='MESH'); text = bpy.context.object
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    bpy.ops.object.select_all(action='DESELECT'); pin.select_set(True); text.select_set(True)
    bpy.context.view_layer.objects.active = pin; bpy.ops.object.join()
    attach(pin, rig, weights_from(suit, suit_nearest), collection, 'Follow blazer')
    return blouse

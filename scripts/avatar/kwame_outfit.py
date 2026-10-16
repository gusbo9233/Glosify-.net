"""Kwame's herringbone tweed blazer, ivory shirt, oxblood knit tie and gold GG emblem.
Retextures the CC0 MakeHuman male_elegantsuit01 (its shirt collar and tie are part of
the suit), keeps the authored fold shading and adds original embroidery geometry.
"""
import math
import bpy
import bmesh
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree
from avatar_textures import rasterize, image_pixels, new_image, blur, smooth, clamp


def herringbone(height, width, rng):
    """Muted brown tweed at about 2 mm per pixel: 1 cm herringbone bands of fine twill,
    slubby yarn and sparse rust, moss and cream flecks."""
    yy, xx = np.mgrid[:height, :width]
    band = 5
    direction = np.where((xx//band) % 2 == 0, 1, -1)
    twill = ((xx + direction*yy) % 3 == 0).astype(np.float32)
    light = np.array([.40, .315, .22], np.float32); dark = np.array([.25, .19, .13], np.float32)
    rgb = dark + (light-dark)*(.8*twill + .2*rng.random((height, width)).astype(np.float32))[:, :, None]
    drift = blur(rng.normal(0, 1, (height, width)).astype(np.float32), 5)
    rgb *= (1 + .07*drift/np.abs(drift).max())[:, :, None]
    for colour, rate in [((.42, .20, .10), .0025), ((.20, .25, .13), .002), ((.55, .50, .40), .0015)]:
        fleck = (rng.random((height, width)) < rate).astype(np.float32)
        rgb += (np.array(colour, np.float32) - rgb)*fleck[:, :, None]*.55
    return rgb


def knit(height, width, colour):
    yy, xx = np.mgrid[:height, :width]
    stitch = np.cos(2*math.pi*((xx % 8)/8 - .3*np.abs(((yy % 8)/8)*2-1)))
    shade = .86 + .12*stitch
    return np.array(colour, np.float32)*shade[:, :, None]


def dress(suit, rig, source_texture):
    collection = bpy.data.collections['Player']
    # Trousers and shoes are never in frame: keep the jacket down to the hip.
    bm = bmesh.new(); bm.from_mesh(suit.data)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if all(v.co.z < .98 for v in f.verts)], context='FACES')
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context='VERTS')
    bm.to_mesh(suit.data); bm.free(); suit.data.update()

    # Classify the authored atlas: navy striped tie, white shirt, charcoal jacket and buttons.
    original = bpy.data.images.load(str(source_texture), check_existing=True)
    width, height = original.size
    source = image_pixels(original)[:, :, :3]
    lum = source.mean(2)
    navy = ((source[:, :, 2]-source[:, :, 0]) > .06).astype(np.float32)
    tie = blur((blur(navy, 9) > .02).astype(np.float32), 9) > .98  # Close the white stripes.
    tie = blur(tie.astype(np.float32), 3) > .01  # Include the light edge stitching.
    shirt = (lum > .42) & ~tie
    buttons = (lum < .10)
    jacket = ~(tie | shirt | buttons)
    reference = np.median(lum[jacket & (lum > .12)])
    shading = np.clip(lum/reference, .35, 1.6)[:, :, None]

    rng = np.random.default_rng(23)
    rgb = herringbone(height, width, rng)*shading
    shirt_shading = np.clip(lum/np.percentile(lum[shirt], 90), .45, 1.05)[:, :, None]
    rgb[shirt] = (np.array([.80, .76, .66], np.float32)*shirt_shading)[shirt]
    tie_shading = np.clip(lum/np.median(lum[tie & (navy > 0)]), .5, 1.25) if tie.any() else 1
    tie_rgb = knit(height, width, (.26, .045, .05))
    rgb[tie] = (tie_rgb*np.clip(np.where(navy > 0, tie_shading, 1), .5, 1.2)[:, :, None])[tie]
    rgb[buttons] = (np.array([.06, .04, .028], np.float32)*np.clip(lum/.08, .4, 1.3)[:, :, None])[buttons]

    # Suede elbow patches on the back of each sleeve, painted through the sleeve UVs.
    bpy.context.view_layer.update()
    elbows = [rig.matrix_world @ rig.pose.bones['lowerarm01.'+side].head for side in 'LR']
    values = []
    for v in suit.data.vertices:
        best = 0
        for elbow in elbows:
            offset = v.co - elbow
            behind = smooth(-.005, .02, offset.y)  # The back of the arm faces +y.
            radius = math.hypot(offset.x*.9, offset.y*.6, offset.z/1.25)
            best = max(best, behind*smooth(.062, .052, radius))
        values.append(best)
    patch, _ = rasterize(suit.data, values, width, height, soften=3)
    patch = np.clip(patch*1.6, 0, 1)*jacket
    suede = np.array([.115, .072, .045], np.float32)*(.9 + .2*blur(rng.random((height, width)).astype(np.float32), 2))[:, :, None]
    rgb += (suede*shading - rgb)*patch[:, :, None]

    tweed = new_image('Herringbone tweed blazer', rgb)
    material = suit.active_material
    material.name = 'Herringbone tweed blazer'
    nodes = material.node_tree.nodes
    texture = next(n for n in nodes if n.type == 'TEX_IMAGE'); texture.image = tweed
    shader = nodes.get('Principled BSDF')
    shader.inputs['Roughness'].default_value = .9
    shader.inputs['Specular IOR Level'].default_value = .2

    # Gold embroidered GG shield on the left breast, laid on the jacket surface.
    vertices = [v.co.copy() for v in suit.data.vertices]
    surface = BVHTree.FromPolygons(vertices, [list(p.vertices) for p in suit.data.polygons])
    nearest = KDTree(len(vertices))
    for i, co in enumerate(vertices): nearest.insert(co, i)
    nearest.balance()

    def on_jacket(x, z, lift):
        hit, normal, _, _ = surface.ray_cast(Vector((x, -1, z)), Vector((0, 1, 0)))
        if hit is None: raise ValueError('Emblem misses the jacket')
        return hit - Vector((0, lift, 0))

    def gold():
        mat = bpy.data.materials.get('Antique gold embroidery') or bpy.data.materials.new('Antique gold embroidery')
        mat.use_nodes = True
        shader = mat.node_tree.nodes.get('Principled BSDF')
        shader.inputs['Base Color'].default_value = (.57, .40, .16, 1); shader.inputs['Roughness'].default_value = .8
        return mat

    def attach(obj):
        for c in list(obj.users_collection): c.objects.unlink(obj)
        collection.objects.link(obj); obj.parent = rig
        groups = {g.name: obj.vertex_groups.new(name=g.name) for g in suit.vertex_groups}
        for v in obj.data.vertices:
            _, index, _ = nearest.find(v.co)
            for g in suit.data.vertices[index].groups:
                groups[suit.vertex_groups[g.group].name].add([v.index], g.weight, 'REPLACE')
        obj.modifiers.new('Follow blazer', 'ARMATURE').object = rig
        return obj

    cx, cz = .112, 1.300
    shield = [(-.021, .018), (.021, .018), (.019, -.007), (0, -.023), (-.019, -.007)]
    points, faces = [], []
    for a, b in zip(shield, shield[1:]+shield[:1]):
        a, b = Vector(a), Vector(b); delta = b-a
        normal = Vector((-delta.y, delta.x)).normalized()*.0005
        count = max(1, round(delta.length/.0024))
        for i in range(count):
            start = len(points)
            lo, hi = a+delta*(i/count), a+delta*((i+.65)/count)
            points.extend(on_jacket(cx+v.x, cz+v.y, .0015) for v in [lo-normal, lo+normal, hi+normal, hi-normal])
            faces.append(tuple(start+j for j in range(4)))
    data = bpy.data.meshes.new('GG emblem border'); data.from_pydata(points, [], faces); data.update()
    data.materials.append(gold())
    border = bpy.data.objects.new('GG emblem border', data); collection.objects.link(border); attach(border)
    lettering = bpy.data.curves.new('GG emblem', 'FONT'); lettering.body = 'GG'
    lettering.align_x = 'CENTER'; lettering.align_y = 'CENTER'; lettering.size = .017
    lettering.extrude = .0003; lettering.bevel_depth = .00015; lettering.resolution_u = 4
    text = bpy.data.objects.new('GG emblem', lettering); collection.objects.link(text)
    text.location = on_jacket(cx, cz+.001, .0022); text.rotation_euler = (math.pi/2, 0, 0)
    text.data.materials.append(gold())
    bpy.ops.object.select_all(action='DESELECT'); text.select_set(True); bpy.context.view_layer.objects.active = text
    bpy.ops.object.convert(target='MESH'); text = bpy.context.object
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True); attach(text)
    return suit

"""Build a stylized, photo-directed male avatar for local review.

Blender -b --factory-startup -P scripts/avatar/build-likeness.py -- SOURCE OUTPUT.glb [PORTRAIT.png]
Uses the existing CC0 anatomy, costume fitting, and all 23 production morphs.
The reference photograph is art direction only; no photograph is embedded.
"""
from pathlib import Path
import argparse
import math
import sys

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree

sys.path.insert(0, str(Path(__file__).parent))
import human_mesh as human
from avatar_rig import Poser, bake, export, morph_poses, render_portrait
from avatar_textures import blur, image_pixels, new_image, rasterize, smooth
from mei_outfit import attach, dress, neckline, weights_from

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("source")
parser.add_argument("output")
parser.add_argument("portrait", nargs="?")
settings = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
human.SOURCE = Path(settings.source).resolve()
out = Path(settings.output).resolve()
bpy.ops.object.select_all(action="SELECT")
bpy.ops.object.delete(use_global=False)

# A softly square jaw and young-adult cheeks with natural eye proportions.
sculpt = [
    ("caucasian-male-young.target", 1),
    ("universal-male-young-averagemuscle-averageweight.target", 1),
    ("head-square.target", .20), ("head-round.target", .18),
    ("chin-width-incr.target", .10), ("chin-height-decr.target", .15),
    ("chin-prominent-incr.target", .12),
    ("mouth-scale-horiz-incr.target", .08),
    ("mouth-upperlip-volume-incr.target", .06),
    ("mouth-lowerlip-volume-incr.target", .08),
    ("eyebrows-trans-down.target", .15),
    ("nose-scale-vert-decr.target", .08),
    ("nose-point-width-decr.target", .15),
]
for side in "lr":
    sculpt.extend([
        (side + "-cheek-volume-incr.target", .12),
        (side + "-cheek-bones-incr.target", .08),
        (side + "-eye-scale-incr.target", .20),
        (side + "-ear-flap-decr.target", .20),
    ])

skin_path = human.SOURCE / "system/skins/young_caucasian_male/young_lightskinned_male_diffuse.png"


def character(assets):
    return human.build_character(
        targets=sculpt, eye_color="brownlight", height_metres=1.72,
        skin_path=skin_path, asset_specs=assets,
    )


# Preserve the neck/chest under the suit's original shirt for the open collar.
character([])
full = bpy.data.objects["Face, neck and hands"]
full.name = "Unclothed fitting surface"
for collection in list(full.users_collection):
    collection.objects.unlink(full)
bpy.context.scene.collection.objects.link(full)
full_mesh = full.data.copy()
rig = character([
    ("Dark blazer", "clothes/male_elegantsuit01", "male_elegantsuit01", .88, False),
    ("Short swept hair", "hair/short02", "short02", .8, True),
    ("Eyebrows", "eyebrows/eyebrow009", "eyebrow009", .88, True),
    ("Teeth", "teeth/teeth_base", "teeth_base", .5, False),
    ("Tongue", "tongue/tongue01", "tongue01", .7, False),
])
clothed = bpy.data.objects["Face, neck and hands"]
visible = {tuple(round(c, 4) for c in polygon.center) for polygon in clothed.data.polygons}
bm = bmesh.new()
bm.from_mesh(full.data)
hidden = [face for face in bm.faces
          if tuple(round(c, 4) for c in face.calc_center_median()) not in visible]
bmesh.ops.delete(bm, geom=[face for face in hidden
                         if max(v.co.z - neckline(v.co.x, v.co.y) for v in face.verts) < -.004],
                 context="FACES")
bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
bm.to_mesh(full.data)
bm.free()
skin_material = clothed.active_material
bpy.data.objects.remove(clothed, do_unlink=True)
body = full
body.name = "Face, neck and hands"
collection = bpy.data.collections["Player"]
for original_collection in list(body.users_collection):
    original_collection.objects.unlink(body)
collection.objects.link(body)
body.parent = rig
body.data.materials[0] = skin_material
next(mod for mod in body.modifiers if mod.type == "ARMATURE").object = rig


def shader(material):
    return next(node for node in material.node_tree.nodes if node.type == "BSDF_PRINCIPLED")


def flat_material(name, colour, roughness=.8, metallic=0):
    material = bpy.data.materials.new(name)
    material.use_nodes = True
    bsdf = shader(material)
    bsdf.inputs["Base Color"].default_value = (*colour, 1)
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Specular IOR Level"].default_value = .25
    return material


def mesh_object(name, coords, faces, material, lookup=None):
    data = bpy.data.meshes.new(name)
    data.from_pydata(coords, [], faces)
    data.update()
    data.materials.append(material)
    for polygon in data.polygons:
        polygon.use_smooth = True
    obj = bpy.data.objects.new(name, data)
    return attach(obj, rig, lookup or (lambda point: [("head", 1)]), collection, "Follow anatomy")


# Retain the source's lip and eyelid colours while removing fine skin detail.
# A flat colour loses those landmarks and makes the face read like a mask.
size = 1024
skin_nodes = skin_material.node_tree.nodes
texture = next(node for node in skin_nodes if node.type == "TEX_IMAGE")
source_skin = texture.image.copy()
source_skin.scale(size, size)
source_rgb = image_pixels(source_skin)[:, :, :3]
soft_rgb = np.stack([blur(source_rgb[:, :, c], 3) for c in range(3)], axis=2)
skin_rgb = .72 * soft_rgb * np.array([.96, .98, 1.02], np.float32) \
    + .28 * np.array([.83, .67, .60], np.float32)


def paint(name, values, colour, opacity=1):
    mask, _ = rasterize(body.data, values, size, size,
                        keep=lambda p: max(body.data.vertices[i].co.z for i in p.vertices) > 1.42,
                        soften=3)
    skin_rgb[:] += (np.array(colour, np.float32) - skin_rgb) * (mask * opacity)[:, :, None]


def lip_mask(co):
    return (1 - smooth(.70, 1.1, math.hypot(co.x / .026, (co.z - 1.552) / .013))) \
        * smooth(-.10, -.13, co.y)


paint("Cheeks", [
    math.exp(-((abs(v.co.x) - .047) / .026) ** 2 - ((v.co.z - 1.596) / .022) ** 2)
    * smooth(-.07, -.11, v.co.y) for v in body.data.vertices
], (.86, .57, .52), .08)


def beard(co):
    x, y, z = co
    ax = abs(x)
    edge = float(np.interp(ax, [0, .022, .038, .068], [1.578, 1.577, 1.570, 1.615]))
    edge += .02 * smooth(-.075, -.04, y)
    upper = 1 - smooth(edge - .014, edge + .004, z)
    lower = smooth(1.487 + .30 * max(0, ax - .01), 1.504 + .30 * max(0, ax - .01), z)
    return upper * lower * smooth(-.03, -.065, y) * (1 - lip_mask(co))


paint("Light stubble", [beard(v.co) for v in body.data.vertices], (.30, .24, .21), .23)
texture.image = new_image("Soft painted complexion and light stubble", skin_rgb)
bpy.data.images.remove(source_skin)
shader(skin_material).inputs["Roughness"].default_value = .72
shader(skin_material).inputs["Specular IOR Level"].default_value = .25

# The source short cut has a raised, swept front. Keep its broad silhouette and
# soften the strand detail, instead of the longer fringe used by Rowan.
hair = bpy.data.objects["Short swept hair"]
hair.vertex_groups.clear()
hair.vertex_groups.new(name="head").add(list(range(len(hair.data.vertices))), 1, "REPLACE")
hair_material = hair.active_material
hair_texture = next(node for node in hair_material.node_tree.nodes if node.type == "TEX_IMAGE")
pixels = image_pixels(hair_texture.image)
lum = pixels[:, :, :3].mean(2)
shade = np.clip(blur(lum, 3) / max(float(np.percentile(lum, 70)), .01), .5, 1.45)
pixels[:, :, :3] = shade[:, :, None] * np.array([.17, .115, .075])
hair_texture.image = new_image("Soft dark brown hair", pixels[:, :, :3], alpha=pixels[:, :, 3])
shader(hair_material).inputs["Roughness"].default_value = .85
shader(hair_material).inputs["Specular IOR Level"].default_value = .18
for vertex in hair.data.vertices:
    # Keep the sides short around the ears and add a small lift at the front.
    co = vertex.co
    front = smooth(-.055, -.12, co.y) * smooth(1.62, 1.70, co.z)
    co.z += .014 * front
hair.data.update()
fitting_surface = BVHTree.FromPolygons(
    [v.co.copy() for v in full_mesh.vertices], [list(p.vertices) for p in full_mesh.polygons])

# Soft textured brow edges avoid the jagged black strip of a flat cutout.
brow_material = bpy.data.objects["Eyebrows"].active_material
brow_shader = shader(brow_material)
brow_texture = next(node for node in brow_material.node_tree.nodes if node.type == "TEX_IMAGE")
brow_pixels = image_pixels(brow_texture.image)
brow_lum = blur(brow_pixels[:, :, :3].mean(2), 1)
brow_shade = np.clip(brow_lum / max(float(np.percentile(brow_lum, 70)), .01), .65, 1.3)
brow_texture.image = new_image("Soft brown brows",
                              brow_shade[:, :, None] * np.array([.20, .14, .11]),
                              alpha=blur(brow_pixels[:, :, 3], 1))
brow_shader.inputs["Roughness"].default_value = .9

eye_texture = next(node for node in bpy.data.objects["Eyes"].active_material.node_tree.nodes
                   if node.type == "TEX_IMAGE")
eye_pixels = image_pixels(eye_texture.image)
height, width, _ = eye_pixels.shape
rows, columns = np.mgrid[:height, :width]
pupils = (eye_pixels[:, :, :3].mean(2) < .12) & (abs(columns / width - .5) < .35) & (abs(rows / height - .5) < .35)
for half in (columns < width / 2, columns >= width / 2):
    cy, cx = rows[pupils & half].mean(), columns[pupils & half].mean()
    iris = np.clip((.118 - np.hypot(rows - cy, columns - cx) / width) / .014, 0, 1)
    eye_pixels[:, :, :3] *= 1 - iris[:, :, None] * np.array([.30, .34, .38])
eye_texture.image = new_image("Warm brown eyes", eye_pixels[:, :, :3])

# Fit the existing open-neck blazer, then style it as a matte men's suit.
blazer = bpy.data.objects["Dark blazer"]
shirt = dress(blazer, rig, full_mesh, body,
              human.SOURCE / "system/clothes/male_elegantsuit01/male_elegantsuit01_diffuse.png")
bpy.data.objects.remove(bpy.data.objects["GG lapel pin"], do_unlink=True)
shirt.name = "Open collar white shirt"
white = flat_material("Warm white cotton", (.78, .755, .69), .88)
# Retain the source's broad cloth normals, with much less fine detail.
blazer_shader = shader(blazer.active_material)
for link in list(blazer_shader.inputs["Base Color"].links):
    blazer.active_material.node_tree.links.remove(link)
blazer_shader.inputs["Base Color"].default_value = (.016, .021, .028, 1)
blazer_shader.inputs["Roughness"].default_value = .9
for node in blazer.active_material.node_tree.nodes:
    if node.type == "NORMAL_MAP":
        node.inputs["Strength"].default_value = 0
shirt.data.materials.clear()
shirt.data.materials.append(white)

nearest = KDTree(len(blazer.data.vertices))
for vertex in blazer.data.vertices:
    nearest.insert(vertex.co, vertex.index)
nearest.balance()
cloth_weights = weights_from(blazer, nearest)
body_nearest = KDTree(len(body.data.vertices))
for vertex in body.data.vertices:
    body_nearest.insert(vertex.co, vertex.index)
body_nearest.balance()
skin_weights = weights_from(body, body_nearest)


def collar_weights(point):
    mix = smooth(1.43, 1.49, point.z)
    weights = {}
    for source, factor in ((cloth_weights, 1 - mix), (skin_weights, mix)):
        for name, weight in source(point):
            weights[name] = weights.get(name, 0) + weight * factor
    total = sum(weights.values())
    return [(name, weight / total) for name, weight in weights.items()]


NECK_CENTER_Y = -.024
COLLAR_OPENING = math.radians(24)


def neck_angle(co):
    return math.atan2(abs(co.x), -(co.y - NECK_CENTER_Y))


def collar_base(angle):
    return float(np.interp(abs(math.degrees(angle)),
                           [0, 10, 24, 45, 90, 135, 180],
                           [1.447, 1.455, 1.473, 1.479, 1.485, 1.491, 1.493]))


def neck_surface(angle, z, clearance):
    radial = Vector((math.sin(angle), -math.cos(angle), 0))
    center = Vector((0, NECK_CENTER_Y, z))
    hit, _, _, _ = fitting_surface.ray_cast(center, radial, .3)
    if hit is None:
        raise ValueError(f"Collar missed the fitting surface at {angle=}, {z=}")
    return hit + radial * clearance


# Replace the low blouse neckline with one continuous open-collar shirt surface.
bm = bmesh.new()
bm.from_mesh(full_mesh)
bmesh.ops.delete(bm, geom=[face for face in bm.faces
                          if not any(1.10 < v.co.z < 1.52 for v in face.verts)
                          or any(abs(v.co.x) > .19 for v in face.verts)], context="FACES")
bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
bmesh.ops.triangulate(bm, faces=bm.faces[:])
bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=1, use_grid_fill=True)
bm.normal_update()
for vertex in bm.verts:
    vertex.co += vertex.normal * .0035
def shirt_edge(co):
    return collar_base(neck_angle(co))


field = {v: v.co.z - shirt_edge(v.co) for v in bm.verts}
coords, faces, lookup = [], [], {}
for face in bm.faces:
    ring = list(face.verts)
    clipped = []
    for a, b in zip(ring, ring[1:] + ring[:1]):
        if field[a] <= 0:
            clipped.append(a.co.copy())
        if (field[a] <= 0) != (field[b] <= 0):
            clipped.append(a.co.lerp(b.co, field[a] / (field[a] - field[b])))
    if len(clipped) < 3:
        continue
    indices = []
    for co in clipped:
        key = tuple(round(c, 6) for c in co)
        if key not in lookup:
            lookup[key] = len(coords)
            coords.append(co)
        indices.append(lookup[key])
    faces.append(indices)
bm.free()
bpy.data.objects.remove(shirt, do_unlink=True)
shirt = mesh_object("Open collar white shirt", coords, faces, white, collar_weights)
white.use_backface_culling = False

# A collar stand follows the full neck circumference, open at the throat.
# Its turned-down leaf shares the fold line and falls into two front points.
# Dense fitted strips give the sides/back a curved roll instead of flat tabs.
angles = np.linspace(COLLAR_OPENING, 2 * math.pi - COLLAR_OPENING, 81)
stand_coords, leaf_coords = [], []
stand_rows, leaf_rows = 5, 7
for angle in angles:
    side_angle = min(angle, 2 * math.pi - angle)
    degrees = math.degrees(side_angle)
    base_z = collar_base(side_angle)
    fold_z = base_z + float(np.interp(degrees, [24, 90, 180], [.018, .019, .019]))
    radial = Vector((math.sin(angle), -math.cos(angle), 0))
    fold = neck_surface(angle, fold_z, .0045)
    for row in range(stand_rows):
        t = row / (stand_rows - 1)
        z = (base_z - .002) * (1 - t) + fold_z * t
        stand_coords.append(neck_surface(angle, z, .0035 + .001 * t))

    outer_z = float(np.interp(degrees, [24, 40, 65, 90, 135, 180],
                             [1.442, 1.458, 1.473, 1.482, 1.490, 1.492]))
    outer = neck_surface(angle, outer_z, .0055)
    # Spread each front point across the shirt, then taper into the neck roll.
    flare = 1 - smooth(24, 55, degrees)
    outer.x += math.copysign(.025 * flare, outer.x)
    for row in range(leaf_rows):
        t = row / (leaf_rows - 1)
        co = fold.lerp(outer, t)
        co += radial * (.0015 * math.sin(math.pi * t))
        fitted_angle = math.atan2(co.x, -(co.y - NECK_CENTER_Y))
        minimum = neck_surface(fitted_angle, co.z, .0035)
        center = Vector((0, NECK_CENTER_Y, co.z))
        if (co - center).length < (minimum - center).length:
            co = minimum
        leaf_coords.append(co)


def collar_strip(name, coords, rows, reverse=False):
    faces = []
    for section in range(len(angles) - 1):
        for row in range(rows - 1):
            a = section * rows + row
            face = (a, a + rows, a + rows + 1, a + 1)
            faces.append(tuple(reversed(face)) if reverse else face)
    obj = mesh_object(name, coords, faces, white, collar_weights)
    edge = obj.modifiers.new("Cotton edge", "SOLIDIFY")
    edge.thickness = .001
    edge.offset = -.5
    return obj


collar_strip("Collar stand around neck", stand_coords, stand_rows)
collar_strip("Folded shirt collar", leaf_coords, leaf_rows, reverse=True)

lapel_material = flat_material("Suit lapel facing", (.018, .023, .030), .9)
lapel_surface = BVHTree.FromPolygons(
    [v.co.copy() for v in blazer.data.vertices], [list(p.vertices) for p in blazer.data.polygons])


def jacket_front(x, z):
    start, direction = Vector((x, -1, z)), Vector((0, 1, 0))
    suit_hit, _, _, _ = lapel_surface.ray_cast(start, direction)
    skin_hit, _, _, _ = fitting_surface.ray_cast(start, direction)
    if skin_hit is None:
        skin_hit, _, _, _ = fitting_surface.find_nearest(Vector((x, -.08, z)))
    depth = skin_hit.y - .012
    if suit_hit is not None:
        depth = min(depth, suit_hit.y)
    return Vector((x, depth - .002, z))


for sign in (-1, 1):
    # Begin the added facing below the shirt collar so it tucks under the fold.
    # The source blazer already supplies the curved seam around the neck.
    coords = [Vector((sign * x, 0, z)) for x, z in
              [(.074, 1.455), (.096, 1.393),
               (.080, 1.383), (.104, 1.366), (.023, 1.185), (.052, 1.338)]]
    faces = [(0, 1, 5), (1, 2, 5), (2, 3, 5), (3, 4, 5)]
    if sign == -1:
        faces = [tuple(reversed(face)) for face in faces]
    lapel = mesh_object("Notched suit lapel", coords, faces, lapel_material, cloth_weights)
    # Densely fit the whole panel to the curved chest, not just its corners.
    bm = bmesh.new()
    bm.from_mesh(lapel.data)
    bmesh.ops.subdivide_edges(bm, edges=bm.edges[:], cuts=4, use_grid_fill=True)
    for vertex in bm.verts:
        vertex.co = jacket_front(vertex.co.x, vertex.co.z)
    bm.to_mesh(lapel.data)
    bm.free()
    lapel.vertex_groups.clear()
    attach(lapel, rig, cloth_weights, collection, "Follow lapel")
    # attach() supplies the one armature modifier this refitted mesh needs.
    for modifier in list(lapel.modifiers)[:-1]:
        lapel.modifiers.remove(modifier)
    lapel.modifiers.new("Tailored lapel edge", "SOLIDIFY").thickness = .0008

# The GG shield and letters follow the chest through breathing and shoulder poses.
suit_surface = BVHTree.FromPolygons(
    [v.co.copy() for v in blazer.data.vertices], [list(p.vertices) for p in blazer.data.polygons])
badge_center, normal, _, _ = suit_surface.ray_cast(Vector((.108, -1, 1.300)), Vector((0, 1, 0)))
if badge_center is None:
    raise ValueError("GG shield missed the blazer")
if normal.y > 0:
    normal = -normal
up = (Vector((0, 0, 1)) - normal * normal.z).normalized()
right = up.cross(normal)
gold = flat_material("Antique gold GG embroidery", (.56, .35, .10), .68, .35)
patch_material = flat_material("Dark green shield backing", (.012, .025, .019), .94)
shield = [(-.025, .022), (.025, .022), (.023, -.008), (0, -.030), (-.023, -.008)]
coords = [badge_center + right * x + up * z + normal * .0018 for x, z in shield]
mesh_object("GG shield backing", coords, [tuple(range(4, -1, -1))], patch_material, cloth_weights)
border_coords, border_faces = [], []
for a, b in zip(shield, shield[1:] + shield[:1]):
    a, b = Vector(a), Vector(b)
    segment = b - a
    perpendicular = Vector((-segment.y, segment.x)).normalized() * .00065
    count = max(1, round(segment.length / .002))
    for index in range(count):
        start = len(border_coords)
        lo = a + segment * index / count
        hi = a + segment * (index + .78) / count
        for point in (lo - perpendicular, lo + perpendicular, hi + perpendicular, hi - perpendicular):
            border_coords.append(badge_center + right * point.x + up * point.y + normal * .0024)
        border_faces.append((start, start + 1, start + 2, start + 3))
mesh_object("Gold shield stitching", border_coords, border_faces, gold, cloth_weights)
lettering = bpy.data.curves.new("GG chest monogram", "FONT")
lettering.body = "GG"
lettering.align_x = "CENTER"
lettering.align_y = "CENTER"
lettering.size = .023
lettering.extrude = .00025
lettering.resolution_u = 4
letters = bpy.data.objects.new("GG chest monogram", lettering)
collection.objects.link(letters)
letters.location = badge_center + normal * .003
letters.rotation_euler = Matrix((right, up, normal)).transposed().to_euler()
letters.data.materials.append(gold)
bpy.ops.object.select_all(action="DESELECT")
letters.select_set(True)
bpy.context.view_layer.objects.active = letters
bpy.ops.object.convert(target="MESH")
letters = bpy.context.object
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
attach(letters, rig, cloth_weights, collection, "Follow jacket")

poser = Poser(rig)
poser.relax_arms()
resting_lid = .04
for side in ("Left", "Right"):
    poser.expression(side + "UpperLidClosed", resting_lid)
originals = [obj for obj in collection.objects if obj.type == "MESH"]
for obj in originals:
    if obj.name in ("Face, neck and hands", "Eyes", "Dark blazer", "Open collar white shirt"):
        modifier = obj.modifiers.new("Portrait smoothing", "SUBSURF")
        modifier.levels = 1
        modifier.render_levels = 1
exported = bake(rig, originals, morph_poses(poser, resting_lid=resting_lid), "Likeness preview")

# A modestly larger head reads as a character. Transform every shape key together,
# so expressions and attached hair retain their alignment.
for _, obj in exported:
    for key in obj.data.shape_keys.key_blocks:
        for point in key.data:
            co = point.co
            weight = smooth(1.46, 1.53, co.z)
            co.x *= 1 + .05 * weight
            co.y = -.022 + (co.y + .022) * (1 + .03 * weight)
            co.z = 1.50 + (co.z - 1.50) * (1 + .025 * weight)

export(exported, out, "MakeHuman CC0 anatomy, short hair, eyes, brows and suit; original GlobeGlotter styling. No reference photo pixels.",
       cutout=("Short swept hair",), cutoff=.35)
if settings.portrait:
    render_portrait(exported, settings.portrait, target=Vector((0, -.015, 1.49)), scale=.65)
print("EXPORTED", out)

"""Render Rain's library background with an antique globe.
blender --factory-startup -b -P build-library.py -- SOURCE_DIRECTORY OUTPUT.jpg [PERCENT] [SAMPLES]
Pinned Poly Haven CC0 assets and NASA Blue Marble imagery (library-source.json) are downloaded
into SOURCE_DIRECTORY and verified. The window view reuses the courtyard background.
"""
import bpy, bmesh, hashlib, json, math, random, sys, urllib.request
from pathlib import Path
from mathutils import Vector, Matrix

args = sys.argv[sys.argv.index('--') + 1:]
assets, out = Path(args[0]).resolve(), Path(args[1]).resolve()
percent = int(args[2]) if len(args) > 2 else 100
samples = int(args[3]) if len(args) > 3 else 256
rng = random.Random(7)
here = Path(__file__).resolve().parent
courtyard = here.parents[1] / 'Glosify/wwwroot/images/avatar/courtyard.jpg'

for entry in json.loads((here / 'library-source.json').read_text())['files']:
    path = assets / entry['path']
    if not path.exists() or hashlib.sha256(path.read_bytes()).hexdigest() != entry['sha256']:
        request = urllib.request.Request(entry['url'], headers={'User-Agent': 'GlobeGlotter-avatar-build'})
        data = urllib.request.urlopen(request, timeout=60).read()
        if hashlib.sha256(data).hexdigest() != entry['sha256']: raise ValueError('Asset checksum mismatch: ' + entry['path'])
        path.parent.mkdir(parents=True, exist_ok=True); path.write_bytes(data)

bpy.ops.object.select_all(action='SELECT'); bpy.ops.object.delete()
scene = bpy.context.scene
ROOM_X, BACK_Y, CEIL = 2.7, 7.2, 4.4

def material(name, color, roughness=.6, **inputs):
    mat = bpy.data.materials.new(name); mat.use_nodes = True
    bsdf = next(n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    bsdf.inputs['Base Color'].default_value = (*color, 1); bsdf.inputs['Roughness'].default_value = roughness
    for key, value in inputs.items(): bsdf.inputs[key].default_value = value
    return mat

def textured(name, folder, scale, tint=(1, 1, 1), roughness=None):
    mat = bpy.data.materials.new(name); mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = next(n for n in nodes if n.type == 'BSDF_PRINCIPLED')
    coords = nodes.new('ShaderNodeTexCoord'); mapping = nodes.new('ShaderNodeMapping')
    mapping.inputs['Scale'].default_value = (scale, scale, scale); links.new(coords.outputs['Object'], mapping.inputs['Vector'])
    def image(file, color=True):
        node = nodes.new('ShaderNodeTexImage'); node.image = bpy.data.images.load(str(assets / folder / file))
        node.projection = 'BOX'; node.projection_blend = .2
        if not color: node.image.colorspace_settings.name = 'Non-Color'
        links.new(mapping.outputs['Vector'], node.inputs['Vector']); return node
    mix = nodes.new('ShaderNodeMix'); mix.data_type = 'RGBA'; mix.blend_type = 'MULTIPLY'; mix.inputs['Factor'].default_value = 1
    links.new(image('Diffuse.jpg').outputs['Color'], mix.inputs[6]); mix.inputs[7].default_value = (*tint, 1)
    links.new(mix.outputs[2], bsdf.inputs['Base Color'])
    if roughness is None: links.new(image('Rough.jpg', False).outputs['Color'], bsdf.inputs['Roughness'])
    else: bsdf.inputs['Roughness'].default_value = roughness
    normal = nodes.new('ShaderNodeNormalMap'); normal.inputs['Strength'].default_value = .6
    links.new(image('nor_gl.jpg', False).outputs['Color'], normal.inputs['Color']); links.new(normal.outputs['Normal'], bsdf.inputs['Normal'])
    return mat

def box(name, lo, hi, mat, parent=None):
    lo, hi = Vector(lo), Vector(hi)
    bpy.ops.mesh.primitive_cube_add(size=1, location=(lo + hi) / 2); obj = bpy.context.object
    obj.name = name; obj.scale = hi - lo; bpy.ops.object.transform_apply(scale=True)
    obj.data.materials.append(mat)
    if parent: obj.parent = parent
    return obj

def bar(name, a, b, radius, mat, vertices=16):
    a, b = Vector(a), Vector(b)
    bpy.ops.mesh.primitive_cylinder_add(vertices=vertices, radius=radius, depth=(b - a).length, location=(a + b) / 2)
    obj = bpy.context.object; obj.name = name
    obj.rotation_euler = (b - a).to_track_quat('Z', 'Y').to_euler(); obj.data.materials.append(mat)
    bpy.ops.object.shade_smooth(); return obj

paint = material('Ivory paint', (.80, .73, .60), .82)
trim = material('Ivory trim', (.86, .80, .68), .45)
walnut = textured('Walnut', 'textures/dark_wood', 1.4, (.48, .32, .23))
floor_wood = textured('Parquet', 'textures/diagonal_parquet', .5, (.34, .27, .22), .6)
brass = material('Brass', (.78, .56, .26), .28, **{'Metallic': 1})
velvet = material('Green velvet', (.012, .045, .03), .7, **{'Sheen Weight': .35, 'Sheen Tint': (.5, .75, .6, 1)})

# Room shell with an arched window in the back wall.
box('Floor', (-ROOM_X, -1, -.1), (ROOM_X, BACK_Y, 0), floor_wood)
box('Left wall', (-ROOM_X - .2, -1, 0), (-ROOM_X, BACK_Y, CEIL), paint)
box('Right wall', (ROOM_X, -1, 0), (ROOM_X + .2, BACK_Y, CEIL), paint)
box('Ceiling', (-ROOM_X, -1, CEIL), (ROOM_X, BACK_Y, CEIL + .1), paint)
wall = box('Back wall', (-ROOM_X, BACK_Y, 0), (ROOM_X, BACK_Y + .25, CEIL), paint)
W, SILL, SPRING = .8, .72, 2.72  # half width, sill height, arch spring line
cut_box = box('Window cut', (-W, BACK_Y - .1, SILL), (W, BACK_Y + .4, SPRING), paint)
bpy.ops.mesh.primitive_cylinder_add(vertices=96, radius=W, depth=.6, location=(0, BACK_Y + .1, SPRING), rotation=(math.pi / 2, 0, 0))
cut_arch = bpy.context.object
for cutter in (cut_box, cut_arch):
    mod = wall.modifiers.new('Window', 'BOOLEAN'); mod.operation = 'DIFFERENCE'; mod.solver = 'EXACT'; mod.object = cutter
    cutter.hide_render = True; cutter.hide_viewport = True
# Painted glazing bars: a rectangular lower sash and a fanlight.
y_bar = BACK_Y + .12
for x in (-.27, .27): box('Mullion', (x - .018, y_bar - .02, SILL), (x + .018, y_bar + .02, SPRING), trim)
for z in (SILL + .64, SILL + 1.3, SPRING): box('Transom', (-W, y_bar - .02, z - .018), (W, y_bar + .02, z + .018), trim)
for angle in (30, 60, 90, 120, 150):
    a = math.radians(angle); inner = Vector((math.cos(a) * .26, y_bar, SPRING + math.sin(a) * .26)); outer = Vector((math.cos(a) * W, y_bar, SPRING + math.sin(a) * W))
    bar('Fan bar', inner, outer, .016, trim, 8)
for radius, thickness in ((.26, .018), (W - .01, .04)):
    bpy.ops.mesh.primitive_torus_add(major_radius=radius, minor_radius=thickness, major_segments=96, minor_segments=8, location=(0, y_bar, SPRING), rotation=(math.pi / 2, 0, 0))
    ring = bpy.context.object; ring.data.materials.append(trim)
    bm = bmesh.new(); bm.from_mesh(ring.data); bmesh.ops.delete(bm, geom=[v for v in bm.verts if (ring.matrix_world @ v.co).z < SPRING - .001], context='VERTS'); bm.to_mesh(ring.data); bm.free()
box('Sill', (-W - .12, BACK_Y - .1, SILL - .06), (W + .12, BACK_Y + .2, SILL), trim)

# Sunlit view through the window: the existing courtyard portrait, far out of focus.
view_mat = bpy.data.materials.new('Courtyard view'); view_mat.use_nodes = True
nodes, links = view_mat.node_tree.nodes, view_mat.node_tree.links
for n in list(nodes): nodes.remove(n)
tex = nodes.new('ShaderNodeTexImage'); tex.image = bpy.data.images.load(str(courtyard))
emit = nodes.new('ShaderNodeEmission'); emit.inputs['Strength'].default_value = 3.2
outn = nodes.new('ShaderNodeOutputMaterial'); links.new(tex.outputs['Color'], emit.inputs['Color']); links.new(emit.outputs['Emission'], outn.inputs['Surface'])
bpy.ops.mesh.primitive_plane_add(size=1, location=(0, 11.5, 2.2), rotation=(math.pi / 2, 0, 0))
view = bpy.context.object; view.scale = (4.4, 5.86, 1); view.data.materials.append(view_mat)
view.visible_shadow = False

# Built-in bookcases. Local frame: u along the case, front at v=0, depth along +v, z up.
PALETTE = [((.33, .05, .04), 5), ((.06, .16, .09), 4), ((.05, .07, .17), 3), ((.42, .25, .12), 4), ((.22, .11, .05), 4),
           ((.66, .57, .43), 2), ((.035, .03, .03), 2), ((.45, .31, .08), 1), ((.30, .14, .17), 1)]
def book_color():
    color = rng.choices([c for c, _ in PALETTE], [w for _, w in PALETTE])[0]
    k = rng.uniform(.7, 1.15); return (*(min(1, c * k) for c in color), 1)
book_mat = bpy.data.materials.new('Bound books'); book_mat.use_nodes = True
bsdf = next(n for n in book_mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
attr = book_mat.node_tree.nodes.new('ShaderNodeAttribute'); attr.attribute_name = 'Col'
book_mat.node_tree.links.new(attr.outputs['Color'], bsdf.inputs['Base Color']); bsdf.inputs['Roughness'].default_value = .5
bsdf.inputs['Sheen Weight'].default_value = .3

def bookcase(name, length, location, rotation, depth=.38, height=3.55, bay=.88):
    frame = bpy.data.objects.new(name, None); scene.collection.objects.link(frame)
    frame.location = location; frame.rotation_euler = (0, 0, rotation)
    t = .035; bays = max(1, round(length / bay)); width = length / bays
    box('Back', (0, depth - .02, 0), (length, depth, height), walnut, frame)
    box('Plinth', (0, 0, 0), (length, depth, .12), walnut, frame)
    box('Crown', (-.03, -.05, height - .12), (length + .03, depth, height + .06), walnut, frame)
    box('Crown moulding', (-.05, -.08, height + .06), (length + .05, depth, height + .12), walnut, frame)
    for i in range(bays + 1): box('Upright', (i * width - t / 2, 0, 0), (i * width + t / 2, depth, height - .12), walnut, frame)
    levels = [.12 + k * .37 for k in range(9)]
    for z in levels: box('Shelf', (0, 0, z), (length, depth - .02, z + .03), walnut, frame)
    bm = bmesh.new(); colors = bm.loops.layers.color.new('Col')
    def add_book(center, size, lean=0):
        matrix = Matrix.Translation(center) @ Matrix.Rotation(lean, 4, 'Y') @ Matrix.Diagonal((*size, 1))
        geom = bmesh.ops.create_cube(bm, size=1, matrix=matrix)['verts']
        color = book_color()
        for face in {f for v in geom for f in v.link_faces}:
            for loop in face.loops: loop[colors] = color
    for i in range(bays):
        for z in levels[:-1]:
            base, clear = z + .03, .34
            u, end = i * width + t / 2 + .01, (i + 1) * width - t / 2 - .01
            while u < end - .03:
                if rng.random() < .07 and end - u > .3:  # a short stack lying flat
                    w, d = rng.uniform(.18, .26), rng.uniform(.15, .22); h = base
                    for _ in range(rng.randint(2, 5)):
                        th = rng.uniform(.025, .05); add_book(Vector((u + w / 2, .03 + d / 2, h + th / 2)), (w, d, th)); h += th
                    u += w + .02; continue
                if rng.random() < .05: u += rng.uniform(.04, .12); continue
                th, h, d = rng.uniform(.022, .06), clear * rng.uniform(.58, .93), rng.uniform(.15, .25)
                if u + th > end: break
                lean = 0
                if end - u - th < .06 and rng.random() < .6: lean = rng.uniform(.12, .3)
                add_book(Vector((u + th / 2 + math.sin(lean) * h / 2, .02 + (depth - .04 - d) * rng.uniform(0, .3) + d / 2, base + h * math.cos(lean) / 2)), (th, d, h), -lean)
                u += th + rng.uniform(0, .004)
    mesh = bpy.data.meshes.new(name + ' books'); bm.to_mesh(mesh); bm.free()
    books = bpy.data.objects.new(name + ' books', mesh); scene.collection.objects.link(books); books.parent = frame
    mesh.materials.append(book_mat)
    return frame

D = .38
bookcase('Back left case', ROOM_X - 1.22, (-ROOM_X, BACK_Y - D, 0), 0)
bookcase('Back right case', ROOM_X - 1.22, (1.22, BACK_Y - D, 0), 0)
bookcase('Left wall case', 3.0, (-ROOM_X + D, BACK_Y - D - 3.0, 0), math.pi / 2)
bookcase('Right wall case', 3.0, (ROOM_X - D, BACK_Y - D, 0), -math.pi / 2)

# Deep green drapes on a brass rod frame the window.
def drape(x0, x1):
    bpy.ops.mesh.primitive_grid_add(x_subdivisions=90, y_subdivisions=6, size=1)
    obj = bpy.context.object; obj.data.materials.append(velvet)
    for v in obj.data.vertices:
        u, h = v.co.x + .5, v.co.y + .5
        x = x0 + (x1 - x0) * u; fold = .07 * math.sin(u * math.pi * 7) + .025 * math.sin(u * math.pi * 17 + 1)
        v.co = Vector((x, BACK_Y - .1 + fold, h * 4.0))
    bpy.ops.object.shade_smooth(); return obj
drape(-1.2, -.6); drape(.6, 1.2)
bar('Curtain rod', (-1.16, BACK_Y - .12, 4.02), (1.16, BACK_Y - .12, 4.02), .02, brass)
for x in (-1.16, 1.16):
    bpy.ops.mesh.primitive_uv_sphere_add(radius=.045, location=(x, BACK_Y - .12, 4.02)); bpy.context.object.data.materials.append(brass)

# Poly Haven CC0 furniture.
def place(asset, location, rotation=0, scale=1):
    before = set(scene.objects)
    bpy.ops.import_scene.gltf(filepath=str(assets / 'models' / asset / f'{asset}.gltf'))
    new = [o for o in scene.objects if o not in before]
    holder = bpy.data.objects.new(asset, None); scene.collection.objects.link(holder)
    for o in new:
        if o.parent is None: o.parent = holder
    holder.location = location; holder.rotation_euler = (0, 0, rotation); holder.scale = (scale,) * 3
    return holder, new
place('GreenChair_01', (-1.82, 6.15, 0), math.radians(28))
TOP = .949 * .85
place('ClassicConsole_01', (1.62, 6.5, 0), 0, .85)
place('vintage_oil_lamp', (2.08, 6.46, TOP))
place('mantel_clock_01', (1.2, 6.52, TOP), math.radians(-8))
place('book_encyclopedia_set_01', (1.38, 6.56, TOP), math.radians(4))
_, chandelier = place('Chandelier_01', (0, 5.2, 3.95), 0, 1.15)
bar('Chandelier chain', (0, 5.2, 3.95), (0, 5.2, CEIL), .008, brass, 6)

# Glowing shades and flames, each with its own warm light.
for mat in bpy.data.materials:
    if mat.name.startswith('lamps_classic_chandelier') or 'flame' in mat.name:
        bsdf = next((n for n in mat.node_tree.nodes if n.type == 'BSDF_PRINCIPLED'), None)
        if bsdf:
            bsdf.inputs['Emission Color'].default_value = (1, .66, .36, 1)
            bsdf.inputs['Emission Strength'].default_value = 4 if 'flame' in mat.name else 1.6
def point(name, location, power, color=(1, .68, .4), radius=.05):
    data = bpy.data.lights.new(name, 'POINT'); data.energy = power; data.color = color; data.shadow_soft_size = radius
    obj = bpy.data.objects.new(name, data); scene.collection.objects.link(obj); obj.location = location
bpy.context.view_layer.update()
for obj in chandelier:
    if obj.type != 'MESH': continue
    shades = [i for i, slot in enumerate(obj.material_slots) if slot.material and slot.material.name.startswith('lamps_')]
    clusters = []
    for poly in obj.data.polygons:
        if poly.material_index in shades:
            c = obj.matrix_world @ poly.center
            near = next((k for k in clusters if (k[0] - c).xy.length < .1), None)
            if near: near.append(c)
            else: clusters.append([c])
    for i, pts in enumerate(clusters):
        point(f'Shade {i}', sum(pts, Vector()) / len(pts), 18)
point('Oil lamp', (2.08, 6.46, TOP + .55), 22, (1, .6, .3), .03)

# The antique floor globe: NASA Blue Marble (public domain) restyled as an aged map.
GX, GY, R, GZ = -1.62, 4.75, .3, .93
globe_mat = bpy.data.materials.new('Antique globe'); globe_mat.use_nodes = True
nodes, links = globe_mat.node_tree.nodes, globe_mat.node_tree.links
bsdf = next(n for n in nodes if n.type == 'BSDF_PRINCIPLED')
# Matte paper: a glossy finish catches the window light and washes out the oceans.
bsdf.inputs['Roughness'].default_value = .7; bsdf.inputs['Specular IOR Level'].default_value = .15
uv = nodes.new('ShaderNodeTexCoord')
earth = nodes.new('ShaderNodeTexImage'); earth.image = bpy.data.images.load(str(assets / 'earth.jpg')); earth.interpolation = 'Cubic'
# Encoded values separate the navy oceans from land more reliably than linear ones.
earth.image.colorspace_settings.name = 'Non-Color'
links.new(uv.outputs['UV'], earth.inputs['Vector'])
sep = nodes.new('ShaderNodeSeparateColor'); links.new(earth.outputs['Color'], sep.inputs['Color'])
sea = nodes.new('ShaderNodeMath'); sea.operation = 'SUBTRACT'; links.new(sep.outputs['Blue'], sea.inputs[0]); links.new(sep.outputs['Red'], sea.inputs[1])
mask = nodes.new('ShaderNodeMapRange'); mask.inputs['From Min'].default_value = .02; mask.inputs['From Max'].default_value = .07
links.new(sea.outputs['Value'], mask.inputs['Value'])
land = nodes.new('ShaderNodeMix'); land.data_type = 'RGBA'; land.blend_type = 'MIX'; land.inputs['Factor'].default_value = .25
land.inputs[6].default_value = (.62, .48, .27, 1); links.new(earth.outputs['Color'], land.inputs[7])
colour = nodes.new('ShaderNodeMix'); colour.data_type = 'RGBA'; links.new(mask.outputs['Result'], colour.inputs['Factor'])
links.new(land.outputs[2], colour.inputs[6]); colour.inputs[7].default_value = (.10, .21, .20, 1)
# Graticule every 15 degrees.
xyz = nodes.new('ShaderNodeSeparateXYZ'); links.new(uv.outputs['UV'], xyz.inputs['Vector'])
def lines(socket, count):
    scale = nodes.new('ShaderNodeMath'); scale.operation = 'MULTIPLY'; scale.inputs[1].default_value = count; links.new(socket, scale.inputs[0])
    wave = nodes.new('ShaderNodeMath'); wave.operation = 'PINGPONG'; wave.inputs[1].default_value = .5; links.new(scale.outputs['Value'], wave.inputs[0])
    edge = nodes.new('ShaderNodeMath'); edge.operation = 'LESS_THAN'; edge.inputs[1].default_value = .035; links.new(wave.outputs['Value'], edge.inputs[0])
    return edge.outputs['Value']
grid = nodes.new('ShaderNodeMath'); grid.operation = 'MAXIMUM'
links.new(lines(xyz.outputs['X'], 24), grid.inputs[0]); links.new(lines(xyz.outputs['Y'], 12), grid.inputs[1])
inked = nodes.new('ShaderNodeMix'); inked.data_type = 'RGBA'; links.new(grid.outputs['Value'], inked.inputs['Factor'])
links.new(colour.outputs[2], inked.inputs[6]); inked.inputs[7].default_value = (.24, .15, .08, 1)
varnish = nodes.new('ShaderNodeMix'); varnish.data_type = 'RGBA'; varnish.blend_type = 'MULTIPLY'; varnish.inputs['Factor'].default_value = 1
links.new(inked.outputs[2], varnish.inputs[6]); varnish.inputs[7].default_value = (1, .86, .62, 1)
links.new(varnish.outputs[2], bsdf.inputs['Base Color'])

assembly = bpy.data.objects.new('Globe axis', None); scene.collection.objects.link(assembly)
assembly.location = (GX, GY, GZ); assembly.rotation_euler = (0, math.radians(-23.5), math.radians(-35))
bpy.ops.mesh.primitive_uv_sphere_add(segments=128, ring_count=64, radius=R)
sphere = bpy.context.object; sphere.name = 'Globe'; sphere.data.materials.append(globe_mat); bpy.ops.object.shade_smooth()
sphere.parent = assembly
# Turn the Atlantic, Europe and Africa towards the viewer (about 5 E, 30 N).
uv_layer = sphere.data.uv_layers.active.data
target = min((l for l in sphere.data.loops), key=lambda l: (uv_layer[l.index].uv - Vector((.514, .67))).length)
p = sphere.data.vertices[target.vertex_index].co
view_dir = (assembly.matrix_world.to_3x3().inverted() @ Vector((0, -1, 0)))
sphere.rotation_euler = (0, 0, math.atan2(view_dir.y, view_dir.x) - math.atan2(p.y, p.x))
bpy.ops.mesh.primitive_torus_add(major_radius=R + .022, minor_radius=.009, major_segments=128, minor_segments=10, rotation=(math.pi / 2, 0, 0))
meridian = bpy.context.object; meridian.parent = assembly; meridian.data.materials.append(brass); bpy.ops.object.shade_smooth()
for s in (-1, 1):
    pin = bar('Axis pin', (0, 0, s * (R - .01)), (0, 0, s * (R + .05)), .007, brass, 8); pin.parent = assembly
stand_wood = textured('Globe stand', 'textures/dark_wood', 2.5, (.55, .33, .2), .35)
bpy.ops.mesh.primitive_torus_add(major_radius=R + .05, minor_radius=.035, major_segments=128, minor_segments=12, location=(GX, GY, GZ))
horizon = bpy.context.object; horizon.scale.z = .35; horizon.data.materials.append(stand_wood); bpy.ops.object.shade_smooth()
feet = []
for k in range(4):
    a = math.radians(45 + 90 * k); top = Vector((GX + math.cos(a) * (R + .05), GY + math.sin(a) * (R + .05), GZ - .01))
    foot = Vector((GX + math.cos(a) * (R + .14), GY + math.sin(a) * (R + .14), 0)); feet.append(foot)
    bar('Globe leg', top, foot, .022, stand_wood, 12)
for a, b in ((0, 2), (1, 3)):
    lift = Vector((0, 0, .22)); bar('Stretcher', feet[a].lerp(Vector((GX, GY, 0)), .25) + lift, feet[b].lerp(Vector((GX, GY, 0)), .25) + lift, .014, stand_wood, 10)

# Daylight from the window, warm interior practicals and a soft fill from the viewer.
sun = bpy.data.lights.new('Afternoon sun', 'SUN'); sun.energy = 3.2; sun.color = (1, .86, .66); sun.angle = math.radians(2)
sun_obj = bpy.data.objects.new('Afternoon sun', sun); scene.collection.objects.link(sun_obj)
sun_obj.rotation_euler = (math.radians(-58), 0, math.radians(-8))
area = bpy.data.lights.new('Window glow', 'AREA'); area.shape = 'RECTANGLE'; area.size, area.size_y = 1.6, 2.9
area.energy = 650; area.color = (1, .92, .8)
area_obj = bpy.data.objects.new('Window glow', area); scene.collection.objects.link(area_obj)
area_obj.location = (0, BACK_Y + .3, 2.1); area_obj.rotation_euler = (math.radians(-90), 0, 0)
fill = bpy.data.lights.new('Viewer fill', 'AREA'); fill.size = 4; fill.energy = 380; fill.color = (1, .87, .72)
fill_obj = bpy.data.objects.new('Viewer fill', fill); scene.collection.objects.link(fill_obj)
fill_obj.location = (0, -.8, 2.6); fill_obj.rotation_euler = (math.radians(82), 0, 0)
scene.world = bpy.data.worlds.new('Room'); scene.world.use_nodes = True
scene.world.node_tree.nodes['Background'].inputs['Color'].default_value = (.06, .05, .04, 1)

# Portrait camera focused where Rain stands, so the room falls softly out of focus.
cam_data = bpy.data.cameras.new('Portrait lens'); cam_data.lens = 35; cam_data.sensor_width = 36
cam_data.dof.use_dof = True; cam_data.dof.focus_distance = 1.7; cam_data.dof.aperture_fstop = 2.0
cam = bpy.data.objects.new('Portrait lens', cam_data); scene.collection.objects.link(cam)
cam.location = (0, 0, 1.5); cam.rotation_euler = (math.radians(91.5), 0, 0); scene.camera = cam

scene.render.engine = 'CYCLES'
try:
    prefs = bpy.context.preferences.addons['cycles'].preferences; prefs.compute_device_type = 'METAL'; prefs.get_devices()
    for d in prefs.devices: d.use = True
    scene.cycles.device = 'GPU'
except Exception as error: print('GPU unavailable', error)
scene.cycles.samples = samples; scene.cycles.use_denoising = True
scene.render.resolution_x, scene.render.resolution_y, scene.render.resolution_percentage = 1800, 1350, percent
scene.view_settings.exposure = .25
settings = scene.render.image_settings
if out.suffix.lower() in ('.jpg', '.jpeg'): settings.file_format = 'JPEG'; settings.quality = 82
else: settings.file_format = 'PNG'
scene.render.filepath = str(out); out.parent.mkdir(parents=True, exist_ok=True)
bpy.ops.render.render(write_still=True)
print('RENDERED', out)

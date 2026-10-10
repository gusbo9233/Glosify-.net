"""Original dark-academia garments with inherited skin weights for Rain and Rowan.
The references inform the styling only: ivory collar, green vest, ribbon and GG patch.
No photograph pixels or third-party logos are embedded in the model.
"""
import math
import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree
from mathutils.kdtree import KDTree


def dress(blouse, rig, *, male=False):
    collection = bpy.data.collections['Player']
    vertices = [v.co.copy() for v in blouse.data.vertices]
    surface = BVHTree.FromPolygons(vertices, [list(p.vertices) for p in blouse.data.polygons])
    nearest = KDTree(len(vertices))
    for i, co in enumerate(vertices): nearest.insert(co, i)
    nearest.balance()

    def material(name, color, roughness=.85):
        mat = bpy.data.materials.new(name); mat.use_nodes = True
        shader = mat.node_tree.nodes.get('Principled BSDF')
        shader.inputs['Base Color'].default_value = (*color, 1)
        shader.inputs['Roughness'].default_value = roughness
        return mat

    green = material('Forest green woven vest', (.015, .055, .035))
    # A small, seamless woven check supplies the fabric detail in exported glTF.
    size = 512
    y, x = np.mgrid[:size, :size]
    check = ((x // 128) % 2) * .13 + ((y // 128) % 2) * .11
    thread = .94 + .06 * np.sin(x * math.pi) + .04 * np.cos(y * math.pi)
    pinstripe = ((x % 128 < 3) | (y % 128 < 3)) * .12
    value = (.84 + check + pinstripe) * thread
    pixels = np.ones((size, size, 4), dtype=np.float32)
    pixels[:, :, :3] = value[:, :, None] * np.array([.12, .235, .175])
    textile = bpy.data.images.new('Forest woven check', width=size, height=size)
    textile.pixels.foreach_set(pixels.ravel()); textile.pack()
    texture = green.node_tree.nodes.new('ShaderNodeTexImage'); texture.image = textile
    green.node_tree.links.new(texture.outputs['Color'], green.node_tree.nodes.get('Principled BSDF').inputs['Base Color'])
    ivory = material('Ivory cotton collar', (.78, .74, .64))
    ribbon = material('Black silk ribbon', (.009, .014, .012), .65)
    patch = material('GG bottle-green patch', (.008, .026, .018))
    gold = material('Antique gold embroidery', (.57, .40, .16), .8)
    coat = material('Dark wool overcoat lapels', (.045, .036, .029), .85)

    def attach(obj):
        # The same weights as the underlying garment keep accessories attached during
        # breathing and shrug morph baking, rather than floating over an animated torso.
        for c in list(obj.users_collection): c.objects.unlink(obj)
        collection.objects.link(obj); obj.parent = rig
        groups = {g.index: obj.vertex_groups.new(name=g.name) for g in blouse.vertex_groups}
        for v in obj.data.vertices:
            if male:
                # Blend nearby skin weights so straight lapel/collar edges stay
                # smooth when the source A-pose is relaxed into the portrait pose.
                samples=nearest.find_n(v.co,16)
                factors=[1/(distance*distance+.0004) for _,_,distance in samples]
                total=sum(factors); weights={}
                for (_,index,_),factor in zip(samples,factors):
                    for g in blouse.data.vertices[index].groups:
                        weights[g.group]=weights.get(g.group,0)+g.weight*factor/total
                for group,weight in weights.items(): groups[group].add([v.index],weight,'REPLACE')
            else:
                _, index, _ = nearest.find(v.co)
                for g in blouse.data.vertices[index].groups:
                    groups[g.group].add([v.index], g.weight, 'REPLACE')
        mod = obj.modifiers.new('Follow blouse', 'ARMATURE'); mod.object = rig
        return obj

    def mesh(name, coords, faces, mat, thickness=0):
        data = bpy.data.meshes.new(name); data.from_pydata(coords, [], faces); data.update()
        obj = bpy.data.objects.new(name, data); collection.objects.link(obj)
        data.materials.append(mat)
        uv = data.uv_layers.new(name='UVMap')
        for face in data.polygons:
            face.use_smooth = True
            for loop in face.loop_indices:
                v = data.vertices[data.loops[loop].vertex_index].co
                uv.data[loop].uv = (v.x * 5, v.z * 5)
        attach(obj)
        if thickness:
            mod = obj.modifiers.new('Fabric edge', 'SOLIDIFY'); mod.thickness = thickness
        return obj

    # A continuous front profile spans the coat's open center without following
    # seam gaps. All male overlays share it, keeping the shirt, tie and lapels apart.
    profile_z=np.linspace(.82,1.49,45)
    profile_y=[]
    if male:
        for z in profile_z:
            depths=[]
            for x in np.linspace(-.17,.17,35):
                hit=surface.ray_cast(Vector((x,-1,z)),Vector((0,1,0)))[0]
                if hit is not None and hit.y<-.025: depths.append(hit.y)
            profile_y.append(min(depths) if depths else -.06)
        profile_y=np.convolve(np.pad(profile_y,(2,2),mode='edge'),np.ones(5)/5,mode='valid')-.012

    def front(x, z, lift=.008):
        if male:
            return (x,float(np.interp(z,profile_z,profile_y))-lift,z)
        hit = surface.ray_cast(Vector((x, -1, z)), Vector((0, 1, 0)))[0]
        # The original blouse is open at the neck; the shirt insert closes that gap.
        depth = max(-.16, -.045 - max(0, 1.465-z) * .85)
        return (x, (hit.y if hit is not None and hit.y < -.025 else depth) - lift, z)

    # Sleeveless vest, gently fitted over the blouse; its top edge describes the V
    # neck, shoulder straps and armholes without modifying the underlying sleeves.
    segments, rows = 96, 28
    points, faces = [], []
    angles = [0, .35, .7, 1.02, 1.57, 2.05, 2.45, math.pi]
    heights = [1.315, 1.355, 1.425, 1.455, 1.325, 1.44, 1.455, 1.445]
    for row in range(rows + 1):
        for col in range(segments):
            theta = 2 * math.pi * col / segments
            angle = min(theta, 2 * math.pi-theta)
            top = float(np.interp(angle, angles, heights))
            z = 1.025 + (top-1.025) * row / rows
            direction = Vector((math.sin(theta), -math.cos(theta), 0))
            origin = Vector((0, -.025, z))
            hit = surface.ray_cast(origin, direction, .3)[0]
            if hit is None:
                radius = 1 / math.sqrt((direction.x/.16)**2 + (direction.y/.12)**2)
                hit = origin + direction * radius
            # Rays near the armpit can hit the original blouse sleeve. Limit the
            # shell to the torso so those samples cannot produce sleeve-like fins.
            width = float(np.interp(z, [1.025,1.1,1.2,1.3,1.38,1.46], [.15,.137,.147,.17,.18,.18]))
            if male: width *= 1.18
            if abs(hit.x) > width:
                hit = origin + direction * (width / abs(direction.x))
            points.append(hit + direction * .008)
    for row in range(rows):
        for col in range(segments):
            a = row*segments+col; b = row*segments+(col+1)%segments
            faces.append((a, b, b+segments, a+segments))
    if not male:
        vest = mesh('Forest green vest', points, faces, green, .002)
        sub = vest.modifiers.new('Soft knit silhouette', 'SUBSURF'); sub.levels=1
    else:
        # Broad notched wool lapels over the fitted coat. Each row follows the
        # original cloth surface, keeping the lapels clear of the chest in motion.
        stations = [(1.46,.066,.142),(1.42,.072,.170),(1.38,.076,.184),
                    (1.355,.078,.153),(1.335,.080,.170),(1.30,.081,.148),
                    (1.26,.083,.125),(1.21,.087,.112),(1.10,.092,.117),(.85,.100,.126)]
        for sign in (-1,1):
            lapel_points, lapel_faces = [], []
            samples = []
            for a,b in zip(stations,stations[1:]):
                for t in np.linspace(0,1,8,endpoint=False):
                    samples.append(tuple(x+(y-x)*t for x,y in zip(a,b)))
            samples.append(stations[-1])
            for i,(z,inner,outer) in enumerate(samples):
                for j in range(9):
                    lapel_points.append(front(sign*(inner+(outer-inner)*j/8),z,.016))
                    if i and j:
                        n=i*9+j
                        lapel_faces.append((n-10,n-9,n,n-1))
            mesh('Notched wool lapel',lapel_points,lapel_faces,coat,.002)


    # Ivory shirt front and two pointed collar leaves, visible inside the V neck.
    if male:
        # Subdivide overlays before sampling the coat so its curved surface cannot
        # cut through a large flat shirt or collar polygon.
        def fitted_panel(name, corners, lift, mat):
            points, faces = [], []
            for i in range(17):
                t=i/16
                left=np.array(corners[0])*(1-t)+np.array(corners[3])*t
                right=np.array(corners[1])*(1-t)+np.array(corners[2])*t
                if name=='Ivory shirt front':
                    width=float(np.interp(left[1],[.85,1.36,1.468],[.114,.100,.065]))
                    left[0],right[0]=-width,width
                for j in range(17):
                    x,z=left+(right-left)*j/16
                    points.append(front(float(x),float(z),lift))
                    if i and j:
                        n=i*17+j
                        faces.append((n-18,n-17,n,n-1))
            mesh(name,points,faces,mat,.001)
        fitted_panel('Ivory shirt front',[(-.065,1.468),(.065,1.468),(.114,.85),(-.114,.85)],.007,ivory)
        # A subtle central placket and small dark buttons show below the tie.
        placket=material('Ivory shirt placket',(.70,.66,.57))
        fitted_panel('Shirt button placket',[(-.009,1.36),(.009,1.36),(.009,.85),(-.009,.85)],.008,placket)
        for z in (1.12,1.045,.97):
            coords=[front(.0035*math.cos(a),z+.0035*math.sin(a),.010) for a in np.linspace(0,2*math.pi,12,endpoint=False)]
            mesh('Shirt button',coords,[tuple(range(12))],ivory,.001)
        for sign in (-1,1):
            fitted_panel('Pointed ivory collar',[(sign*x,z) for x,z in
                [(.004,1.472),(.064,1.468),(.089,1.410),(.040,1.428)]],.038,ivory)
    else:
        coords = [front(x, z, .002) for x, z in [(-.060, 1.468), (0, 1.472), (.060, 1.468),
            (-.085, 1.39), (0, 1.39), (.085, 1.39), (-.05, 1.30), (0, 1.30), (.05, 1.30)]]
        mesh('Ivory shirt insert', coords, [(0,3,4,1),(1,4,5,2),(3,6,7,4),(4,7,8,5)], ivory, .001)
        for sign in (-1, 1):
            coords = [front(sign*x, z, .014) for x, z in [(0.004,1.472),(.064,1.468),(.102,1.401),(.040,1.420)]]
            mesh('Pointed ivory collar', coords, [(0,1,2,3)], ivory, .0015)
    # A slim tied ribbon, inspired by the second reference.
    for sign in (() if male else (-1, 1)):
        coords = [front(sign*x, z, .024) for x, z in [(.002,1.432),(.012,1.427),(.034,1.25),(.019,1.258),(.006,1.248)]]
        mesh('Ribbon tail', coords, [(0,1,2,3,4)], ribbon, .002)
    if male:
        tie_points, tie_faces = [], []
        for i, z in enumerate(np.linspace(1.43, 1.17, 28)):
            width = float(np.interp(z, [1.17,1.19,1.43], [0,.024,.009]))
            tie_points.extend([front(-width,z,.028), front(width,z,.028)])
            if i: tie_faces.append((2*i-2,2*i-1,2*i+1,2*i))
        mesh('Dark necktie', tie_points, tie_faces, ribbon, .002)
    mesh('Ribbon knot', [front(x,z,.027) for x,z in [(-.014,1.445),(.014,1.445),(.010,1.420),(-.010,1.420)]], [(0,1,2,3)], ribbon, .004)

    # Small stitched GG badge on her left chest, deliberately tucked under the hair.
    cx, cz, w, h = .100, 1.285, .062, .046
    if male: cx,cz=.158,1.240
    coords = [front(cx+x,cz+z,.020) for x,z in [(-w/2,-h/2),(w/2,-h/2),(w/2,h/2),(-w/2,h/2)]]
    if not male: mesh('GG cloth patch', coords, [(0,1,2,3)], patch, .0015)
    # Separate short stitches form a fine embroidered border.
    stitch_points, stitch_faces = [], []
    for axis, length in ([] if male else [(0,w-.008),(1,h-.008)]):
        count = round(length/.003)
        for side in (-1,1):
            for i in range(count):
                t = -length/2 + length*i/count
                if axis==0: x,z=cx+t,cz+side*(h/2-.004)
                else: x,z=cx+side*(w/2-.004),cz+t
                dx,dz=(.001,.00035) if axis==0 else (.00035,.001)
                start = len(stitch_points)
                stitch_points.extend(front(x+a,z+b,.022) for a,b in [(-dx,-dz),(dx,-dz),(dx,dz),(-dx,dz)])
                stitch_faces.append(tuple(start+i for i in range(4)))
    if male:
        # An embroidered shield directly on the coat, without Rain's cloth patch.
        shield = [(-.031,.027),(.031,.027),(.028,-.010),(0,-.035),(-.028,-.010)]
        for a,b in zip(shield,shield[1:]+shield[:1]):
            a,b=Vector(a),Vector(b); delta=b-a
            normal=Vector((-delta.y,delta.x)).normalized()*.00055
            count=max(1,round(delta.length/.0025))
            for i in range(count):
                start=len(stitch_points)
                lo,hi=a+delta*(i/count),a+delta*((i+.65)/count)
                stitch_points.extend(front(cx+v.x,cz+v.y,.005) for v in [lo-normal,lo+normal,hi+normal,hi-normal])
                stitch_faces.append(tuple(start+j for j in range(4)))
    mesh('GG embroidery border', stitch_points, stitch_faces, gold)
    lettering = bpy.data.curves.new('GG embroidery','FONT'); lettering.body='GG'
    lettering.align_x='CENTER'; lettering.align_y='CENTER'; lettering.size=.025
    lettering.extrude=.0003; lettering.bevel_depth=.00015; lettering.resolution_u=4
    text=bpy.data.objects.new('GG monogram',lettering); collection.objects.link(text)
    text.location=front(cx,cz,.009 if male else .026); text.rotation_euler=(math.pi/2,0,0)
    text.data.materials.append(gold)
    bpy.ops.object.select_all(action='DESELECT'); text.select_set(True); bpy.context.view_layer.objects.active=text
    bpy.ops.object.convert(target='MESH'); text=bpy.context.object
    bpy.ops.object.transform_apply(location=True,rotation=True,scale=True); attach(text)

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
    # Rowan's shirt collar follows the neck anatomy, not the cardigan neckline.
    if male:
        neck_body=bpy.data.objects['Face, neck and hands']
        neck_surface=BVHTree.FromPolygons([v.co.copy() for v in neck_body.data.vertices],
                                         [list(p.vertices) for p in neck_body.data.polygons])
        neck_nearest=KDTree(len(neck_body.data.vertices))
        for i,v in enumerate(neck_body.data.vertices): neck_nearest.insert(v.co,i)
        neck_nearest.balance()

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
    knit_edge = material('Charcoal ribbed cardigan border', (.050, .044, .034), .94)

    def attach(obj, neck_fit=False):
        # The same weights as the underlying garment keep accessories attached during
        # breathing and shrug morph baking, rather than floating over an animated torso.
        for c in list(obj.users_collection): c.objects.unlink(obj)
        collection.objects.link(obj); obj.parent = rig
        groups={g.name:obj.vertex_groups.new(name=g.name) for g in blouse.vertex_groups}
        for v in obj.data.vertices:
            if male:
                # Blend chest and neck weights across the shirt so the collar,
                # shirt front and tie remain joined during nods and shrugs.
                neck_weight=max(0,min(1,(v.co.z-1.445)/.050)) if neck_fit else 0
                weights={}
                for source,lookup,mix in ((blouse,nearest,1-neck_weight),(neck_body,neck_nearest,neck_weight)):
                    if mix==0: continue
                    samples=lookup.find_n(v.co,16)
                    factors=[1/(distance*distance+.0004) for _,_,distance in samples]
                    total=sum(factors)
                    for (_,index,_),factor in zip(samples,factors):
                        for g in source.data.vertices[index].groups:
                            name=source.vertex_groups[g.group].name
                            weights[name]=weights.get(name,0)+g.weight*factor/total*mix
                for name,weight in weights.items(): groups[name].add([v.index],weight,'REPLACE')
            else:
                _,index,_=nearest.find(v.co)
                for g in blouse.data.vertices[index].groups:
                    groups[blouse.vertex_groups[g.group].name].add([v.index],g.weight,'REPLACE')
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
        attach(obj, neck_fit=male and name in ('Shirt collar stand','Shirt shoulder yoke','Pointed ivory collar','Ivory shirt front','Dark necktie','Tie knot'))
        if thickness:
            mod = obj.modifiers.new('Fabric edge', 'SOLIDIFY'); mod.thickness = thickness
        return obj

    # A continuous front profile spans the cardigan's V neck without following
    # seam gaps. All male overlays share it, keeping the shirt, tie and knit border apart.
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
        # Narrow ribbing frames the V, then continues as a button band to the hem.
        # Surface sampling keeps the trim against the knit rather than floating.
        def cardigan_front(x,z,lift=.006):
            hit=surface.ray_cast(Vector((x,-1,z)),Vector((0,1,0)))[0]
            return (x,(hit.y if hit is not None and hit.y < -.015 else front(x,z,0)[1])-lift,z)
        for sign in (-1,1):
            trim_points,trim_faces=[],[]
            for i,z in enumerate(np.linspace(1.465,.98,65)):
                center=max(.008,.40*(z-1.265))
                # Sample beyond the cut edge so both sides share a stable depth.
                depth=cardigan_front(sign*(center+.021),z,.008)[1]
                for j in range(5):
                    x=sign*(center-.007+j*.006)
                    trim_points.append((x,depth,z))
                    if i and j:
                        n=i*5+j; trim_faces.append((n-6,n-5,n,n-1))
            mesh('V-neck cardigan ribbing',trim_points,trim_faces,knit_edge,.002)
        button=material('Dark horn cardigan buttons',(.022,.017,.012),.55)
        for z in (1.245,1.178,1.111,1.044,.987):
            coords=[cardigan_front(.0065*math.cos(a),z+.0065*math.sin(a),.015)
                    for a in np.linspace(0,2*math.pi,16,endpoint=False)]
            mesh('Cardigan button',coords,[tuple(range(16))],button,.003)
        # Closely spaced raised ribs give the border a knitted edge without a shader.
        rib_points,rib_faces=[],[]
        for sign in (-1,1):
            for z in np.arange(.99,1.462,.004):
                center=max(.008,.40*(z-1.265))
                depth=cardigan_front(sign*(center+.021),z,.010)[1]
                n=len(rib_points)
                rib_points.extend((sign*x,depth,zz) for x,zz in
                    [(center-.006,z),(center+.016,z),(center+.016,z+.001),(center-.006,z+.001)])
                rib_faces.append((n,n+1,n+2,n+3))
        mesh('Cardigan border stitches',rib_points,rib_faces,knit_edge)

    # A real collar stand wraps the base of Rowan's neck. The smaller folded
    # leaves meet at the tie knot instead of floating low across his chest.
    if male:
        def shirt_front(x,z,lift=.007):
            chest_y=front(x,z,0)[1]
            blend=max(0,min(1,(z-1.445)/.050))
            hit=neck_surface.ray_cast(Vector((x,-1,min(z,1.502))),Vector((0,1,0)))[0]
            neck_y=hit.y-.003 if hit is not None else chest_y
            return (x,chest_y*(1-blend)+neck_y*blend-lift,z)

        points,faces=[],[]
        segments,rows=64,5
        for row in range(rows+1):
            z=1.480+.022*row/rows
            for col in range(segments):
                theta=2*math.pi*col/segments
                direction=Vector((math.sin(theta),-math.cos(theta),0))
                origin=Vector((0,-.026,z))
                hit=neck_surface.ray_cast(origin,direction,.12)[0]
                if hit is None:
                    radius=1/math.sqrt((direction.x/.062)**2+(direction.y/.062)**2)
                    hit=origin+direction*radius
                points.append(hit+direction*.004)
                if row:
                    a=(row-1)*segments+col; b=(row-1)*segments+(col+1)%segments
                    faces.append((a,b,b+segments,a+segments))
        mesh('Shirt collar stand',points,faces,ivory,.0015)

        # Fill the shoulder area between the collar stand and the cardigan.
        # The outer edge slips beneath the knit; no bare-skin wedges show below it.
        yoke_points,yoke_faces=[],[]
        for row in range(7):
            t=row/6
            for col in range(segments):
                theta=2*math.pi*col/segments
                direction=Vector((math.sin(theta),-math.cos(theta),0))
                z=1.483
                origin=Vector((0,-.026,z))
                inner=neck_surface.ray_cast(origin,direction,.12)[0]
                if inner is None: inner=origin+direction*.062
                inner+=direction*.004
                outer_z=1.448+.010*(1-math.cos(theta))
                outer=Vector((.115*math.sin(theta),-.026-.055*math.cos(theta),outer_z))
                point=inner.lerp(outer,t)
                yoke_points.append(point)
                # The front panel already covers the central chest. Keep this
                # yoke at the shoulders/back so it cannot cross over the tie.
                angle=min(theta,2*math.pi-theta)
                if row and angle > .75:
                    a=(row-1)*segments+col;b=(row-1)*segments+(col+1)%segments
                    yoke_faces.append((a,b,b+segments,a+segments))
        mesh('Shirt shoulder yoke',yoke_points,yoke_faces,ivory,.001)

        def fitted_panel(name,corners,lift,mat):
            points,faces=[],[]
            for i in range(17):
                t=i/16
                left=np.array(corners[0])*(1-t)+np.array(corners[3])*t
                right=np.array(corners[1])*(1-t)+np.array(corners[2])*t
                if name=='Ivory shirt front':
                    width=float(np.interp(left[1],[1.265,1.38,1.46,1.478,1.498],[.003,.043,.083,.070,.056]))
                    left[0],right[0]=-width,width
                for j in range(17):
                    x,z=left+(right-left)*j/16
                    points.append(shirt_front(float(x),float(z),lift))
                    if i and j:
                        n=i*17+j; faces.append((n-18,n-17,n,n-1))
            mesh(name,points,faces,mat,.001)
        fitted_panel('Ivory shirt front',[(-.052,1.498),(.052,1.498),(.003,1.265),(-.003,1.265)],.007,ivory)
        for sign in (-1,1):
            fitted_panel('Pointed ivory collar',[(sign*x,z) for x,z in
                [(.010,1.502),(.052,1.500),(.066,1.449),(.022,1.474)]],.014,ivory)
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
        for i, z in enumerate(np.linspace(1.479, 1.270, 24)):
            width = float(np.interp(z, [1.270,1.30,1.479], [0,.021,.009]))
            tie_points.extend([shirt_front(-width,z,.016), shirt_front(width,z,.016)])
            if i: tie_faces.append((2*i-2,2*i-1,2*i+1,2*i))
        mesh('Dark necktie', tie_points, tie_faces, ribbon, .002)
        mesh('Tie knot',[shirt_front(x,z,.018) for x,z in
             [(-.012,1.501),(.012,1.501),(.009,1.477),(-.009,1.477)]],[(0,1,2,3)],ribbon,.004)
    else:
        mesh('Ribbon knot', [front(x,z,.027) for x,z in [(-.014,1.445),(.014,1.445),(.010,1.420),(-.010,1.420)]], [(0,1,2,3)], ribbon, .004)

    # Small stitched GG badge on her left chest, deliberately tucked under the hair.
    cx, cz, w, h = .100, 1.285, .062, .046
    if male: cx,cz=.115,1.280
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
        # An embroidered shield directly on the cardigan, without Rain's cloth patch.
        shield = [(-.031,.027),(.031,.027),(.028,-.010),(0,-.035),(-.028,-.010)]
        for a,b in zip(shield,shield[1:]+shield[:1]):
            a,b=Vector(a),Vector(b); delta=b-a
            normal=Vector((-delta.y,delta.x)).normalized()*.00055
            count=max(1,round(delta.length/.0025))
            for i in range(count):
                start=len(stitch_points)
                lo,hi=a+delta*(i/count),a+delta*((i+.65)/count)
                stitch_points.extend(cardigan_front(cx+v.x,cz+v.y,.008) for v in [lo-normal,lo+normal,hi+normal,hi-normal])
                stitch_faces.append(tuple(start+j for j in range(4)))
    mesh('GG embroidery border', stitch_points, stitch_faces, gold)
    lettering = bpy.data.curves.new('GG embroidery','FONT'); lettering.body='GG'
    lettering.align_x='CENTER'; lettering.align_y='CENTER'; lettering.size=.025
    lettering.extrude=.0003; lettering.bevel_depth=.00015; lettering.resolution_u=4
    text=bpy.data.objects.new('GG monogram',lettering); collection.objects.link(text)
    text.location=cardigan_front(cx,cz,.012) if male else front(cx,cz,.026); text.rotation_euler=(math.pi/2,0,0)
    text.data.materials.append(gold)
    bpy.ops.object.select_all(action='DESELECT'); text.select_set(True); bpy.context.view_layer.objects.active=text
    bpy.ops.object.convert(target='MESH'); text=bpy.context.object
    bpy.ops.object.transform_apply(location=True,rotation=True,scale=True); attach(text)

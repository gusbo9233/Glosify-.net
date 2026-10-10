"""CC0 MakeHuman mesh fitting helper adapted from the GlobeGlotter NPC builder.
Source assets and reproduction instructions: README.md.
"""
from pathlib import Path
import bpy, json
from mathutils import Vector
HERE=Path(__file__).resolve().parent
SOURCE=HERE/'character-source'

def obj_data(path):
    vertices=[];uv=[];faces=[];group=''
    for line in path.read_text().splitlines():
        p=line.split()
        if not p: continue
        if p[0]=='v': vertices.append(Vector(tuple(map(float,p[1:4]))))
        elif p[0]=='vt': uv.append(tuple(map(float,p[1:3])))
        elif p[0]=='g': group=p[1]
        elif p[0]=='f':
            corners=[tuple(int(x)-1 if x else -1 for x in v.split('/')) for v in p[1:]]
            faces.append((group,corners))
    return vertices,uv,faces

def fit_data(path,base):
    scales=[1.,1.,1.];fits=[];deleted=set();section=''
    for line in path.read_text().splitlines():
        p=line.split()
        if not p or p[0].startswith('#'): continue
        if p[0] in ['x_scale','y_scale','z_scale']:
            axis='xyz'.index(p[0][0]);a,b=int(p[1]),int(p[2]);scales[axis]=abs(base[a][axis]-base[b][axis])/float(p[3])
        elif p[0]=='verts': section='verts'
        elif p[0]=='delete_verts': section='delete'
        elif section=='verts' and p[0].lstrip('-').isdigit():
            if len(p)==1: fits.append(([int(p[0])],[1.],Vector((0,0,0))))
            else: fits.append((list(map(int,p[:3])),list(map(float,p[3:6])),Vector(tuple(float(p[6+i])*scales[i] for i in range(3)))))
        elif section=='delete':
            i=0
            while i<len(p):
                if i+2<len(p) and p[i+1]=='-': deleted.update(range(int(p[i]),int(p[i+2])+1));i+=3
                else: deleted.add(int(p[i]));i+=1
    return fits,deleted

def material(name,diffuse,normal=None,roughness=.7,alpha=False):
    mat=bpy.data.materials.new(name);mat.use_nodes=True
    nodes=mat.node_tree.nodes;links=mat.node_tree.links;bsdf=nodes.get('Principled BSDF')
    bsdf.inputs['Roughness'].default_value=roughness
    bsdf.inputs['Specular IOR Level'].default_value=.28
    tex=nodes.new('ShaderNodeTexImage');tex.image=bpy.data.images.load(str(diffuse),check_existing=True)
    # Keep face detail at 2K; secondary materials use smaller maps.
    limit=2048 if name == 'Skin' else 1024
    if max(tex.image.size)>limit:
        ratio=limit/max(tex.image.size);tex.image.scale(int(tex.image.size[0]*ratio),int(tex.image.size[1]*ratio))
    links.new(tex.outputs['Color'],bsdf.inputs['Base Color'])
    if alpha:
        links.new(tex.outputs['Alpha'],bsdf.inputs['Alpha']);mat.surface_render_method='DITHERED'
        mat.use_backface_culling=False
    if normal:
        n=nodes.new('ShaderNodeTexImage');n.image=bpy.data.images.load(str(normal),check_existing=True);n.image.colorspace_settings.name='Non-Color'
        if max(n.image.size)>limit: n.image.scale(limit,limit)
        nm=nodes.new('ShaderNodeNormalMap');nm.inputs['Strength'].default_value=.65
        links.new(n.outputs['Color'],nm.inputs['Color']);links.new(nm.outputs['Normal'],bsdf.inputs['Normal'])
    return mat

def build_character(*, targets, height_metres, asset_specs, skin_path):
    # Replace only the avatar collection.
    old=bpy.data.collections.get('Player')
    if old:
        for ob in list(old.all_objects): bpy.data.objects.remove(ob,do_unlink=True)
        bpy.data.collections.remove(old)
    collection=bpy.data.collections.new('Player');bpy.context.scene.collection.children.link(collection)
    base,body_uv,body_faces=obj_data(SOURCE/'base.obj')
    for target in targets:
        name,weight=target if isinstance(target,tuple) else (target,1.)
        if weight<=0: continue
        for line in (SOURCE/name).read_text().splitlines():
            p=line.split()
            if len(p)==4 and p[0].isdigit(): base[int(p[0])]+=Vector(tuple(map(float,p[1:])))*weight
    body_indices={c[0] for g,face in body_faces if g=='body' for c in face}
    floor=min(base[i].y for i in body_indices);height=max(base[i].y for i in body_indices)-floor
    scale=height_metres/height
    def world(v): return Vector((v.x*scale,-v.z*scale,(v.y-floor)*scale+.015))
    rig_data=json.loads((SOURCE/'default.mhskel').read_text())
    source_weights=json.loads((SOURCE/'default_weights.mhw').read_text())['weights']
    weights=[{} for _ in base]
    for bone,entries in source_weights.items():
        for index,weight in entries: weights[index][bone]=weight
    armature=bpy.data.armatures.new('Anatomical skeleton')
    rig=bpy.data.objects.new('Civilian',armature);collection.objects.link(rig)
    bpy.context.view_layer.objects.active=rig;rig.select_set(True)
    bpy.ops.object.mode_set(mode='EDIT')
    def joint(name):
        ids=rig_data['joints'][name]
        return world(sum((base[i] for i in ids),Vector())/len(ids))
    for name,bone in rig_data['bones'].items():
        eb=armature.edit_bones.new(name);eb.head=joint(bone['head']);eb.tail=joint(bone['tail'])
        eb.align_roll(Vector((0,1,0)))
    for name,bone in rig_data['bones'].items():
        if bone['parent']: armature.edit_bones[name].parent=armature.edit_bones[bone['parent']]
    bpy.ops.object.mode_set(mode='OBJECT')
    def mesh(name,vertices,uv,faces,maps,mat):
        # Compact unused body/helper vertices before skinning.
        used=sorted({c[0] for _,face in faces for c in face});lookup={old:i for i,old in enumerate(used)}
        data=bpy.data.meshes.new(name)
        data.from_pydata([world(vertices[i]) for i in used],[],[[lookup[c[0]] for c in f] for _,f in faces]);data.update()
        layer=data.uv_layers.new(name='UVMap')
        for poly,(_,corners) in zip(data.polygons,faces):
            poly.use_smooth=True
            for loop,c in zip(poly.loop_indices,corners): layer.data[loop].uv=uv[c[1]] if len(c)>1 and c[1]>=0 else (0,0)
        ob=bpy.data.objects.new(name,data);collection.objects.link(ob);ob.parent=rig;data.materials.append(mat)
        groups={name:ob.vertex_groups.new(name=name) for name in source_weights}
        for new,index in enumerate(used):
            w={};ids,factors,_=maps[index]
            for i,factor in zip(ids,factors):
                for bone,value in weights[i].items(): w[bone]=w.get(bone,0)+value*factor
            top=sorted(((n,v) for n,v in w.items() if v>0),key=lambda p:-p[1])[:4]
            total=sum(v for _,v in top)
            for bone,value in top: groups[bone].add([new],value/total,'REPLACE')
        modifier=ob.modifiers.new('Skin deformation','ARMATURE');modifier.object=rig
        return ob
    system=SOURCE/'system'
    hidden=set();pending=[]
    for name,folder,stem,rough,alpha in asset_specs:
        path=system/folder
        fits,deleted=fit_data(path/(stem+'.mhclo'),base);hidden.update(deleted)
        _,uv,faces=obj_data(path/(stem+'.obj'))
        fitted=[sum((base[i]*w for i,w in zip(ids,factors)),offset.copy()) for ids,factors,offset in fits]
        diffuse=path/(stem+'_diffuse.png')
        if not diffuse.exists():
            descriptor=next(path.glob('*.mhmat')).read_text().splitlines()
            relative=next(line.split(maxsplit=1)[1] for line in descriptor if line.startswith('diffuseTexture '))
            diffuse=path/relative
        normal=path/(stem+'_normal.png')
        mat=material(name,diffuse,normal if normal.exists() else None,rough,alpha)
        pending.append((name,fitted,uv,faces,fits,mat))
    body_faces=[(g,f) for g,f in body_faces if g=='body' and not any(c[0] in hidden for c in f)]
    skin=material('Skin',skin_path,roughness=.62)
    mesh('Face, neck and hands',base,body_uv,body_faces,[([i],[1.],Vector()) for i in range(len(base))],skin)
    for item in pending: mesh(*item)
    fits,_=fit_data(SOURCE/'low-poly.mhclo',base)
    _,uv,faces=obj_data(SOURCE/'low-poly.obj')
    vertices=[sum((base[i]*w for i,w in zip(ids,fs)),offset.copy()) for ids,fs,offset in fits]
    mesh('Eyes',vertices,uv,faces,fits,material('Brown eyes',system/'eyes/materials/brown_eye.png',roughness=.24))

    bpy.ops.object.select_all(action='DESELECT')
    for ob in collection.all_objects: ob.select_set(True)
    bpy.context.view_layer.objects.active=rig
    bpy.context.scene.frame_set(1)
    print('CHARACTER EXPORT COMPLETE',len(collection.objects),'objects',len(armature.bones),'bones')
    return rig

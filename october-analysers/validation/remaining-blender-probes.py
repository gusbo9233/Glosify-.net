"""Local probes of cited asset helpers; no source assets or network required."""
import ast
import copy
import json
import math
from pathlib import Path
import struct
import sys
import tempfile
import bpy
import numpy as np
from mathutils import Vector
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts/avatar'))
import avatar_textures as textures
import avatar_rig as rig
import human_mesh as human
results = {'blender': bpy.app.version_string, 'numpy': np.__version__, 'pythonOptimized': sys.flags.optimize}
image = textures.new_image('analyser probe', np.full((2, 3, 3), .5, np.float32))
pixels = textures.image_pixels(image)
assert pixels.shape == (2, 3, 4)
assert np.allclose(pixels[:, :, :3], .5, atol=1/255)
results['numpyPositionalArrayAndReshape'] = 'passed'
with tempfile.TemporaryDirectory(prefix='glosify-assets-') as directory:
    path = Path(directory)
    image.filepath_raw = str(path / 'probe.png'); image.file_format = 'PNG'; image.save()
    material = human.material('analyser material', path / 'probe.png')
    assert next(n for n in material.node_tree.nodes if n.type == 'TEX_IMAGE').image
    assert next(n for n in material.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    results['materialNodeInvariants'] = 'passed'
    # A minimal uncompressed GLB exercises the actual compactor and its defaults.
    def write_glb(target, stride=False, component=5126):
        data = np.array([[.1, 0, 0], [0, 0, 0]], np.float32).tobytes()
        doc = {'asset':{'version':'2.0'},'materials':[], 'buffers':[{'byteLength':len(data)}],
               'bufferViews':[{'buffer':0, 'byteOffset':0,'byteLength':len(data), **({'byteStride':12} if stride else {})}],
               'accessors':[{'bufferView':0,'componentType':component,'count':2,'type':'VEC3'}],
               'meshes':[{'primitives':[{'targets':[{'POSITION':0}]}]}]}
        text=json.dumps(doc).encode();text+=b' '*(-len(text)%4)
        target.write_bytes(struct.pack('<III',0x46546c67,2,28+len(text)+len(data))+struct.pack('<II',len(text),0x4e4f534a)+text+struct.pack('<II',len(data),0x004e4942)+data)
    defaults=copy.deepcopy(rig.compact_morph_targets.__defaults__)
    target=path/'probe.glb'
    for _ in range(2):
        write_glb(target);rig.compact_morph_targets(target)
        raw=target.read_bytes();length=struct.unpack_from('<I',raw,12)[0];doc=json.loads(raw[20:20+length]);assert doc['accessors'][0]['sparse']['count']==1
    assert defaults==rig.compact_morph_targets.__defaults__
    for kwargs in [{'stride':True},{'component':5123}]:
        write_glb(target,**kwargs);before=target.read_bytes()
        try: rig.compact_morph_targets(target)
        except AssertionError: pass
        else: raise AssertionError('Invalid exporter structure was accepted')
        assert target.read_bytes()==before
    results['compactorDefaultsAndGuards']='passed'
# Execute the production tube helper and actual lambda from the loop with varying tips.
source=(ROOT/'scripts/avatar/build-kwame.py').read_text();tree=ast.parse(source)
tube=next(n for n in tree.body if isinstance(n,ast.FunctionDef) and n.name=='tube')
callback=next(n for n in ast.walk(tree) if isinstance(n,ast.Lambda) and n.lineno==240)
ns={'Vector':Vector,'math':math,'smooth':textures.smooth}
exec(compile(ast.Module(body=[tube],type_ignores=[]),'<tube>','exec'),ns)
observed=[]
for tip in [3,7]:
    ns['tip']=tip
    radius=eval(compile(ast.Expression(callback),'<radius>','eval'),ns)
    points=[Vector((i,0,0)) for i in range(tip+1)]
    coords,faces,uvs=ns['tube'](points,radius)
    assert len(coords)==(tip+1)*10
    assert abs((coords[tip*10]-points[-1]).length-.0016*.55)<1e-8
    observed.append(len(coords))
results['synchronousClosure']=observed
# Both smooth definitions are deliberately used in separate build phases.
results['smoothDefinitions']=[n.lineno for n in ast.walk(ast.parse((ROOT/'scripts/avatar/build-rain.py').read_text())) if isinstance(n,ast.FunctionDef) and n.name=='smooth']
print('ANALYSER_RESULT='+json.dumps(results,sort_keys=True))

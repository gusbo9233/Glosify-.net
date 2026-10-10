"""Download and verify the pinned CC0 core and attributed CC-BY hair assets."""
from pathlib import Path
from contextlib import ExitStack
import hashlib, json, sys, urllib.request, zipfile, tempfile
manifest=json.loads(Path(__file__).with_name('human-source.json').read_text())
out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=True)
def checked(data, expected):
    if hashlib.sha256(data).hexdigest()!=expected: raise ValueError('Asset checksum mismatch')
    return data
with tempfile.TemporaryDirectory() as temporary, ExitStack() as stack:
    packs={}
    sources=[{'id':'system','url':manifest['packUrl'],'sha256':manifest['packSha256']}]
    sources+=manifest.get('additionalPacks',[])
    for source in sources:
        archive=Path(temporary)/(source['id']+'.zip')
        urllib.request.urlretrieve(source['url'],archive)
        checked(archive.read_bytes(),source['sha256'])
        packs[source['id']]=stack.enter_context(zipfile.ZipFile(archive))
    for entry in manifest['files']:
        data=packs[entry.get('pack','system')].read(entry['archivePath']) if 'archivePath' in entry else urllib.request.urlopen(entry['url'],timeout=30).read()
        path=out/entry['path'];path.parent.mkdir(parents=True,exist_ok=True)
        path.write_bytes(checked(data,entry['sha256']))
print('Verified avatar sources:',out)

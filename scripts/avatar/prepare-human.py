"""Download and verify only the pinned CC0 assets needed by build-rain.py."""
from pathlib import Path
import hashlib, json, sys, urllib.request, zipfile, tempfile
manifest=json.loads(Path(__file__).with_name('human-source.json').read_text())
out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=True)
def checked(data, expected):
    if hashlib.sha256(data).hexdigest()!=expected: raise ValueError('Asset checksum mismatch')
    return data
with tempfile.TemporaryDirectory() as temporary:
    archive=Path(temporary)/'system.zip'
    urllib.request.urlretrieve(manifest['packUrl'],archive)
    checked(archive.read_bytes(),manifest['packSha256'])
    with zipfile.ZipFile(archive) as pack:
        for entry in manifest['files']:
            data=pack.read(entry['archivePath']) if 'archivePath' in entry else urllib.request.urlopen(entry['url'],timeout=30).read()
            path=out/entry['path'];path.parent.mkdir(parents=True,exist_ok=True)
            path.write_bytes(checked(data,entry['sha256']))
print('Verified avatar sources:',out)

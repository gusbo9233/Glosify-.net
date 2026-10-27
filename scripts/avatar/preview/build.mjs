import { build } from 'esbuild';
import assert from 'node:assert/strict';
import { copyFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const output = new URL('../../../output/likeness-avatar/viewer/', import.meta.url);
await mkdir(output, { recursive: true });
const result = await build({
    entryPoints: [fileURLToPath(new URL('preview.source.js', import.meta.url))],
    outfile: fileURLToPath(new URL('preview.js', output)),
    // The viewer and relocated decoder must use the same Babylon module instances.
    alias: {
        '@babylonjs/core': fileURLToPath(new URL('../node_modules/@babylonjs/core', import.meta.url)),
        '@babylonjs/loaders': fileURLToPath(new URL('../node_modules/@babylonjs/loaders', import.meta.url)),
    },
    bundle: true, minify: true, format: 'esm', legalComments: 'linked', metafile: true,
});
assert.equal(Object.keys(result.metafile.inputs).filter(path => path.endsWith('/glTFLoaderExtensionRegistry.js')).length, 1,
    'The preview loader and local decoder must share one Babylon.js extension registry.');
await copyFile(new URL('index.html', import.meta.url), new URL('index.html', output));
console.log('Built output/likeness-avatar/viewer');

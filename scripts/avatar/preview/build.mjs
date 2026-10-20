import { build } from 'esbuild';
import { copyFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const output = new URL('../../../output/likeness-avatar/viewer/', import.meta.url);
await mkdir(output, { recursive: true });
await build({
    entryPoints: [fileURLToPath(new URL('preview.source.js', import.meta.url))],
    outfile: fileURLToPath(new URL('preview.js', output)),
    nodePaths: [fileURLToPath(new URL('../node_modules/', import.meta.url))],
    bundle: true, minify: true, format: 'esm', legalComments: 'linked',
});
await copyFile(new URL('index.html', import.meta.url), new URL('index.html', output));
console.log('Built output/likeness-avatar/viewer');

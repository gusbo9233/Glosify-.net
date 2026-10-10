import { build } from 'esbuild';
import { fileURLToPath } from 'node:url';
import { readFile, writeFile } from 'node:fs/promises';
const root = fileURLToPath(new URL('../../', import.meta.url));
await build({ entryPoints: [root + 'Glosify/wwwroot/js/avatar/scene.source.js'], bundle: true, minify: true,
    format: 'esm', outfile: root + 'Glosify/wwwroot/js/avatar/scene.min.js', legalComments: 'inline',
    banner: { js: '/*! Babylon.js: see scene.min.js.LEGAL.txt for licenses and notices. */' },
    nodePaths: [fileURLToPath(new URL('node_modules', import.meta.url))] });

const core = new URL('node_modules/@babylonjs/core/', import.meta.url);
await writeFile(root + 'Glosify/wwwroot/js/avatar/scene.min.js.LEGAL.txt',
    await readFile(new URL('NOTICE.md', core), 'utf8') + '\n\n' + await readFile(new URL('license.md', core), 'utf8'));

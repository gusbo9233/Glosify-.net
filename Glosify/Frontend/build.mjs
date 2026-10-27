import { build, context } from 'esbuild';
import { readFile, writeFile, mkdir, readdir, rm } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, relative } from 'node:path';
import { createHash } from 'node:crypto';

const root = dirname(fileURLToPath(import.meta.url));
const webroot = resolve(root, '../wwwroot');
const entries = JSON.parse(await readFile(resolve(root, 'entries.json'), 'utf8'));
const production = process.argv.includes('--production');
const watch = process.argv.includes('--watch');
const testing = process.argv.includes('--test');
const cache = resolve(root, '../obj/frontend');

async function files(directory) {
    const result = [];
    for (const entry of await readdir(directory, { withFileTypes: true })) {
        if (entry.name === 'node_modules') continue;
        const path = resolve(directory, entry.name);
        if (entry.isDirectory()) result.push(...await files(path));
        else result.push(path);
    }
    return result.sort();
}

const sourceFiles = await files(root);
const digest = createHash('sha256').update(String(production));
for (const path of sourceFiles) digest.update(relative(root, path)).update(await readFile(path));
const signature = digest.digest('hex');
const previous = await readFile(resolve(cache, 'outputs.json'), 'utf8').then(JSON.parse).catch(() => null);
if (!watch && !testing && previous?.signature === signature
    && (await Promise.all(previous.files.map(path => readFile(resolve(webroot, path)).then(() => true, () => false)))).every(Boolean)) {
    process.exit(0);
}

const common = {
    absWorkingDir: root, target: 'es2022', logLevel: 'warning', legalComments: 'inline',
    sourcemap: production || testing ? false : 'linked',
};
const configurations = [];
if (testing) {
    const outdir = resolve(root, '../../Glosify.ClientTests/build');
    await rm(outdir, { recursive: true, force: true });
    await mkdir(outdir, { recursive: true });
    await writeFile(resolve(outdir, 'package.json'), '{"type":"module"}\n');
    configurations.push({ ...common, entryPoints: sourceFiles.filter(path => /\.(js|ts)$/.test(path) && !path.endsWith('.d.ts')),
        outbase: root, outdir, format: 'esm' });
    // The conversation lifecycle harness supplies browser dependencies through require.
    configurations.push({ ...common, entryPoints: ['avatar/avatar.ts'],
        outfile: resolve(outdir, 'avatar/avatar.cjs'), format: 'cjs' });
    for (const { source, format } of Object.values(entries)) {
        if (format !== 'iife' || source.startsWith('avatar/')) continue;
        configurations.push({ ...common, entryPoints: [source], outfile: resolve(outdir, source.replace(/\.(js|ts)$/, '.bundle.js')),
            bundle: true, format: 'iife' });
    }
} else {
    const modules = {};
    for (const [url, { source, format }] of Object.entries(entries)) {
        if (url === 'avatar/scene.min.js' || url === 'avatar/meshopt.worker.js') continue;
        if (format === 'esm') modules[url.replace(/\.js$/, '')] = source;
        else configurations.push({ ...common, entryPoints: [source], outfile: resolve(webroot, 'js', url),
            bundle: true, format: 'iife', minify: production });
    }
    configurations.push({ ...common, entryPoints: modules, outdir: resolve(webroot, 'js'), bundle: true,
        format: 'esm', splitting: true, chunkNames: 'chunks/[name]-[hash]', minify: production,
        external: ['/lib/*', '/js/avatar/scene.min.js'] });
    configurations.push({ ...common, entryPoints: [entries['avatar/scene.min.js'].source], outfile: resolve(webroot, 'js/avatar/scene.min.js'),
        bundle: true, format: 'esm', minify: production,
        banner: { js: '/*! Babylon.js: see scene.min.js.LEGAL.txt for licenses and notices. */' } });
    configurations.push({ ...common, entryPoints: [entries['avatar/meshopt.worker.js'].source], outfile: resolve(webroot, 'js/avatar/meshopt.worker.js'),
        bundle: true, format: 'iife', minify: production,
        banner: { js: '/*! meshoptimizer 0.24.0, MIT: see meshopt.worker.js.LEGAL.txt */' } });
    configurations.push({ ...common, entryPoints: (await readdir(resolve(root, 'styles'))).filter(name => name.endsWith('.css')).map(name => 'styles/' + name),
        outdir: resolve(webroot, 'css'), bundle: true, minify: production, external: ['/images/*', '/lib/*'] });
}

await mkdir(cache, { recursive: true });
if (watch) {
    // Watch builds overwrite the webroot without updating the cached output signature.
    await rm(resolve(cache, 'outputs.json'), { force: true });
    for (const configuration of configurations) await (await context(configuration)).watch();
    console.log('Watching frontend sources. Restart the ASP.NET host after adding new entrypoints.');
} else {
    const generated = [];
    for (const configuration of configurations) {
        const result = await build({ ...configuration, metafile: true });
        generated.push(...Object.keys(result.metafile.outputs).map(path => relative(webroot, resolve(root, path))));
    }
    if (!testing) {
        // Keep upstream notices beside the generated renderer and decoder.
        for (const [name, sources] of [
            ['scene.min.js.LEGAL.txt', ['@babylonjs/core/NOTICE.md', '@babylonjs/core/license.md']],
            ['meshopt.worker.js.LEGAL.txt', ['meshoptimizer/LICENSE.md']],
        ]) {
            const path = 'js/avatar/' + name;
            await writeFile(resolve(webroot, path), (await Promise.all(sources.map(source => readFile(resolve(root, 'node_modules', source), 'utf8')))).join('\n\n'));
            generated.push(path);
        }
        for (const path of (await Promise.all(['js', 'css'].map(directory => files(resolve(webroot, directory))))).flat().map(path => relative(webroot, path))) {
            if (!generated.includes(path) && (path.startsWith('js/') || path.startsWith('css/')))
                await rm(resolve(webroot, path), { force: true });
        }
        await writeFile(resolve(cache, 'outputs.json'), JSON.stringify({ signature, files: generated }, null, 2) + '\n');
    }
}

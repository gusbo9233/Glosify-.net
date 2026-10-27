import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import { cp, mkdtemp, readFile, readdir, rm, stat, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, join } from 'node:path';
import { setTimeout } from 'node:timers/promises';
import { fileURLToPath } from 'node:url';

for (const scenario of ['watch mode', 'a failed Debug build']) {
    test(`production assets are rebuilt after ${scenario} with unchanged sources`, { timeout: 90_000 }, async t => {
        const temporary = await mkdtemp(join(tmpdir(), 'glosify-frontend-build-'));
        t.after(() => rm(temporary, { recursive: true, force: true }));
        const frontend = fileURLToPath(new URL('../Glosify/Frontend/', import.meta.url));
        const cwd = join(temporary, 'Frontend');
        await cp(frontend, cwd, { recursive: true, filter: path => basename(path) !== 'node_modules' });
        await symlink(join(frontend, 'node_modules'), join(cwd, 'node_modules'), 'dir');
        const scene = join(temporary, 'wwwroot/js/avatar/scene.min.js');
        const stylesheet = join(temporary, 'wwwroot/css/site.css');
        const manifest = join(temporary, 'obj/frontend/outputs.json');
        const hash = content => createHash('sha256').update(content).digest('hex');
        const build = () => execFileSync(process.execPath, ['build.mjs', '--production'], { cwd, timeout: 30_000 });

        build();
        const productionScene = hash(await readFile(scene));
        const productionStyles = hash(await readFile(stylesheet));
        if (scenario === 'a failed Debug build') {
            const source = join(cwd, 'styles/site.css');
            const original = await readFile(source);
            await writeFile(source, '@import "./missing-regression.css";\n' + original);
            assert.throws(() => execFileSync(process.execPath, ['build.mjs'], { cwd, timeout: 30_000, stdio: 'pipe' }),
                error => error.status === 1 && error.stderr.toString().includes('missing-regression.css'));
            assert.notEqual(hash(await readFile(scene)), productionScene, 'Debug assets were written before CSS failed');
            await writeFile(source, original);
        } else {
            const watcher = spawn(process.execPath, ['build.mjs', '--watch'], { cwd, stdio: ['ignore', 'ignore', 'pipe'] });
            const closed = once(watcher, 'close');
            let errors = '';
            watcher.stderr.on('data', data => { errors += data; });
            try {
                const deadline = Date.now() + 30_000;
                while (!(await readFile(scene, 'utf8')).includes('sourceMappingURL=scene.min.js.map')
                    || !(await readFile(stylesheet, 'utf8')).includes('sourceMappingURL=site.css.map')) {
                    assert.equal(watcher.exitCode, null, errors);
                    assert.ok(Date.now() < deadline, `Watch mode did not emit Debug assets: ${errors}`);
                    await setTimeout(50);
                }
                assert.notEqual(hash(await readFile(scene)), productionScene);
                assert.notEqual(hash(await readFile(stylesheet)), productionStyles);
            } finally {
                watcher.kill();
                await closed;
            }
        }

        build();
        assert.equal(hash(await readFile(scene)), productionScene, 'Release must restore the minified scene');
        assert.equal(hash(await readFile(stylesheet)), productionStyles, 'Release must restore minified CSS');
        assert.equal((await readdir(join(temporary, 'wwwroot'), { recursive: true })).some(path => path.endsWith('.map')), false);
        const rebuilt = await stat(manifest);
        build();
        assert.equal((await stat(manifest)).mtimeMs, rebuilt.mtimeMs, 'Unchanged production builds must still use the cache');
    });
}

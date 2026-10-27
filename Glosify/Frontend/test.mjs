import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('.', import.meta.url));
for (const [args, cwd] of [
    [['build.mjs', '--test'], root],
    [['--experimental-vm-modules', '--test'], fileURLToPath(new URL('../../Glosify.ClientTests/', import.meta.url))],
]) {
    const result = spawnSync(process.execPath, args, { cwd, stdio: 'inherit' });
    if (result.error) throw result.error;
    if (result.status !== 0) process.exit(result.status ?? 1);
}

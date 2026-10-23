// Local review server. Explicit routes keep source files and other outputs private.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';

const root = new URL('../../../', import.meta.url);
const output = new URL('output/likeness-avatar/', root);
const routes = new Map([
    ['/', [new URL('viewer/index.html', output), 'text/html; charset=utf-8']],
    ['/preview.js', [new URL('viewer/preview.js', output), 'text/javascript']],
    ['/preview.js.LEGAL.txt', [new URL('viewer/preview.js.LEGAL.txt', output), 'text/plain']],
    ['/avatar.glb', [new URL('models/likeness.glb', output), 'model/gltf-binary']],
    ['/favicon.png', [new URL('Glosify/wwwroot/images/favicon-32x32.png', root), 'image/png']],
    ['/images/library.jpg', [new URL('Glosify/wwwroot/images/avatar/library.jpg', root), 'image/jpeg']],
    ['/js/avatar/meshopt.worker.js',
        [new URL('Glosify/wwwroot/js/avatar/meshopt.worker.js', root), 'text/javascript']],
]);
const port = Number(process.argv[2] || 5201);
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Invalid preview port');
const allowedHosts = new Set([`127.0.0.1:${port}`, `localhost:${port}`]);
if (port === 80) {
    allowedHosts.add('127.0.0.1');
    allowedHosts.add('localhost');
}
createServer(async (request, response) => {
    if (!allowedHosts.has(request.headers.host?.toLowerCase())) {
        response.writeHead(403).end('Forbidden host');
        return;
    }
    let pathname;
    try { pathname = new URL(request.url, 'http://localhost').pathname; }
    catch { response.writeHead(400).end(); return; }
    const route = routes.get(pathname);
    if (!route || !['GET', 'HEAD'].includes(request.method)) {
        response.writeHead(404).end('Not found');
        return;
    }
    try {
        const data = await readFile(route[0]);
        response.writeHead(200, {
            'Content-Type': route[1], 'Content-Length': data.length,
            'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff',
        });
        response.end(request.method === 'HEAD' ? undefined : data);
    } catch { response.writeHead(404).end('Preview asset not built'); }
}).listen(port, '127.0.0.1', () => console.log(`Avatar preview: http://127.0.0.1:${port}/`));

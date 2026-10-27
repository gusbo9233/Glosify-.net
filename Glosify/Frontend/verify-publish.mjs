import assert from 'node:assert/strict';
import { readFile, readdir, access } from 'node:fs/promises';
import { resolve } from 'node:path';

assert.ok(process.argv[2], 'Usage: node verify-publish.mjs <publish-directory>');
const published = resolve(process.argv[2]);
const entries = JSON.parse(await readFile(new URL('./entries.json', import.meta.url), 'utf8'));
const required = [
    'Glosify.dll', 'appsettings.json', 'Glosify.staticwebassets.endpoints.json',
    'wwwroot/favicon.ico', 'wwwroot/lib/pdfjs/pdf.min.mjs',
    'wwwroot/images/avatar/library.jpg', 'wwwroot/css/site.css',
    ...Object.keys(entries).map(path => 'wwwroot/js/' + path),
];
for (const path of required) {
    assert.ok((await readFile(resolve(published, path))).length > 0, `Empty published file: ${path}`);
}
assert.ok((await readdir(resolve(published, 'wwwroot/samples'))).some(name => name.startsWith('starter-') && name.endsWith('.pdf')),
    'Guest sample PDFs must be published');
assert.equal(await access(resolve(published, 'Frontend')).then(() => true, () => false), false,
    'Frontend source and dependencies must not be published');
for (const directory of ['wwwroot/js', 'wwwroot/css']) {
    const files = await readdir(resolve(published, directory), { recursive: true });
    assert.equal(files.some(path => /\.(?:map|ts)$/.test(path) || path.endsWith('.source.js')), false,
        `Production ${directory} must contain only generated runtime assets`);
}
console.log('Published configuration, vendor assets, guest samples, and frontend assets verified.');

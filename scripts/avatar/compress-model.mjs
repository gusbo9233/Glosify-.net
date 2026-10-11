import { NodeIO } from '@gltf-transform/core';
import { ALL_EXTENSIONS } from '@gltf-transform/extensions';
import { meshopt } from '@gltf-transform/functions';
import { MeshoptEncoder } from 'meshoptimizer';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';

const [input, output] = process.argv.slice(2);
if (!input || !output || resolve(input) === resolve(output)) {
    throw new Error('Usage: node compress-model.mjs RAW.glb COMPRESSED.glb (use different paths).');
}
const raw = await readFile(input);
const json = JSON.parse(raw.subarray(20, 20 + raw.readUInt32LE(12)).toString());
if (json.extensionsUsed?.includes('EXT_meshopt_compression')) {
    throw new Error('Compress a fresh uncompressed export; repeated quantization loses precision.');
}
await MeshoptEncoder.ready;
const io = new NodeIO().registerExtensions(ALL_EXTENSIONS)
    .registerDependencies({ 'meshopt.encoder': MeshoptEncoder });
const document = await io.readBinary(raw);
// Preserve geometry, materials, textures, morph names and topology. Do not simplify.
await document.transform(meshopt({ encoder: MeshoptEncoder, level: 'high',
    quantizePosition: 14, quantizeNormal: 10, quantizeTexcoord: 12 }));
const bytes = await io.writeBinary(document);
await mkdir(dirname(resolve(output)), { recursive: true });
await writeFile(output, bytes);
console.log(`${input}: ${(raw.length / 1e6).toFixed(2)} MB -> ${(bytes.length / 1e6).toFixed(2)} MB`);

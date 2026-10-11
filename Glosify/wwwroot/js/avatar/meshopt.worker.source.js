import { MeshoptDecoder } from 'meshoptimizer/meshopt_decoder.module.js';

// Observe initialization failure immediately; requests below report it to the page.
MeshoptDecoder.ready?.catch(() => {});
self.onmessage = async ({ data: { id, source, count, stride, mode, filter } }) => {
    try {
        if (!MeshoptDecoder.supported) throw new Error('WebAssembly is required for this avatar.');
        await MeshoptDecoder.ready;
        const result = new Uint8Array(count * stride);
        MeshoptDecoder.decodeGltfBuffer(result, count, stride, source, mode, filter);
        self.postMessage({ id, result }, [result.buffer]);
    } catch (error) {
        self.postMessage({ id, error: error.message || 'Avatar geometry could not be decoded.' });
    }
};

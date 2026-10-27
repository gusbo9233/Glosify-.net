import { GLTFLoader, ArrayItem } from '@babylonjs/loaders/glTF/2.0/glTFLoader.js';
import { registerGLTFExtension, unregisterGLTFExtension } from '@babylonjs/loaders/glTF/2.0/glTFLoaderExtensionRegistry.js';
import { MeshoptWorkerClient } from './meshopt-worker-client.js';

interface MeshoptExtension {
    buffer: number; byteOffset?: number; byteLength: number;
    count: number; byteStride: number; mode: string; filter?: string;
}

// Override Babylon's CDN/blob-worker decoder with a bundled same-origin worker.
// The loader owns the extension and disposes its worker when the load completes.
const name = 'EXT_meshopt_compression';
unregisterGLTFExtension(name);
registerGLTFExtension(name, true, loader => {
    let decoder: MeshoptWorkerClient | undefined;
    let disposed = false;
    const decoded = new WeakMap<object, Promise<Uint8Array>>();
    return {
        name,
        enabled: loader.isExtensionUsed(name),
        loadBufferViewAsync(context, view) {
            return GLTFLoader.LoadExtensionAsync<MeshoptExtension, Uint8Array>(context, view, name, (extensionContext, extension) => {
                if (!decoded.has(view)) {
                    const buffer = ArrayItem.Get(`${extensionContext}/buffer`, loader.gltf.buffers, extension.buffer);
                    const promise = loader.loadBufferAsync(`/buffers/${buffer.index}`, buffer,
                        extension.byteOffset || 0, extension.byteLength).then(source => {
                        if (disposed) throw new Error('Avatar model loading was cancelled.');
                        decoder ??= new MeshoptWorkerClient();
                        return decoder.decode(new Uint8Array(source.buffer, source.byteOffset, source.byteLength),
                            extension.count, extension.byteStride, extension.mode, extension.filter);
                    });
                    decoded.set(view, promise);
                }
                return decoded.get(view)!;
            });
        },
        dispose() { disposed = true; decoder?.dispose(); }
    };
});

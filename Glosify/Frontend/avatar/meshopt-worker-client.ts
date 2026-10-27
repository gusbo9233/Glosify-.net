// One local worker per model load. Copy compressed views before transferring:
// the loader's source buffer is shared by other views and must not be detached.
export class MeshoptWorkerClient {
    private worker: Worker;
    private pending: Map<number, { resolve: (result: Uint8Array) => void; reject: (error: unknown) => void }>;
    private nextId: number;
    private closed: boolean;
    constructor(createWorker = () => new Worker('/js/avatar/meshopt.worker.js?v=0.24.0-1')) {
        this.worker = createWorker();
        this.pending = new Map(); this.nextId = 0; this.closed = false;
        this.worker.onmessage = ({ data }) => {
            const request = this.pending.get(data.id);
            if (!request) return;
            this.pending.delete(data.id);
            if (data.error) request.reject(new Error(data.error));
            else request.resolve(data.result);
        };
        this.worker.onerror = () => this.dispose(new Error('Avatar geometry decoder failed to load.'));
        this.worker.onmessageerror = () => this.dispose(new Error('Avatar geometry decoder returned invalid data.'));
    }
    decode(source: Uint8Array, count: number, stride: number, mode: string, filter?: string) {
        if (this.closed) return Promise.reject(new Error('Avatar geometry decoder is closed.'));
        const id = ++this.nextId, copy = source.slice();
        return new Promise<Uint8Array>((resolve, reject) => {
            this.pending.set(id, { resolve, reject });
            try { this.worker.postMessage({ id, source: copy, count, stride, mode, filter }, [copy.buffer]); }
            catch (error) { this.pending.delete(id); reject(error); }
        });
    }
    dispose(error = new Error('Avatar geometry decoding was cancelled.')) {
        if (this.closed) return;
        this.closed = true; this.worker.terminate();
        for (const request of this.pending.values()) request.reject(error);
        this.pending.clear();
    }
}

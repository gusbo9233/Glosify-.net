declare const sampleRate: number;
declare abstract class AudioWorkletProcessor {
    readonly port: MessagePort;
    abstract process(inputs: Float32Array[][], outputs: Float32Array[][], parameters: Record<string, Float32Array>): boolean;
}
declare function registerProcessor(name: string, processor: new () => AudioWorkletProcessor): void;

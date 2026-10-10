/* global AudioWorkletProcessor, sampleRate, registerProcessor */
// Mono PCM16 at 16 kHz in 200 ms frames, independent of the hardware sample rate.
class AvatarCapture extends AudioWorkletProcessor {
    constructor() {
        super(); this.frame = new Int16Array(3200); this.position = 0; this.phase = 0; this.sum = 0; this.samples = 0;
        this.port.onmessage = event => {
            if (event.data?.type !== 'flush') return;
            if (this.position) {
                const tail = this.frame.slice(0, this.position);
                this.port.postMessage({ pcm: tail.buffer, rms: 0 }, [tail.buffer]);
                this.position = 0;
            }
            this.port.postMessage({ flushed: true });
        };
    }
    process(inputs) {
        const channel = inputs[0]?.[0];
        if (!channel) return true;
        const ratio = sampleRate / 16000;
        for (const value of channel) {
            this.sum += value; this.samples++; this.phase++;
            if (this.phase >= ratio) {
                const sample = Math.max(-1, Math.min(1, this.sum / this.samples));
                this.frame[this.position++] = sample < 0 ? sample * 32768 : sample * 32767;
                this.phase -= ratio; this.sum = 0; this.samples = 0;
                if (this.position === this.frame.length) {
                    let energy = 0;
                    for (const x of this.frame) energy += (x / 32768) ** 2;
                    this.port.postMessage({ pcm: this.frame.buffer, rms: Math.sqrt(energy / this.frame.length) }, [this.frame.buffer]);
                    this.frame = new Int16Array(3200); this.position = 0;
                }
            }
        }
        return true;
    }
}
registerProcessor('avatar-capture', AvatarCapture);

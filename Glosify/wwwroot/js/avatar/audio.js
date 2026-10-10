export class AvatarAudio {
    constructor() { this.sources = new Set(); this.endAt = 0; this.generation = 0; }
    async open(onFrame) {
        this.stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true }, video: false });
        try {
            this.context = new AudioContext();
            await this.context.resume();
            await this.context.audioWorklet.addModule('/js/avatar/capture.worklet.js');
            this.capture = new AudioWorkletNode(this.context, 'avatar-capture');
            this.input = this.context.createMediaStreamSource(this.stream);
            this.silent = this.context.createGain(); this.silent.gain.value = 0;
            this.input.connect(this.capture).connect(this.silent).connect(this.context.destination);
            this.capture.port.onmessage = event => {
                if (event.data.flushed) { this.flushReady?.(); this.flushReady = null; }
                else onFrame(event.data);
            };
            this.analyser = this.context.createAnalyser(); this.analyser.fftSize = 512;
            this.analyser.connect(this.context.destination);
        } catch (error) { await this.close(); throw error; }
    }
    flush() {
        if (!this.capture || !this.context || this.context.state !== 'running') return Promise.resolve();
        return new Promise(resolve => { this.flushReady = resolve; this.capture.port.postMessage({ type: 'flush' }); });
    }
    mute(value) { this.stream?.getAudioTracks().forEach(track => { track.enabled = !value; }); }
    play(encoded) {
        const raw = atob(encoded);
        if (raw.length % 2) throw new Error('Invalid speech audio.');
        const bytes = Uint8Array.from(raw, x => x.charCodeAt(0));
        const pcm = new DataView(bytes.buffer);
        const buffer = this.context.createBuffer(1, bytes.length / 2, 24000);
        const samples = buffer.getChannelData(0);
        for (let i = 0; i < samples.length; i++) samples[i] = pcm.getInt16(i * 2, true) / 32768;
        const source = this.context.createBufferSource(); source.buffer = buffer; source.connect(this.analyser);
        const when = Math.max(this.context.currentTime + .04, this.endAt);
        this.endAt = when + buffer.duration;
        this.sources.add(source);
        source.onended = () => { this.sources.delete(source); source.disconnect(); };
        source.start(when);
    }
    async finished() {
        const generation = this.generation;
        while (this.context && this.context.currentTime < this.endAt + .02) {
            await new Promise(resolve => setTimeout(resolve, 30));
            if (generation !== this.generation) return false;
        }
        return generation === this.generation;
    }
    stop() {
        this.generation++;
        for (const source of this.sources) { try { source.stop(); } catch {} }
        this.sources.clear(); this.endAt = 0;
    }
    async close() {
        this.stop(); this.flushReady?.(); this.flushReady = null;
        this.stream?.getTracks().forEach(track => track.stop()); this.stream = null;
        if (this.capture) this.capture.port.onmessage = null;
        this.input?.disconnect(); this.capture?.disconnect(); this.silent?.disconnect();
        if (this.context && this.context.state !== 'closed') await this.context.close();
        this.context = null;
    }
}

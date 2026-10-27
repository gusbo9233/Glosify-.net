export class AvatarAudio {
    stream: MediaStream | null = null;
    context: AudioContext | null = null;
    analyser: AnalyserNode | null = null;
    private capture?: AudioWorkletNode;
    private input?: MediaStreamAudioSourceNode;
    private silent?: GainNode;
    private flushReady: (() => void) | null = null;
    private sources: Set<AudioBufferSourceNode>;
    private endAt: number;
    private generation: number;
    constructor() { this.sources = new Set(); this.endAt = 0; this.generation = 0; }
    async open(onFrame: (frame: { pcm: ArrayBuffer; rms: number }) => void) {
        const generation = this.generation;
        const stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true }, video: false });
        if (generation !== this.generation) { stream.getTracks().forEach(track => track.stop()); return false; }
        this.stream = stream;
        try {
            this.context = new AudioContext();
            await this.context.resume();
            if (generation !== this.generation) { await this.close(); return false; }
            await this.context.audioWorklet.addModule('/js/avatar/capture.worklet.js');
            if (generation !== this.generation) { await this.close(); return false; }
            this.capture = new AudioWorkletNode(this.context, 'avatar-capture');
            this.input = this.context.createMediaStreamSource(this.stream);
            this.silent = this.context.createGain(); this.silent.gain.value = 0;
            this.input.connect(this.capture).connect(this.silent).connect(this.context.destination);
            this.capture.port.onmessage = event => {
                if (event.data.flushed) { this.flushReady?.(); this.flushReady = null; }
                else onFrame(event.data);
            };
            // Little smoothing so the viewer's mouth shapes follow individual syllables.
            this.analyser = this.context.createAnalyser(); this.analyser.fftSize = 512; this.analyser.smoothingTimeConstant = .25;
            this.analyser.connect(this.context.destination);
            return true;
        } catch (error) { await this.close(); throw error; }
    }
    flush() {
        if (!this.capture || !this.context || this.context.state !== 'running') return Promise.resolve();
        return new Promise<void>((resolve, reject) => {
            const timer = setTimeout(() => { this.flushReady = null; reject(new Error('Microphone flush timed out.')); }, 1000);
            this.flushReady = () => { clearTimeout(timer); resolve(); };
            this.capture!.port.postMessage({ type: 'flush' });
        });
    }
    mute(value: boolean) { this.stream?.getAudioTracks().forEach(track => { track.enabled = !value; }); }
    play(encoded: string) {
        if (!this.context || !this.analyser) throw new Error('Voice audio is not open.');
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
        for (const source of this.sources) { try { source.stop(); } catch { /* Playback can already have ended. */ } }
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

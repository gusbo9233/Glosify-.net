// Reconnect transport only. Never queue or replay audio/commands across sockets.
export class VoiceConnection {
    constructor(url, { onMessage, onLost, onEnded }, {
        createSocket = value => new WebSocket(value), now = () => Date.now(),
        schedule = (action, delay) => setTimeout(action, delay), cancel = id => clearTimeout(id)
    } = {}) {
        Object.assign(this, { url, onMessage, onLost, onEnded, createSocket, now, schedule, cancel });
        this.closed = false; this.lostAt = null; this.attempt = 0; this.nextAttempt = 0;
        this.connect(); this.watch();
    }
    get readyState() { return this.socket?.readyState ?? 3; }
    connect() {
        if (this.closed) return;
        this.connected = false; this.startedAt = this.lastReceived = this.lastPing = this.now();
        let socket;
        try { socket = this.createSocket(this.url); }
        catch { this.fail(); return; }
        this.socket = socket;
        socket.onmessage = event => {
            if (this.closed || socket !== this.socket) return;
            let message;
            try { message = JSON.parse(event.data); if (!message || typeof message.type !== 'string') throw new Error(); }
            catch { this.finish('The voice connection returned an invalid message. Please start again.'); return; }
            this.lastReceived = this.now();
            if (message.type === 'pong') return;
            if (message.type === 'ended') {
                this.finish('Conversation ended. Start again whenever you like.'); return;
            }
            if (message.type === 'connected') {
                message.resumed = this.lostAt !== null;
                this.connected = true; this.lostAt = null; this.attempt = 0;
            }
            try { Promise.resolve(this.onMessage(message)).catch(() => {
                if (socket === this.socket && !this.closed) this.finish('Voice playback failed. Please start again.');
            }); }
            catch { this.finish('Voice playback failed. Please start again.'); }
        };
        socket.onerror = () => { if (socket === this.socket) this.fail(); };
        socket.onclose = event => {
            if (socket !== this.socket || this.closed) return;
            if (event.code === 1000 || event.code === 1008) this.finish('Conversation ended. Start again whenever you like.');
            else this.fail();
        };
    }
    detach() {
        const socket = this.socket; this.socket = null;
        if (socket) {
            socket.onmessage = socket.onerror = socket.onclose = null;
            try { socket.close(); } catch { /* Already disconnected. */ }
        }
    }
    fail() {
        if (this.closed) return;
        this.detach(); this.connected = false;
        if (this.lostAt === null) { this.lostAt = this.now(); this.onLost(); }
        this.nextAttempt = this.now() + Math.min(1000 * 2 ** this.attempt++, 8000);
    }
    send(data) {
        if (this.closed) return false;
        // Allow the five-second setup buffer to drain in one batch, but bound
        // stalled transport to six seconds of 16 kHz PCM (192 KB).
        if (this.socket?.readyState !== 1 || this.socket.bufferedAmount > 192000) { this.fail(); return false; }
        try { this.socket.send(data); return true; }
        catch { this.fail(); return false; }
    }
    watch() {
        this.timer = this.schedule(() => {
            if (this.closed) return;
            const now = this.now();
            if (this.lostAt !== null && now - this.lostAt >= 45000) {
                this.finish('Could not reconnect. Your microphone is off. Start a new conversation when the connection returns.'); return;
            }
            if (!this.socket) { if (now >= this.nextAttempt) this.connect(); }
            else if (!this.connected && now - this.startedAt >= 10000) this.fail();
            else if (this.connected && now - this.lastReceived >= 20000) this.fail();
            else if (this.connected && now - this.lastPing >= 5000) {
                this.lastPing = now; this.send(JSON.stringify({ type: 'ping' }));
            }
            if (!this.closed) this.watch();
        }, 1000);
    }
    finish(detail) { this.close(false); this.onEnded(detail); }
    close(notify = true) {
        if (this.closed) return;
        this.closed = true; this.cancel(this.timer);
        if (notify && this.socket?.readyState === 1) {
            try { this.socket.send(JSON.stringify({ type: 'end' })); } catch { /* Cleanup also uses HTTP. */ }
        }
        this.detach();
    }
}

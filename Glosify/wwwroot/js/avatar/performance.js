// Rain's procedural performance: speech-shaped mouth, eye contact, blinks, expressions
// and posture. It has no Babylon dependency so it can be tested in Node; scene.source.js
// applies the returned weights to the baked morph targets of the same names.

const clamp = (value, min = 0, max = 1) => Math.min(max, Math.max(min, value));
const ease = (value, target, dt, seconds) => value + (target - value) * (1 - Math.exp(-dt / seconds));

// Approximates mouth shapes from the playback spectrum (dB per frequency bin); it is
// not phoneme-exact. Band levels are compared in dB: energy near 1 kHz relative to the
// voice fundamental opens the jaw (a, o), energy at 2-3 kHz above the 1 kHz valley
// spreads the lips (i, e), its absence rounds them (u, o) and hiss parts the lips (s).
export function speechShape(spectrum, sampleRate, level) {
    const bin = sampleRate / (spectrum.length * 2);
    const band = (from, to) => {
        let sum = 0, count = 0;
        for (let i = Math.max(1, Math.round(from / bin)); i < Math.min(spectrum.length, Math.round(to / bin)); i++, count++) sum += 10 ** (spectrum[i] / 10);
        return 10 * Math.log10(sum / Math.max(1, count) + 1e-30);
    };
    const loud = clamp((20 * Math.log10(level + 1e-9) + 46) / 28);
    const low = band(100, 450), open = band(800, 1300), front = band(1900, 3000), hiss = band(4500, 11000);
    const openness = clamp((open - low + 22) / 20), spread = front - open, sibilance = clamp((hiss - low + 32) / 16);
    const voice = loud * (1 - sibilance * .7);
    return {
        loud,
        jaw: voice * (.25 + .75 * openness),
        wide: voice * clamp((spread + 6) / 16) * .8 + loud * sibilance * .3,
        round: voice * clamp((-spread - 10) / 14) * (1 - openness * .5),
        part: loud * (.2 + sibilance * .6)
    };
}

// Resting expression and posture for each conversation state.
const moods = {
    off: { smile: .3, brow: .04, inner: 0, down: 0, squint: .1, press: 0, lean: 0, nod: 0, tilt: .1 },
    connecting: { smile: .26, brow: .12, inner: 0, down: 0, squint: .08, press: 0, lean: .2, nod: 0, tilt: 0 },
    idle: { smile: .36, brow: .1, inner: 0, down: 0, squint: .12, press: 0, lean: .2, nod: 0, tilt: .15 },
    listening: { smile: .2, brow: .12, inner: .1, down: 0, squint: .05, press: 0, lean: 1, nod: .25, tilt: .35 },
    thinking: { smile: .06, brow: 0, inner: .14, down: .22, squint: .22, press: .4, lean: .4, nod: -.15, tilt: .25 },
    speaking: { smile: .18, brow: .06, inner: 0, down: 0, squint: .08, press: 0, lean: .35, nod: 0, tilt: 0 },
    error: { smile: 0, brow: 0, inner: .45, down: 0, squint: 0, press: .2, lean: 0, nod: .2, tilt: .3 }
};
// Chance per second of glancing away from the viewer.
const glances = { off: .09, connecting: .05, idle: .07, listening: .025, thinking: 0, speaking: .1, error: .03 };

// Smooth, repeatable wandering in [-1, 1].
function wander(seed) {
    const hash = n => { const x = Math.sin(n * 127.1 + seed * 311.7) * 43758.5453; return (x - Math.floor(x)) * 2 - 1; };
    const smooth = t => { const i = Math.floor(t), f = t - i; return hash(i) + (hash(i + 1) - hash(i)) * f * f * (3 - 2 * f); };
    return t => smooth(t) * .65 + smooth(t * 2.3 + 17) * .35;
}
function spring(frequency, damping) {
    const w = 2 * Math.PI * frequency;
    let value = 0, velocity = 0;
    return {
        get value() { return value; },
        step(target, dt) {
            velocity += (w * w * (target - value) - 2 * damping * w * velocity) * dt;
            value += velocity * dt; return value;
        }
    };
}

export function createPerformance(random = Math.random) {
    const between = (from, to) => from + (to - from) * random();
    const side = () => random() < .5 ? -1 : 1;
    const drift = Object.fromEntries(['nod', 'turn', 'tilt', 'smile', 'brow', 'sway', 'yaw'].map((name, i) => [name, wander(i + 1)]));
    const head = { nod: spring(1.1, .75), turn: spring(.9, .8), tilt: spring(.8, .85), shrug: spring(1.4, .9) };
    const mood = { ...moods.off }, mouth = { jaw: 0, round: 0, wide: 0, part: 0 };
    const gaze = { x: 0, y: 0, targetX: 0, targetY: 0, until: 0, next: 0 };
    let state = 'off', time = 0, gestures = [], tiltSide = side(), lean = 0, breath = 0;
    let blinkAt = between(1, 3), blinkStart = -1, doubleBlinkAt = Infinity;
    let fast = 0, slow = 0, silence = 1, beatReady = 0, voiceUntil = -1, talk = 0, quiet = 0, nodReady = 0;
    let question = false, lively = 1, greeted = false;

    const gesture = (channel, amount, duration, delay = 0) => gestures.push({ channel, amount, start: time + delay, duration });
    const blinkSoon = () => { blinkAt = Math.min(blinkAt, time + .03); };
    const contact = () => { gaze.until = gaze.next = time; };
    function look(x, y, seconds) {
        if (Math.hypot(x - gaze.targetX, y - gaze.targetY) > .3 && random() < .5) blinkSoon();
        gaze.targetX = x; gaze.targetY = y; gaze.until = time + seconds; gaze.next = gaze.until;
    }
    // Emphasis while speaking: a small nod, sometimes raised brows, a turn or a shrug.
    function beat() {
        beatReady = time + between(.7, 1.5);
        gesture('nod', between(.35, .7) * lively, between(.28, .4));
        if (random() < .4) {
            const duration = between(.4, .7);
            gesture(random() < .15 ? (random() < .5 ? 'browLeft' : 'browRight') : 'brow', between(.25, .55) * lively, duration);
            gesture('wide', between(.15, .3) * lively, duration);
        }
        if (random() < .25) gesture('turn', side() * between(.2, .45), between(.5, .9));
        if (random() < .08) gesture('shrug', between(.25, .45), between(.6, .9));
    }
    // Acknowledge the learner's pauses with a small double nod.
    function backchannel() {
        nodReady = time + between(2.2, 4); talk = 0;
        gesture('nod', .7, .4); gesture('nod', .45, .38, .42);
        if (random() < .5) gesture('smile', .14, 1.4);
    }
    function setState(next) {
        if (next === state || !moods[next]) return;
        const previous = state; state = next;
        if (gaze.until === Infinity) contact();
        if (previous === 'speaking' && next !== 'speaking') {
            blinkSoon();
            // After asking a question Rain raises her brows and tilts her head, waiting.
            if (question) { gesture('brow', .35, 1.8); gesture('tilt', tiltSide * .6, 2.2); gesture('smile', .12, 2); }
            question = false;
        }
        if (next === 'off') greeted = false;
        if (next === 'idle' && previous === 'connecting' && !greeted) { greeted = true; gesture('smile', .25, 1.6); gesture('brow', .3, 1); gesture('wide', .2, 1); }
        if (next === 'listening') { tiltSide = side(); contact(); gesture('brow', .2, .8); talk = 0; quiet = 0; }
        if (next === 'thinking') { tiltSide = side(); look(tiltSide * between(.3, .5), between(.3, .45), Infinity); }
        if (next === 'speaking') { if (random() < .6) look(side() * between(.25, .45), between(-.1, .15), between(.5, 1)); gesture('brow', .2, .6); }
        if (next === 'error') { gesture('shrug', .7, 1); gesture('tilt', tiltSide * .5, 1.6); }
    }
    function update(elapsed, audio = null, reduced = false) {
        // Long frames (a busy main thread) must not destabilise the head springs.
        const dt = clamp(elapsed, 0, .1);
        time += dt;
        for (const name in mood) mood[name] = ease(mood[name], moods[state][name], dt, .45);
        lean = ease(lean, mood.lean, dt, 1.2);

        // Mouth: fast attack, slightly slower release so syllables stay readable.
        const shape = audio ? speechShape(audio.spectrum, audio.sampleRate, audio.level) : { loud: 0, jaw: 0, round: 0, wide: 0, part: 0 };
        for (const name in mouth) mouth[name] = ease(mouth[name], shape[name], dt, shape[name] > mouth[name] ? .035 : .07);
        fast = ease(fast, shape.loud, dt, .06); slow = ease(slow, shape.loud, dt, .45);
        if (shape.loud < .08) silence += dt;
        else {
            // Speakers often glance away briefly as a new phrase begins.
            if (silence > .45 && state === 'speaking' && random() < .4) look(side() * between(.2, .45), between(-.15, .2), between(.4, 1));
            silence = 0;
        }
        if (state === 'speaking' && fast - slow > .16 && fast > .35 && time >= beatReady) beat();

        if (state === 'listening') {
            if (time < voiceUntil) { talk += dt; quiet = 0; }
            else {
                quiet += dt;
                if (quiet > 1.2) talk = 0;
                else if (talk > .8 && quiet >= .35 && time >= nodReady) backchannel();
            }
        }

        // Eye contact with small fixational saccades, occasional glances away.
        if (time >= gaze.until) {
            if (time >= gaze.next) { gaze.targetX = between(-.05, .05); gaze.targetY = between(-.035, .035); gaze.next = time + between(.5, 2.2); }
            if (random() < glances[state] * dt) look(side() * between(.25, .6), between(-.2, .25), between(.7, 2));
        }
        gaze.x = ease(gaze.x, gaze.targetX, dt, .03); gaze.y = ease(gaze.y, gaze.targetY, dt, .03);

        if (time >= blinkAt) {
            blinkStart = time; blinkAt = time + between(2, 6) * ({ listening: 1.25, speaking: .85, thinking: .8 }[state] || 1);
            if (random() < .12) doubleBlinkAt = time + .28;
        }
        if (time >= doubleBlinkAt) { blinkStart = time; doubleBlinkAt = Infinity; }
        const since = time - blinkStart;
        const blink = reduced || blinkStart < 0 ? 0 : since < .07 ? (since / .07) ** 2 : since < .1 ? 1 : since < .24 ? 1 - ((since - .1) / .14) ** .7 : 0;

        gestures = gestures.filter(item => time < item.start + item.duration);
        const g = { nod: 0, turn: 0, tilt: 0, shrug: 0, smile: 0, brow: 0, browLeft: 0, browRight: 0, wide: 0 };
        for (const item of gestures) if (time >= item.start) g[item.channel] += item.amount * Math.sin(Math.PI * (time - item.start) / item.duration);

        // The head partly follows the gaze; the eyes counter-rotate to hold eye contact.
        // One nod/turn unit is about 3.9 degrees and one gaze unit about 19 degrees.
        const nod = head.nod.step(drift.nod(time * .23) * .22 + mood.nod + g.nod - gaze.targetY * .45, dt);
        const turn = head.turn.step(drift.turn(time * .17) * .38 + gaze.targetX * .9 + g.turn, dt);
        const tilt = head.tilt.step(drift.tilt(time * .13) * .2 + mood.tilt * tiltSide + g.tilt, dt);
        const shrug = head.shrug.step(g.shrug, dt);
        const eyeX = clamp(gaze.x - turn * .21, -.7, .7), eyeY = clamp(gaze.y + nod * .185, -.6, .6);
        breath += dt / (state === 'speaking' ? 5 : 4.2);
        const breathing = (1 - Math.cos(2 * Math.PI * breath)) / 2 * (state === 'speaking' ? .5 : 1);

        const open = 1 - blink, speech = clamp(mouth.jaw * 2);
        const smile = clamp(mood.smile + g.smile), smileTilt = drift.smile(time * .11) * .12;
        const brow = mood.brow + g.brow, browTilt = drift.brow(time * .15) * .1;
        const weights = {
            jawOpen: mouth.jaw, mouthWide: mouth.wide, mouthRound: mouth.round, lipsPart: mouth.part,
            mouthPress: clamp(mood.press * (1 - speech)),
            smileLeft: clamp(smile * (1 + smileTilt)), smileRight: clamp(smile * (1 - smileTilt)),
            browRaiseLeft: clamp(brow * (1 + browTilt) + g.browLeft), browRaiseRight: clamp(brow * (1 - browTilt) + g.browRight),
            browInner: clamp(mood.inner), browDown: clamp(mood.down),
            squint: clamp(mood.squint + smile * .15) * open, eyeWide: clamp(g.wide) * open, blink,
            lookLeft: Math.max(0, eyeX), lookRight: Math.max(0, -eyeX),
            lookUp: Math.max(0, eyeY) * open, lookDown: Math.max(0, -eyeY) * open,
            nod, turn, tilt, shrug: clamp(shrug, -.2, 1.2), breathe: breathing
        };
        let posture = { lean: lean * .012, sway: drift.sway(time * .09) * .005, yaw: drift.yaw(time * .06) * .012, rise: breathing * .0012 };
        if (reduced) {
            for (const name of ['lookLeft', 'lookRight', 'lookUp', 'lookDown', 'nod', 'turn', 'tilt', 'shrug', 'breathe']) weights[name] = 0;
            posture = { lean: 0, sway: 0, yaw: 0, rise: 0 };
        }
        return { weights, posture };
    }
    return {
        setState,
        // Shapes the reply's body language: questions invite an answer, exclamations add energy.
        setReply(text) { question = /[?？؟]["'”»)\]\s]*$/.test(text); lively = /[!！]/.test(text) ? 1.25 : 1; },
        // Microphone frames cover 200 ms, so a voiced frame counts as speech for that long.
        hear(level) { if (level > .03) voiceUntil = time + .25; },
        update,
        get state() { return state; }
    };
}

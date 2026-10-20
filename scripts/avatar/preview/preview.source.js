// Standalone local review. The animation, morph binding and frame scheduler are
// the production modules; the model and review controls are isolated here.
import { Engine } from '@babylonjs/core/Engines/engine.js';
import { Scene } from '@babylonjs/core/scene.js';
import { ArcRotateCamera } from '@babylonjs/core/Cameras/arcRotateCamera.js';
import { Vector3 } from '@babylonjs/core/Maths/math.vector.js';
import { Color3, Color4 } from '@babylonjs/core/Maths/math.color.js';
import { HemisphericLight } from '@babylonjs/core/Lights/hemisphericLight.js';
import { DirectionalLight } from '@babylonjs/core/Lights/directionalLight.js';
import { TransformNode } from '@babylonjs/core/Meshes/transformNode.js';
import { ImportMeshAsync } from '@babylonjs/core/Loading/sceneLoader.js';
import { ImageProcessingConfiguration } from '@babylonjs/core/Materials/imageProcessingConfiguration.js';
import '@babylonjs/loaders/glTF';
import '../../../Glosify/wwwroot/js/avatar/meshopt.js';
import { createPerformance } from '../../../Glosify/wwwroot/js/avatar/performance.js';
import { bindMorphTargets } from '../../../Glosify/wwwroot/js/avatar/morphs.js';
import { createRenderLoop, portraitFraming } from '../../../Glosify/wwwroot/js/avatar/render-loop.js';

const canvas = document.getElementById('portrait');
const status = document.getElementById('status');
const audioElement = document.getElementById('audio');
const controls = document.querySelectorAll('button, select, input');
const reduced = matchMedia('(prefers-reduced-motion: reduce)');
const engine = new Engine(canvas, true, { stencil: false, powerPreference: 'low-power' });
engine.setHardwareScalingLevel(Math.max(.7, 1 / Math.min(devicePixelRatio, 1.5)));
const scene = new Scene(engine);
scene.clearColor = new Color4(0, 0, 0, 0);
scene.skipPointerMovePicking = true;
scene.imageProcessingConfiguration.toneMappingEnabled = true;
scene.imageProcessingConfiguration.toneMappingType = ImageProcessingConfiguration.TONEMAPPING_ACES;
scene.imageProcessingConfiguration.exposure = 1.05;
const camera = new ArcRotateCamera('portrait', -Math.PI / 2, Math.PI / 2 - .10, 1.65,
    new Vector3(0, 1.46, 0), scene);
camera.minZ = .02;
function frame() {
    const framing = portraitFraming(canvas.clientWidth, canvas.clientHeight,
        { compact: canvas.clientWidth < 600, distance: camera.radius });
    camera.fov = framing.fov;
    camera.target.y = framing.targetY + .015;
}
frame();
const fill = new HemisphericLight('soft daylight', new Vector3(0, 1, -1), scene);
fill.intensity = .8;
fill.diffuse = new Color3(.98, 1, .97);
fill.groundColor = new Color3(.3, .25, .19);
const key = new DirectionalLight('key', new Vector3(.5, -1, 1), scene);
key.intensity = 1.5;
key.diffuse = new Color3(1, .89, .76);
const rim = new DirectionalLight('rim', new Vector3(-.7, -.2, -.8), scene);
rim.intensity = .9;
rim.diffuse = new Color3(1, .92, .79);

let loop, resize, intersection, disposed = false, inView = true;
let context, analyser, source, spectrum, wave, audioUrl;
let state = 'idle', angle = 0, pulse = null, pulseTimer, smileAtRest = 1;
const actor = createPerformance();

function updateActivity() {
    loop?.setVisible(!document.hidden && inView);
}
function updateMotion() {
    loop?.setReducedMotion(reduced.matches);
}
function clearPulse() {
    clearTimeout(pulseTimer);
    pulse = null;
    loop?.setState(state);
    loop?.invalidate();
}
function setState(next) {
    state = next;
    actor.setState(next);
    // A queued media pause can change the actor state while an explicit demo
    // is playing. Keep frames running for that demo, including reduced motion.
    loop?.setState(pulse ? 'speaking' : next);
    for (const button of document.querySelectorAll('[data-state]')) {
        button.setAttribute('aria-pressed', String(button.dataset.state === next));
    }
}
function ensureAudio() {
    if (context) return;
    context = new AudioContext();
    analyser = context.createAnalyser();
    analyser.fftSize = 512;
    analyser.smoothingTimeConstant = .25;
    source = context.createMediaElementSource(audioElement);
    source.connect(analyser);
    analyser.connect(context.destination);
    spectrum = new Float32Array(analyser.frequencyBinCount);
    wave = new Float32Array(analyser.fftSize);
}
function dispose() {
    if (disposed) return;
    disposed = true;
    clearTimeout(pulseTimer);
    audioElement.pause();
    if (audioUrl) URL.revokeObjectURL(audioUrl);
    source?.disconnect();
    analyser?.disconnect();
    context?.close();
    loop?.dispose();
    resize?.disconnect();
    intersection?.disconnect();
    document.removeEventListener('visibilitychange', updateActivity);
    reduced.removeEventListener('change', updateMotion);
    scene.dispose();
    engine.dispose();
}
addEventListener('pagehide', dispose, { once: true });
addEventListener('pageshow', event => { if (event.persisted) location.reload(); });

try {
    const loaded = await ImportMeshAsync('/avatar.glb', scene);
    if (!disposed) {
        const root = new TransformNode('performance', scene);
        root.rotation.y = Math.PI;
        for (const mesh of loaded.meshes) {
            if (!mesh.parent) mesh.parent = root;
            mesh.isPickable = false;
        }
        const applyWeights = bindMorphTargets(loaded.meshes);
        loop = createRenderLoop(elapsed => {
            let audio = null;
            if (analyser && state === 'speaking' && !audioElement.paused) {
                analyser.getFloatFrequencyData(spectrum);
                analyser.getFloatTimeDomainData(wave);
                audio = { spectrum, sampleRate: context.sampleRate,
                    level: Math.sqrt(wave.reduce((sum, value) => sum + value * value, 0) / wave.length) };
            }
            const { weights, posture } = actor.update(elapsed, audio, reduced.matches);
            // Use the selected expression on the neutral mesh, without stacking
            // the production actor's automatic smile on top.
            const restingSmile = state === 'idle' ? smileAtRest : 0;
            weights.smileLeft = weights.smileRight = restingSmile;
            if (state === 'idle') {
                for (const name of ['browRaiseLeft', 'browRaiseRight', 'browInner', 'browDown',
                    'squint', 'eyeWide', 'mouthPress', 'jawOpen', 'mouthWide', 'mouthRound', 'lipsPart']) {
                    weights[name] = 0;
                }
            }
            if (pulse) {
                const t = Math.min(1, (performance.now() - pulse.start) / pulse.duration);
                const envelope = Math.sin(Math.PI * t);
                if (pulse.name === 'blink') {
                    weights.blink = envelope;
                } else {
                    weights.jawOpen = .65 * Math.abs(Math.sin(t * Math.PI * 13)) * envelope;
                    weights.mouthWide = .35 * Math.abs(Math.sin(t * Math.PI * 7)) * envelope;
                    weights.mouthRound = .45 * Math.abs(Math.cos(t * Math.PI * 9)) * envelope;
                }
            }
            applyWeights(weights);
            root.position.set(0, posture.rise, -posture.lean);
            root.rotation.set(0, Math.PI + angle + posture.yaw, posture.sway);
            engine.beginFrame();
            scene.render();
            engine.endFrame();
        });
        resize = new ResizeObserver(() => { engine.resize(); frame(); loop.invalidate(); });
        resize.observe(canvas);
        intersection = new IntersectionObserver(entries => {
            inView = entries[0].isIntersecting;
            updateActivity();
        });
        intersection.observe(canvas);
        document.addEventListener('visibilitychange', updateActivity);
        reduced.addEventListener('change', updateMotion);
        updateMotion();
        updateActivity();
        scene.onReadyObservable.addOnce(() => loop.invalidate());
        engine.onContextRestoredObservable.add(() => loop.invalidate());
        await scene.whenReadyAsync();
        if (!disposed) {
            controls.forEach(control => { control.disabled = false; });
            status.textContent = '';
            setState('idle');
        }
    }
} catch (error) {
    if (!disposed) {
        status.textContent = 'The 3D preview could not load. Reload this page to try again.';
        console.error(error);
        dispose();
    }
}

document.getElementById('states').addEventListener('click', event => {
    const next = event.target.closest('[data-state]')?.dataset.state;
    if (!next) return;
    audioElement.pause();
    clearPulse();
    setState(next);
});
document.querySelectorAll('[data-expression]').forEach(button => {
    button.addEventListener('click', () => {
        audioElement.pause();
        clearPulse();
        const name = button.dataset.expression;
        if (name === 'smile' || name === 'neutral') {
            smileAtRest = name === 'smile' ? 1 : 0;
            for (const choice of document.querySelectorAll('[data-expression="smile"], [data-expression="neutral"]')) {
                choice.setAttribute('aria-pressed', String(choice === button));
            }
            setState('idle');
            loop.invalidate();
            return;
        }
        const duration = name === 'blink' ? 400 : name === 'speech' ? 5000 : 2000;
        pulse = { name, start: performance.now(), duration };
        loop.setState('speaking');
        pulseTimer = setTimeout(clearPulse, duration);
    });
});
document.getElementById('angle').addEventListener('change', event => {
    angle = Number(event.target.value);
    loop?.invalidate();
});
document.getElementById('clip').addEventListener('change', event => {
    const file = event.target.files[0];
    if (!file) return;
    status.textContent = '';
    audioElement.pause();
    clearPulse();
    if (audioUrl) URL.revokeObjectURL(audioUrl);
    audioUrl = URL.createObjectURL(file);
    audioElement.src = audioUrl;
    audioElement.hidden = false;
    ensureAudio();
});
audioElement.addEventListener('play', async () => {
    try {
        ensureAudio();
        await context.resume();
        if (!audioElement.paused && !disposed) {
            status.textContent = '';
            clearPulse();
            setState('speaking');
        }
    } catch {
        audioElement.pause();
        status.textContent = 'Audio playback could not start. Try another clip.';
    }
});
audioElement.addEventListener('pause', () => { if (!disposed && state === 'speaking') setState('idle'); });
audioElement.addEventListener('ended', () => { if (!disposed) setState('idle'); });
audioElement.addEventListener('error', () => {
    if (!disposed) {
        status.textContent = 'This audio clip could not be played. Choose another file.';
        setState('idle');
    }
});

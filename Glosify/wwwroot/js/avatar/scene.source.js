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
import './meshopt.js';
import { createRenderQuality } from './render-quality.js';
import { createPerformance } from './performance.js';
import { bindMorphTargets } from './morphs.js';
import { createRenderLoop, portraitFraming } from './render-loop.js';

export async function createAvatar(canvas, character = 'rain', { quality: qualityMode = 'auto' } = {}) {
    const model = {
        rain: '/models/avatar/rain-realistic.glb?v=natural-brown-eyes-1',
        rowan: '/models/avatar/rowan-realistic.glb?v=brown-hair-1',
        kwame: '/models/avatar/kwame-realistic.glb?v=tweed-1',
        mei: '/models/avatar/mei-realistic.glb?v=low-bun-1'
    }[character];
    if (typeof model !== 'string') throw new Error('Unknown conversation partner.');
    const engine = new Engine(canvas, true, { stencil: false, preserveDrawingBuffer: false, powerPreference: 'low-power' });
    const quality = createRenderQuality(qualityMode);
    function applyQuality() {
        canvas.dataset.backgroundQuality = quality.background();
        const scaling = quality.scaling(canvas.clientWidth, canvas.clientHeight, window.devicePixelRatio);
        if (Math.abs(engine.getHardwareScalingLevel() - scaling) > .001) engine.setHardwareScalingLevel(scaling);
    }
    applyQuality();
    const scene = new Scene(engine);
    scene.skipPointerMovePicking = true;
    scene.clearColor = new Color4(0, 0, 0, 0);
    scene.imageProcessingConfiguration.toneMappingEnabled = true;
    scene.imageProcessingConfiguration.toneMappingType = ImageProcessingConfiguration.TONEMAPPING_ACES;
    scene.imageProcessingConfiguration.exposure = 1.05;
    const camera = new ArcRotateCamera('portrait', -Math.PI / 2, Math.PI / 2 - .13, 1.65, new Vector3(0, 1.42, 0), scene);
    camera.minZ = .02;
    const compact = matchMedia('(max-width: 720px)');
    const frame = () => {
        const { fov, targetY } = portraitFraming(canvas.clientWidth, canvas.clientHeight, { compact: compact.matches, distance: camera.radius });
        camera.fov = fov; camera.target.y = targetY;
    };
    frame();
    const fill = new HemisphericLight('soft daylight', new Vector3(0, 1, -1), scene); fill.intensity = .8;
    fill.diffuse = new Color3(.98, 1, .97); fill.groundColor = new Color3(.3, .25, .19);
    const key = new DirectionalLight('key', new Vector3(.5, -1, 1), scene); key.intensity = 1.5; key.diffuse = new Color3(1, .89, .76);
    const rim = new DirectionalLight('rim', new Vector3(-.7, -.2, -.8), scene); rim.intensity = .9; rim.diffuse = new Color3(1, .92, .79);
    let loaded;
    try { loaded = await ImportMeshAsync(model, scene); }
    catch (error) { scene.dispose(); engine.dispose(); throw error; }
    const root = new TransformNode('breathing', scene); root.rotation.y = Math.PI;
    for (const mesh of loaded.meshes) {
        if (!mesh.parent) mesh.parent = root;
        mesh.isPickable = false;
    }
    const applyWeights = bindMorphTargets(loaded.meshes);
    const rain = createPerformance();
    let analyser = null, spectrum = null, wave = null;
    const reduced = matchMedia('(prefers-reduced-motion: reduce)');
    const loop = createRenderLoop(elapsed => {
        let audio = null;
        if (analyser && rain.state === 'speaking') {
            analyser.getFloatFrequencyData(spectrum); analyser.getFloatTimeDomainData(wave);
            audio = { spectrum, sampleRate: analyser.context.sampleRate, level: Math.sqrt(wave.reduce((sum, value) => sum + value * value, 0) / wave.length) };
        }
        const { weights, posture } = rain.update(elapsed, audio, reduced.matches);
        applyWeights(weights);
        // A slight lean towards the viewer, weight shift and breathing rise.
        root.position.set(0, posture.rise, -posture.lean);
        root.rotation.set(0, Math.PI + posture.yaw, posture.sway);
        engine.beginFrame(); scene.render(); engine.endFrame();
    }, { onFrame: sample => {
        if (quality.sample(sample)) { applyQuality(); loop.invalidate(); }
    } });
    quality.reset(performance.now());
    loop.setReducedMotion(reduced.matches);
    let inView = true;
    const visibility = () => {
        quality.reset(performance.now());
        loop.setVisible(!document.hidden && inView);
    };
    const motion = () => loop.setReducedMotion(reduced.matches);
    document.addEventListener('visibilitychange', visibility);
    reduced.addEventListener('change', motion);
    visibility();
    const intersection = new IntersectionObserver(entries => {
        inView = entries[0].isIntersecting; visibility();
    });
    intersection.observe(canvas);
    const resize = new ResizeObserver(() => {
        quality.reset(performance.now()); applyQuality();
        engine.resize(); frame(); loop.invalidate();
    });
    resize.observe(canvas);
    // Shader/texture readiness and context restoration can occur after settling.
    const ready = scene.onReadyObservable.addOnce(() => loop.invalidate());
    const restored = engine.onContextRestoredObservable.add(() => { quality.reset(performance.now()); applyQuality(); loop.invalidate(); });
    return {
        setQuality(value) { quality.setMode(value, performance.now()); applyQuality(); loop.invalidate(); },
        setState(value) {
            rain.setState(value); loop.setState(value);
        },
        setReply(text) { rain.setReply(text); },
        hear(level) { rain.hear(level); },
        setAnalyser(value) {
            analyser = value;
            if (value) { spectrum = new Float32Array(value.frequencyBinCount); wave = new Float32Array(value.fftSize); }
        },
        dispose() {
            loop.dispose(); resize.disconnect(); intersection.disconnect();
            document.removeEventListener('visibilitychange', visibility);
            reduced.removeEventListener('change', motion);
            scene.onReadyObservable.remove(ready); engine.onContextRestoredObservable.remove(restored);
            scene.dispose(); engine.dispose();
        }
    };
}

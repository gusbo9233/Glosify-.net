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
import { createPerformance } from './performance.js';

export async function createAvatar(canvas) {
    const engine = new Engine(canvas, true, { stencil: false, preserveDrawingBuffer: false, powerPreference: 'low-power' });
    engine.setHardwareScalingLevel(Math.max(1, window.devicePixelRatio / 1.5));
    const scene = new Scene(engine); scene.clearColor = new Color4(0, 0, 0, 0);
    scene.imageProcessingConfiguration.toneMappingEnabled = true;
    scene.imageProcessingConfiguration.toneMappingType = ImageProcessingConfiguration.TONEMAPPING_ACES;
    scene.imageProcessingConfiguration.exposure = 1.05;
    const camera = new ArcRotateCamera('portrait', -Math.PI / 2, Math.PI / 2 - .13, 1.65, new Vector3(0, 1.42, 0), scene);
    camera.fov = .49; camera.minZ = .02;
    const fill = new HemisphericLight('soft daylight', new Vector3(0, 1, -1), scene); fill.intensity = .8;
    fill.diffuse = new Color3(.98, 1, .97); fill.groundColor = new Color3(.3, .25, .19);
    const key = new DirectionalLight('key', new Vector3(.5, -1, 1), scene); key.intensity = 1.5; key.diffuse = new Color3(1, .89, .76);
    const rim = new DirectionalLight('rim', new Vector3(-.7, -.2, -.8), scene); rim.intensity = .9; rim.diffuse = new Color3(1, .92, .79);
    let loaded;
    try { loaded = await ImportMeshAsync('/models/avatar/rain-realistic.glb', scene); }
    catch (error) { scene.dispose(); engine.dispose(); throw error; }
    const root = new TransformNode('breathing', scene); root.rotation.y = Math.PI;
    for (const mesh of loaded.meshes) if (!mesh.parent) mesh.parent = root;
    const targets = {};
    for (const mesh of loaded.meshes) {
        const manager = mesh.morphTargetManager;
        if (!manager) continue;
        for (let i = 0; i < manager.numTargets; i++) {
            const target = manager.getTarget(i); (targets[target.name] ||= []).push(target);
        }
        // Many units cross zero every frame; a fixed influencer count avoids a shader
        // variant per active-target count. Vertex-attribute mode supports only eight.
        if (manager.isUsingTextureForTargets) manager.numMaxInfluencers = manager.numTargets;
    }
    const set = (name, value) => { for (const target of targets[name] || []) target.influence = value; };
    const rain = createPerformance();
    let analyser = null, spectrum = null, wave = null, last = performance.now();
    const reduced = matchMedia('(prefers-reduced-motion: reduce)');
    engine.runRenderLoop(() => {
        if (document.hidden) return;
        const now = performance.now();
        if (now - last < 1000 / 40) return;
        const elapsed = (now - last) / 1000; last = now;
        let audio = null;
        if (analyser && rain.state === 'speaking') {
            analyser.getFloatFrequencyData(spectrum); analyser.getFloatTimeDomainData(wave);
            audio = { spectrum, sampleRate: analyser.context.sampleRate, level: Math.sqrt(wave.reduce((sum, value) => sum + value * value, 0) / wave.length) };
        }
        const { weights, posture } = rain.update(elapsed, audio, reduced.matches);
        for (const name in weights) set(name, weights[name]);
        // A slight lean towards the viewer, weight shift and breathing rise.
        root.position.set(0, posture.rise, -posture.lean);
        root.rotation.set(0, Math.PI + posture.yaw, posture.sway);
        scene.render();
    });
    const resize = new ResizeObserver(() => engine.resize()); resize.observe(canvas);
    return {
        setState(value) { rain.setState(value); },
        setReply(text) { rain.setReply(text); },
        hear(level) { rain.hear(level); },
        setAnalyser(value) {
            analyser = value;
            if (value) { spectrum = new Float32Array(value.frequencyBinCount); wave = new Float32Array(value.fftSize); }
        },
        dispose() { resize.disconnect(); scene.dispose(); engine.dispose(); }
    };
}

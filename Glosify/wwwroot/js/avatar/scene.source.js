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
        if (manager) for (let i = 0; i < manager.numTargets; i++) {
            const target = manager.getTarget(i); (targets[target.name] ||= []).push(target);
        }
    }
    const set = (name, value) => { for (const target of targets[name] || []) target.influence = value; };
    let state = 'off', analyser = null, mouth = 0, blinkAt = 2.6, start = performance.now(), last = 0;
    const wave = new Uint8Array(512);
    const reduced = matchMedia('(prefers-reduced-motion: reduce)');
    engine.runRenderLoop(() => {
        if (document.hidden) return;
        const now = performance.now(), t = (now - start) / 1000;
        if (now - last < 1000 / 40) return;
        last = now;
        let volume = 0;
        if (analyser && state === 'speaking') {
            analyser.getByteTimeDomainData(wave);
            volume = Math.sqrt(wave.reduce((sum, value) => sum + ((value - 128) / 128) ** 2, 0) / wave.length);
        }
        mouth += (Math.min(1, volume * 7) - mouth) * .55;
        set('jawOpen', mouth); set('smile', 0);
        if (!reduced.matches) {
            if (t > blinkAt + .18) blinkAt = t + 2.8 + Math.random() * 3;
            set('blink', t >= blinkAt ? Math.sin(Math.min(1, (t - blinkAt) / .18) * Math.PI) : 0);
            set('nod', Math.sin(t * .72) * .25 + (state === 'listening' ? .28 : 0));
            set('turn', Math.sin(t * .38) * .25);
            root.position.y = Math.sin(t * 1.1) * .0018;
            root.rotation.y = Math.PI + Math.sin(t * .4) * .014;
        } else { set('blink', 0); set('nod', 0); set('turn', 0); }
        scene.render();
    });
    const resize = new ResizeObserver(() => engine.resize()); resize.observe(canvas);
    return {
        setState(value) { state = value; },
        setAnalyser(value) { analyser = value; },
        dispose() { resize.disconnect(); scene.dispose(); engine.dispose(); }
    };
}

import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { loadBrowserModule } from './browser-module.js';
import { createRenderQuality } from './build/avatar/render-quality.js';
import { createRenderLoop, portraitFraming } from './build/avatar/render-loop.js';
import { createPerformance } from './build/avatar/performance.js';

// Run the scene's actual quality/loop wiring with a canvas-clearing engine double.
// GPU/model loading is irrelevant to scheduling and is covered in browser checks.
async function sceneHarness(cost = 55) {
    let time = 0, next = 0, engine;
    const callbacks = new Map();
    const observable = { addOnce() {}, add() {}, remove() {} };
    class Engine {
        constructor() { engine = this; this.scale = 1; this.blank = false; this.resizes = 0; }
        getHardwareScalingLevel() { return this.scale; }
        setHardwareScalingLevel(value) { this.scale = value; this.blank = true; this.resizes++; }
        beginFrame() {} endFrame() {} resize() {} dispose() {}
        onContextRestoredObservable = observable;
    }
    class Scene {
        imageProcessingConfiguration = {};
        onReadyObservable = observable;
        render() { time += cost; engine.blank = false; }
        dispose() {}
    }
    class Vector { set() {} }
    class Camera { constructor(_name, _alpha, _beta, radius) { this.radius = radius; this.target = {}; } }
    class Node { position = new Vector(); rotation = new Vector(); }
    class Observer { observe() {} disconnect() {} }
    const canvas = { clientWidth: 800, clientHeight: 600, dataset: {} };
    const context = vm.createContext({ Engine, Scene, Vector3: Vector, ArcRotateCamera: Camera,
        Color3: Vector, Color4: Vector, TransformNode: Node, HemisphericLight: Node, DirectionalLight: Node,
        ImageProcessingConfiguration: {}, ImportMeshAsync: async () => ({ meshes: [] }), bindMorphTargets: () => () => {},
        createRenderQuality, createPerformance, portraitFraming,
        createRenderLoop: (render, options) => createRenderLoop(render, { ...options, now: () => time,
            request: callback => { callbacks.set(++next, callback); return next; }, cancel: id => callbacks.delete(id) }),
        window: { devicePixelRatio: 2 }, performance: { now: () => time },
        document: { hidden: false, addEventListener() {}, removeEventListener() {} },
        matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }),
        ResizeObserver: Observer, IntersectionObserver: Observer, canvas
    });
    const { createAvatar } = await loadBrowserModule(new URL('./build/avatar/scene.source.js', import.meta.url), context, {
        '@babylonjs/core/Engines/engine.js': { Engine },
        '@babylonjs/core/scene.js': { Scene },
        '@babylonjs/core/Cameras/arcRotateCamera.js': { ArcRotateCamera: Camera },
        '@babylonjs/core/Maths/math.vector.js': { Vector3: Vector },
        '@babylonjs/core/Maths/math.color.js': { Color3: Vector, Color4: Vector },
        '@babylonjs/core/Lights/hemisphericLight.js': { HemisphericLight: Node },
        '@babylonjs/core/Lights/directionalLight.js': { DirectionalLight: Node },
        '@babylonjs/core/Meshes/transformNode.js': { TransformNode: Node },
        '@babylonjs/core/Loading/sceneLoader.js': { ImportMeshAsync: context.ImportMeshAsync },
        '@babylonjs/core/Materials/imageProcessingConfiguration.js': { ImageProcessingConfiguration: {} },
        '@babylonjs/loaders/glTF': {}, './meshopt.js': {},
        './morphs.js': { bindMorphTargets: context.bindMorphTargets },
        './render-loop.js': { createRenderLoop: context.createRenderLoop, portraitFraming },
    });
    const visual = await createAvatar(canvas);
    return { visual, engine, canvas, advance(ms, stateChanges = false) {
        const end = time + ms;
        while (time < end) {
            time += 1000 / 60;
            if (stateChanges) visual.setState(['listening', 'thinking', 'speaking', 'reconnecting'][Math.floor(time / 3000) % 4]);
            const pending = [...callbacks.values()]; callbacks.clear();
            const timestamp = time;
            time += 3; // Browser callbacks run after the rAF timestamp.
            for (const callback of pending) callback(timestamp);
            assert.equal(engine.blank, false, 'quality resize must redraw in the same animation callback');
        }
    } };
}

test('automatic resolution changes redraw before yielding to the browser', async () => {
    const h = await sceneHarness();
    h.advance(20000);
    assert.ok(h.engine.resizes > 0, 'overload must actually exercise a canvas resize');
    assert.ok(h.engine.scale > 1);
    h.visual.dispose();
});

test('quality continues adapting through short conversation states and repeated reconnection updates', async () => {
    const h = await sceneHarness();
    h.advance(24000, true);
    assert.equal(h.canvas.dataset.backgroundQuality, 'reduced');
    assert.ok(h.engine.scale > 1, 'sustained overload must lower resolution despite state changes');
    h.visual.dispose();
});

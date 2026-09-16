import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { mountGlobe, resolveGlobeFocus, projectGlobePoint, renderGlobePaths } from '../Glosify/wwwroot/js/home-globe.js';

const catalog = readFileSync(new URL('../Glosify/Services/Language/QuizLanguageCatalog.cs', import.meta.url), 'utf8');

test('every language catalog flag region has a finite geographic focus', () => {
    const regions = [...catalog.matchAll(/new\("[^"\n]+"(?:, "[^"\n]*"){5}, "([A-Z]{2})"/g)].map(match => match[1]);
    assert.ok(regions.length >= 69, 'Read the language learning entries, not Freestyle');
    for (const region of regions) {
        const coordinate = resolveGlobeFocus(region);
        assert.ok(coordinate, `${region} needs a focus`);
        assert.ok(Number.isFinite(coordinate[0]) && Math.abs(coordinate[0]) <= 180);
        assert.ok(Number.isFinite(coordinate[1]) && Math.abs(coordinate[1]) <= 90);
    }
});

test('representative regions resolve to Poland, UK, Spain, and southern/eastern locations', () => {
    assert.deepEqual(resolveGlobeFocus('PL'), [19, 52]);
    assert.deepEqual(resolveGlobeFocus(' gb '), [-2, 54]);
    assert.deepEqual(resolveGlobeFocus('ES'), [-4, 40]);
    assert.deepEqual(resolveGlobeFocus('JP'), [138, 37]);
    assert.deepEqual(resolveGlobeFocus('BR'), [-52, -14]);
    assert.deepEqual(resolveGlobeFocus('NZ'), [173, -41]);
    for (const region of ['', null, undefined, 'free', 'XX', '__proto__', 'constructor']) {
        assert.equal(resolveGlobeFocus(region), null);
    }
});

test('orthographic projection centers the focus, hides the back, and keeps the marker visible through sway', () => {
    for (const region of ['PL', 'JP', 'NZ', 'BR']) {
        const focus = resolveGlobeFocus(region);
        const centered = projectGlobePoint(focus, focus);
        assert.ok(Math.abs(centered.x - 220) < 0.001);
        assert.ok(Math.abs(centered.y - 220) < 0.001);
        assert.equal(centered.visible, true);
        assert.equal(projectGlobePoint([focus[0] + 180, -focus[1]], focus).visible, false);
        for (const sway of [-7, 7]) {
            const point = projectGlobePoint(focus, [focus[0] + sway, focus[1]]);
            assert.equal(point.visible, true);
            assert.ok(Math.abs(point.x - 220) < 24);
        }
    }
    assert.deepEqual(projectGlobePoint([90, 0], [0, 0]), { x: 404, y: 220, visible: true });
});

test('continent and grid geometry changes with the geographic focus', () => {
    const poland = renderGlobePaths(resolveGlobeFocus('PL'));
    const japan = renderGlobePaths(resolveGlobeFocus('JP'));
    assert.notEqual(poland.landPath, japan.landPath);
    assert.notEqual(poland.gridPath, japan.gridPath);
    assert.ok(poland.landPath.split('M').length > 1000);
    for (const path of [...Object.values(poland), ...Object.values(japan)]) {
        assert.doesNotMatch(path, /NaN|Infinity/);
    }
});

function fixture(region = 'PL', reduced = false) {
    const doc = new EventTarget();
    doc.hidden = false;
    const motion = new EventTarget();
    motion.matches = reduced;
    const nodes = Object.fromEntries(['land', 'grid', 'marker', 'fallback'].map(name => [name, {
        style: {}, attributes: {}, setAttribute(key, value) { this.attributes[key] = value; },
    }]));
    const classes = new Set();
    const element = {
        ownerDocument: doc,
        dataset: { region },
        querySelector(selector) { return nodes[selector.match(/data-globe-(\w+)/)[1]]; },
        classList: { toggle(name, enabled) { if (enabled) classes.add(name); else classes.delete(name); } },
    };
    const frames = new Map();
    let sequence = 0, intersectionCallback;
    const environment = {
        matchMedia: () => motion,
        requestAnimationFrame(callback) { frames.set(++sequence, callback); return sequence; },
        cancelAnimationFrame(id) { frames.delete(id); },
        IntersectionObserver: class {
            constructor(callback) { intersectionCallback = callback; }
            observe() {}
            disconnect() {}
        },
    };
    const dispose = mountGlobe(element, environment);
    return {
        nodes, doc, motion, frames, classes, dispose,
        intersect(visible) { intersectionCallback([{ isIntersecting: visible }]); },
        step(time) {
            const [id, callback] = frames.entries().next().value;
            frames.delete(id);
            callback(time);
        },
    };
}

test('normal motion changes geographic paths and pauses in hidden tabs and offscreen', () => {
    const f = fixture();
    const initial = f.nodes.land.attributes.d;
    f.step(0); f.step(100);
    assert.notEqual(f.nodes.land.attributes.d, initial);
    assert.equal(f.nodes.marker.style.display, '');
    assert.equal(f.nodes.fallback.style.display, 'none');
    f.doc.hidden = true;
    f.doc.dispatchEvent(new Event('visibilitychange'));
    assert.equal(f.frames.size, 0);
    assert.equal(f.classes.has('is-paused'), true);
    f.doc.hidden = false;
    f.doc.dispatchEvent(new Event('visibilitychange'));
    assert.equal(f.frames.size, 1);
    f.intersect(false);
    assert.equal(f.frames.size, 0);
    f.intersect(true);
    assert.equal(f.frames.size, 1);
    f.dispose();
    f.doc.dispatchEvent(new Event('visibilitychange'));
    assert.equal(f.frames.size, 0);
});

test('reduced motion renders a centered stationary globe and reacts to preference changes', () => {
    const f = fixture('PL', true);
    assert.equal(f.frames.size, 0);
    assert.equal(f.nodes.marker.attributes.transform, 'translate(220.000 220.000)');
    f.motion.matches = false;
    f.motion.dispatchEvent(new Event('change'));
    assert.equal(f.frames.size, 1);
    f.step(0); f.step(1000);
    assert.notEqual(f.nodes.marker.attributes.transform, 'translate(220.000 220.000)');
    f.motion.matches = true;
    f.motion.dispatchEvent(new Event('change'));
    assert.equal(f.frames.size, 0);
    assert.equal(f.nodes.marker.attributes.transform, 'translate(220.000 220.000)');
    f.dispose();
});

test('no language, Freestyle, and unknown regions show neutral geometry without a marker', () => {
    for (const region of ['', 'free', 'unknown']) {
        const f = fixture(region, true);
        assert.equal(f.nodes.marker.style.display, 'none');
        assert.equal(f.nodes.land.attributes.d, renderGlobePaths([12, 20]).landPath);
        f.dispose();
    }
});

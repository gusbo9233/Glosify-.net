import test from 'node:test';
import assert from 'node:assert/strict';
import { bindMorphTargets } from '../Glosify/wwwroot/js/avatar/morphs.js';

function manager(texture) {
    const targets = ['jawOpen', 'mouthWide', 'mouthRound', 'lipsPart', 'mouthPress',
        'smileLeft', 'smileRight', 'browRaiseLeft', 'browRaiseRight', 'browInner',
        'lookLeft', 'blink', 'nod', 'turn', 'tilt', 'breathe'].map(name => ({ name, influence: .5, hasPositions: true, hasNormals: true }));
    return { optimizeInfluencers: false, numMaxInfluencers: 7, supportsPositions: false, supportsNormals: false, targets, numTargets: targets.length, isUsingTextureForTargets: texture, getTarget: i => targets[i] };
}

const mesh = (manager, maxVertexAttribs = 16) => ({ morphTargetManager: manager,
    getScene: () => ({ getEngine: () => ({ getCaps: () => ({ maxVertexAttribs }) }) }),
    getVerticesDataKinds: () => ['position', 'normal', 'uv', 'position0', 'normal0'] });

test('attribute fallback preserves speech, paired smile, blink and nod without slot overflow', () => {
    const low = manager(false), full = manager(true);
    const apply = bindMorphTargets([mesh(low), mesh(full)]);
    const weights = Object.fromEntries(full.targets.map(t => [t.name, .7]));
    apply(weights);
    assert.deepEqual(low.targets.filter(t => t.influence !== 0).map(t => t.name),
        ['jawOpen', 'mouthWide', 'smileLeft', 'smileRight', 'blink', 'nod']);
    assert.equal(full.targets.find(t => t.name === 'lookLeft').influence, .7);
    assert.equal(full.numMaxInfluencers, full.numTargets);
    assert.equal(full.optimizeInfluencers, true);
    assert.equal(low.numMaxInfluencers, 0);
    assert.equal(low.optimizeInfluencers, true);
    apply({ ...weights, jawOpen: 0, mouthWide: 0, mouthRound: 0, lipsPart: 0 });
    assert.equal(low.targets.find(t => t.name === 'blink').influence, .7);
    assert.equal(low.targets.find(t => t.name === 'nod').influence, .7);
    assert.equal(low.targets.find(t => t.name === 'lookLeft').influence, 0);
    apply({});
    assert.ok(low.targets.every(t => t.influence === 0));
});


test('minimal attribute budget keeps speech and blink without exceeding GPU limits', () => {
    const low = manager(false);
    const apply = bindMorphTargets([mesh(low, 8)]);
    apply(Object.fromEntries(low.targets.map(t => [t.name, .7])));
    assert.deepEqual(low.targets.filter(t => t.influence !== 0).map(t => t.name), ['jawOpen', 'blink']);
});

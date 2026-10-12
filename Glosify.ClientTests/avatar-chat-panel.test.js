import test from 'node:test';
import assert from 'node:assert/strict';
import { constrainChat, setupFloatingChat } from '../Glosify/wwwroot/js/avatar/chat-panel.js';

function fixture() {
    const element = () => ({ style: {}, handlers: {}, captured: new Set(),
        addEventListener(name, action) { this.handlers[name] = action; }, focus() {},
        setPointerCapture(id) { this.captured.add(id); }, hasPointerCapture(id) { return this.captured.has(id); },
        releasePointerCapture(id) { this.captured.delete(id); },
        fire(name, overrides = {}) { this.handlers[name]?.({ isPrimary: true, button: 0, pointerId: 1,
            clientX: 0, clientY: 0, preventDefault() {}, stopPropagation() {}, ...overrides }); },
    });
    const panel = element(), move = element(), resize = element();
    const stage = { clientWidth: 1000, clientHeight: 660 };
    let resized;
    class Observer { constructor(callback) { resized = callback; } observe() {} disconnect() {} }
    setupFloatingChat({ panel, move, resize, stage, Observer });
    const rect = () => Object.fromEntries(['left', 'top', 'width', 'height'].map(key => [key, parseFloat(panel.style[key])]));
    return { panel, move, resize, stage, rect, resized: () => resized() };
}

test('dragging uses the initial pointer offset and stops on cancellation', () => {
    const h = fixture();
    h.move.fire('pointerdown', { clientX: 700, clientY: 110 });
    h.move.fire('pointermove', { clientX: 500, clientY: 140 });
    assert.deepEqual(h.rect(), { left: 456, top: 126, width: 320, height: 390 });
    h.move.fire('pointercancel');
    h.move.fire('pointermove', { clientX: 0, clientY: 0 });
    assert.equal(h.rect().left, 456);
    assert.equal(h.move.captured.size, 0);
});

test('resize preserves the top left corner and cannot cover voice controls', () => {
    const h = fixture();
    h.resize.fire('pointerdown');
    h.resize.fire('pointermove', { clientX: 1000, clientY: 1000 });
    assert.deepEqual(h.rect(), { left: 656, top: 96, width: 332, height: 452 });
    h.resize.fire('pointermove', { clientX: -1000, clientY: -1000 });
    assert.deepEqual(h.rect(), { left: 656, top: 96, width: 240, height: 200 });
});

test('secondary input cannot take over an active gesture', () => {
    const h = fixture();
    h.move.fire('pointerdown', { isPrimary: false });
    h.move.fire('pointermove', { clientX: 50 });
    assert.equal(h.rect().left, 656);
    h.move.fire('pointerdown');
    h.resize.fire('pointerdown', { pointerId: 2 });
    h.move.fire('pointermove', { pointerId: 2, clientX: 100 });
    assert.equal(h.rect().left, 656);
    h.move.fire('pointermove', { clientX: -50 });
    assert.equal(h.rect().left, 606);
});

test('keyboard movement and sizing are usable without dragging', () => {
    const h = fixture();
    h.move.fire('keydown', { key: 'ArrowLeft', shiftKey: true });
    h.resize.fire('keydown', { key: 'ArrowDown' });
    assert.deepEqual(h.rect(), { left: 624, top: 96, width: 320, height: 402 });
});

test('changing stage width brings the entire chat back within a phone screen', () => {
    const h = fixture();
    h.stage.clientWidth = 300; h.stage.clientHeight = 540; h.resized();
    assert.deepEqual(h.rect(), { left: 44, top: 38, width: 212, height: 390 });
    h.move.fire('pointerdown');
    h.move.fire('pointermove', { clientX: -999, clientY: -999 });
    assert.equal(h.rect().left, 44); assert.equal(h.rect().top, 12);
});

test('very small containers remain bounded rather than enforcing impossible minimums', () => {
    assert.deepEqual(constrainChat({ x: 800, y: 400, width: 320, height: 390 }, { width: 200, height: 280 }),
        { x: 44, y: 12, width: 112, height: 156 });
});

test('phone drag and resize keep both edge arrows clear', () => {
    const h = fixture(); h.stage.clientWidth = 366; h.stage.clientHeight = 540; h.resized();
    h.move.fire('pointerdown'); h.move.fire('pointermove', { clientX: 999 }); h.move.fire('pointerup');
    h.resize.fire('pointerdown'); h.resize.fire('pointermove', { clientX: 999 });
    const rect = h.rect();
    assert.equal(rect.left, 44);
    assert.equal(rect.left + rect.width, 322);
    assert.equal(rect.width, 278);
});

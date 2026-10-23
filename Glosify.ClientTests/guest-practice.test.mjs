import test from 'node:test';
import assert from 'node:assert/strict';
import { createPractice, isReversePractice } from '../Glosify/wwwroot/js/guest-practice-state.js';
const cards = [{ Target: 'buenos días', Source: 'good morning' }, { Target: 'hola', Source: 'hello' }];
test('typing scores once, normalizes case and Unicode, and completes', () => {
    const state = createPractice(cards);
    state.next();
    assert.equal(state.index, 0);
    assert.equal(state.answer(' BUENOS DI\u0301AS '), true);
    assert.equal(state.answer('buenos días'), null);
    assert.equal(state.correct, 1);
    state.next();
    assert.equal(state.current.Source, 'hello');
    assert.equal(state.answer('wrong'), false);
    state.next();
    assert.equal(state.done, true);
    assert.equal(state.correct, 1);
    assert.equal(state.answer('hola'), null);
});
test('flashcard ratings and restart remain local to a practice instance', () => {
    const state = createPractice(cards);
    state.rate(false); state.next();
    state.rate(true); state.rate(true); state.next();
    assert.equal(state.done, true);
    assert.equal(state.correct, 1);
    const fresh = createPractice(cards);
    assert.equal(fresh.index, 0);
    assert.equal(fresh.correct, 0);
    state.restart();
    assert.equal(state.index, 0);
    assert.equal(state.correct, 0);
    assert.equal(state.done, false);
});

test('Polish answers preserve diacritics and normalize composed Unicode', () => {
    const state = createPractice([{ Source: 'hello', Target: 'cześć' }], 'pl');
    assert.equal(state.answer(' CZEŚĆ '.normalize('NFD')), true);
});
test('Turkish answers use the answer language casing rules', () => {
    const state = createPractice([{ Source: 'I am fine', Target: 'İyiyim' }], 'tr');
    assert.equal(state.answer('iyiyim'), true);
});
test('English learning accepts Spanish answers in reverse practice', () => {
    const state = createPractice([{ Source: 'good morning', Target: 'buenos días' }], 'es');
    assert.equal(state.answer('BUENOS DÍAS'), true);
});
test('typographic and ASCII apostrophes are the same answer', () => {
    assert.equal(createPractice([{ Source: 'please', Target: 's’il vous plaît' }], 'fr').answer("S'il vous plaît"), true);
    assert.equal(createPractice([{ Source: 'please', Target: "s'il vous plaît" }], 'fr').answer('s’il vous plaît'), true);
});
test('a repeated reverse prompt accepts each of its answers but nothing else', () => {
    const reverse = [{ Source: 'bonjour', Target: 'hello' }, { Source: 'merci', Target: 'thank you' }, { Source: 'bonjour', Target: 'good morning' }];
    const state = createPractice(reverse);
    assert.equal(state.answer('good morning'), true); state.next();
    assert.equal(state.answer('hello'), false); state.next();
    assert.equal(state.answer('hello'), true);
});
test('practice direction is read like the server binds it', () => {
    assert.equal(isReversePractice('?PracticeDirection=target-to-source'), true);
    assert.equal(isReversePractice('?practicedirection=Target-To-Source'), true);
    assert.equal(isReversePractice('?PracticeDirection=source-to-target&practiceDirection=target-to-source'), false);
    assert.equal(isReversePractice('?WordCount=5'), false);
    assert.equal(isReversePractice(''), false);
});

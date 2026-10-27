import test from 'node:test';
import assert from 'node:assert/strict';
import { createTextFormatter } from './build/shared/localization.js';
import { localReturnUrl } from './build/guest/local-return-url.js';

test('localized messages substitute repeated placeholders and retain caller fallbacks', () => {
    const text = createTextFormatter(JSON.stringify({ greeting: '{0}, {1}. {0}!', empty: '', invalid: 42 }));
    assert.equal(text('greeting', '', 'Hej', 2), 'Hej, 2. Hej!');
    assert.equal(text('empty', 'Fallback'), 'Fallback');
    assert.equal(text('invalid', 'Fallback'), 'Fallback');
    assert.equal(text('missing'), 'missing');
    assert.equal(text('constructor', 'Fallback'), 'Fallback');
    for (const malformed of ['null', '[]', '{']) assert.equal(createTextFormatter(malformed)('missing', 'Fallback'), 'Fallback');
});

test('login return URLs retain local routes and reject external or malformed destinations', () => {
    const origin = 'https://glosify.test';
    for (const value of ['/Books?sort=title#page', `${origin}/Books?sort=title`])
        assert.equal(localReturnUrl(value, origin), '/Books?sort=title');
    for (const value of ['https://other.test/Books', '//other.test/Books', 'javascript:alert(1)', null, undefined, {}, ''])
        assert.equal(localReturnUrl(value, origin), '/');
});

import test from 'node:test';
import assert from 'node:assert/strict';
import {
    formatLanguageCount,
    languageMatches,
    normalizeLanguageSearch,
} from '../Glosify/wwwroot/js/language-picker.js';

test('language search matches English, native script, aliases, and provider codes', () => {
    const arabic = 'ar ara Arabic العربية';
    const portuguese = 'pt por Portuguese (Brazil) Português (Brasil) Brazilian Portuguese';

    assert.equal(languageMatches(arabic, 'Arabic'), true);
    assert.equal(languageMatches(arabic, 'العربية'), true);
    assert.equal(languageMatches(portuguese, 'Brazilian Portuguese'), true);
    assert.equal(languageMatches(portuguese, 'por'), true);
});

test('language search is case- and diacritic-insensitive', () => {
    assert.equal(normalizeLanguageSearch('Māori'), 'maori');
    assert.equal(languageMatches('Māori Maori mri', 'MAORI'), true);
});

test('language search matches a localized display name included by the server', () => {
    assert.equal(languageMatches('Polish Polski polaco pol', 'polaco'), true);
});

test('language search reports an empty result for an unknown language', () => {
    assert.equal(languageMatches('cy cym Welsh Cymraeg', 'Klingon'), false);
});

test('result count uses the localized template supplied by Razor', () => {
    assert.equal(formatLanguageCount('{0} studiesätt och språk', 3), '3 studiesätt och språk');
    assert.equal(formatLanguageCount(
        '{0} modes and languages',
        1,
        '{0} mode or language'), '1 mode or language');
    assert.equal(formatLanguageCount('{0} 个模式和语言', 1, '{0} 个模式或语言'), '1 个模式或语言');
});

test('result count degrades to the number when a template is unavailable', () => {
    assert.equal(formatLanguageCount('', 0), '0');
});

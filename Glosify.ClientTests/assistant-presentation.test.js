import test from 'node:test';
import assert from 'node:assert/strict';
import { quizLink } from '../Glosify/wwwroot/js/assistant/presentation.js';

test('created quiz links target the details page and keep quiz names as text', () => {
    const document = { createElement: tag => ({ tag }) };
    const id = '4ad0356d-9dbc-4ed2-a97b-42bc0fe914eb';
    const link = quizLink(document, { text: id, title: '<img src=x onerror=alert(1)>' });
    assert.equal(link.tag, 'a');
    assert.equal(link.href, `/Quizzes/Details/${id}`);
    assert.equal(link.textContent, '<img src=x onerror=alert(1)>');
    assert.equal(link.innerHTML, undefined);
    assert.equal(quizLink(document, { text: 'javascript:alert(1)' }), null);
});

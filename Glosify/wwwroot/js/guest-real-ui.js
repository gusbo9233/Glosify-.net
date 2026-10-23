import { createPractice, isReversePractice } from './guest-practice-state.js';

const section = document.body.dataset.guestSection;
if (section) {
    const promptLogin = event => {
        event.preventDefault();
        event.stopImmediatePropagation();
        document.dispatchEvent(new CustomEvent('glosify:login-required'));
    };
    window.addEventListener('click', event => {
        const target = event.target;
        if (!(target instanceof Element)) return;
        if (target.closest('[data-sample-account-form] button[type="submit"], [data-sample-account-form] input[type="file"], [data-sample-account-form] label:has(input[type="file"])')) promptLogin(event);
    }, true);
    window.addEventListener('submit', event => {
        const form = event.target;
        if (form.matches('[data-sample-account-form]')) { promptLogin(event); return; }
        if (form.matches('[data-sample-settings]')) {
            event.preventDefault(); event.stopImmediatePropagation();
            const data = new FormData(form);
            const mode = data.get('Mode') === 'typing' ? 'typing' : 'flashcards';
            const query = new URLSearchParams();
            for (const key of ['WordCount', 'PracticeDirection', 'WordRangeStart', 'WordRangeEnd', 'SelectedWordIds']) query.set(key, data.get(key) || '');
            location.assign(`/preview/quiz/sample/practice/${mode}?${query}`);
        }
    }, true);
    window.addEventListener('dragstart', event => event.preventDefault(), true);

    const typing = document.querySelector('[data-typing-quiz]');
    const flash = document.querySelector('[data-flashcard-quiz], [data-anki-study]');
    if (typing || flash) initializePractice(typing, flash);
}

function initializePractice(typing, flash) {
    const query = new URLSearchParams(location.search);
    let cards = JSON.parse(document.body.dataset.guestCards);
    const selected = query.get('SelectedWordIds');
    if (selected) {
        const ids = new Set(selected.split(','));
        cards = cards.filter((_, index) => ids.has(`sample-${index}`));
    } else {
        const bound = (value, fallback) => value === null || value === '' || !Number.isFinite(Number(value)) ? fallback : Math.max(0, Math.min(100, Number(value)));
        const start = bound(query.get('WordRangeStart'), 0);
        const end = bound(query.get('WordRangeEnd'), 100);
        cards = cards.slice(Math.floor(cards.length * start / 100), Math.ceil(cards.length * end / 100));
    }
    const limit = Number(query.get('WordCount'));
    if (Number.isInteger(limit) && limit > 0) cards = cards.slice(0, limit);
    const reverse = isReversePractice(location.search);
    if (reverse) cards = cards.map(card => ({ Source: card.Target, Target: card.Source }));
    const sourceLanguage = document.body.dataset.guestSourceLanguage;
    const targetLanguage = document.body.dataset.guestTargetLanguage;
    const isFreestyle = document.body.dataset.guestLanguageCode === 'free';
    const answerLocale = reverse ? document.body.dataset.guestSourceCode : document.body.dataset.guestLanguageCode;
    const state = createPractice(cards, answerLocale === 'free' ? 'en' : answerLocale);
    const root = typing || flash;
    const prompt = root.querySelector(typing ? '[data-prompt]' : '.card-word .card-text');
    const answer = root.querySelector('.card-answer .card-text');
    const answerPanel = root.querySelector('.card-answer');
    const controls = root.querySelector('form.controls');
    const form = root.querySelector('[data-typing-form]');
    const feedback = root.querySelector('[data-feedback]');
    const check = root.querySelector('[data-check-button]');
    const input = root.querySelector('[data-answer-input]');
    const progress = root.querySelector('.progress-meta .progress-count') || document.createElement('p');
    progress.className = 'progress-count'; progress.setAttribute('aria-live', 'polite'); progress.dataset.sampleProgress = '';
    if (!progress.isConnected) root.prepend(progress);
    const reveal = document.createElement('button');
    reveal.type = 'button'; reveal.className = 'btn-main'; reveal.textContent = 'Show answer'; reveal.dataset.sampleReveal = '';
    if (flash) controls.before(reveal);
    const restart = document.createElement('button');
    restart.type = 'button'; restart.className = 'btn-linkish'; restart.textContent = 'Restart all cards'; restart.dataset.sampleRestart = '';
    // Typing and flashcard views render their own restart forms; only the Anki study view lacks one.
    if (!root.querySelector('.session-reset, [data-sample-restart-form]')) root.append(restart);
    let waiting = false;
    let revealed = false;
    function render() {
        waiting = false; revealed = false;
        progress.textContent = state.done ? `Complete · ${state.correct} of ${cards.length} correct` : `Card ${state.index + 1} of ${cards.length} · ${state.correct} correct`;
        if (prompt) prompt.textContent = state.done ? 'Practice complete' : state.current.Source;
        if (answer) answer.textContent = state.done ? '' : state.current.Target;
        if (answerPanel) answerPanel.hidden = true;
        if (controls) controls.hidden = true;
        reveal.hidden = state.done;
        root.querySelector('.flashcard')?.classList.remove('is-revealed');
        if (form) form.hidden = state.done;
        if (input) input.value = '';
        if (feedback) feedback.textContent = '';
        if (check) check.textContent = 'Check answer';
        const score = root.querySelector('.progress-meta .progress-count:last-child');
        if (score && score !== progress) score.textContent = `${state.correct} correct`;
        const bar = root.querySelector('.progress-fill');
        if (bar) bar.style.width = `${cards.length ? state.index / cards.length * 100 : 100}%`;
        const results = root.querySelector('[data-results]');
        if (results) {
            results.hidden = !state.done;
            results.classList.toggle('visible', state.done);
            root.querySelector('.typing-stage').hidden = state.done;
            root.querySelector('[data-result-score]').textContent = `${cards.length ? Math.round(state.correct / cards.length * 100) : 0}%`;
            root.querySelector('[data-result-correct]').textContent = String(state.correct);
            root.querySelector('[data-result-incorrect]').textContent = String(state.index - state.correct);
            root.querySelector('.result-stat:last-child span:last-child').textContent = String(cards.length);
        }
        const stats = root.querySelector('.anki-stats');
        if (stats) stats.textContent = `${Math.max(0, cards.length - state.index)} sample cards remaining · ${state.index} practiced`;
        const card = root.querySelector('.flashcard');
        if (card) { card.tabIndex = 0; card.classList.toggle('flashcard-clickable', !state.done); }
        root.querySelectorAll('[data-tts]').forEach(button => {
            const speaksPrompt = Boolean(button.closest('.card-word, .typing-prompt'));
            button.dataset.tts = state.done ? '' : speaksPrompt ? state.current.Source : state.current.Target;
            button.dataset.ttsLang = speaksPrompt === reverse ? targetLanguage : sourceLanguage;
            button.setAttribute('aria-label', `Play pronunciation: ${button.dataset.tts}`);
        });
        const numberLabel = root.querySelector('[data-card-label], .card-label:not(.card-label-language)');
        if (numberLabel) numberLabel.textContent = state.done ? 'Complete' : `${isFreestyle ? 'Question' : 'Word'} ${state.index + 1}`;
        const heading = root.querySelector('.flash-kicker, .card-label-language');
        if (heading) heading.textContent = isFreestyle
            ? (reverse ? 'Answer → Question' : 'Question → Answer')
            : (reverse ? `${targetLanguage} → ${sourceLanguage}` : `${sourceLanguage} → ${targetLanguage}`);
        const typingLabel = root.querySelector('.typing-prompt-label');
        if (typingLabel) typingLabel.textContent = isFreestyle
            ? (reverse ? 'Answer' : 'Question') : (reverse ? targetLanguage : sourceLanguage);
        const subtitle = root.querySelector('.flash-subtitle');
        if (subtitle && typing) subtitle.textContent = isFreestyle
            ? (reverse ? 'Type the question.' : 'Type the answer.')
            : `Type the ${reverse ? sourceLanguage : targetLanguage} translation.`;
    }
    reveal.addEventListener('click', () => {
        if (state.done) return;
        revealed = true; reveal.hidden = true; answerPanel.hidden = false; controls.hidden = false;
        root.querySelector('.flashcard')?.classList.add('is-revealed');
    });
    window.addEventListener('submit', event => {
        if (!root.contains(event.target)) return;
        if (event.target === form) {
            event.preventDefault(); event.stopImmediatePropagation();
            if (waiting) { state.next(); render(); input.focus(); return; }
            if (!input.value.trim()) return;
            const correct = state.answer(input.value);
            if (correct === null) return;
            feedback.textContent = correct ? 'Correct!' : `Answer: ${state.current.Target}`;
            waiting = true; check.textContent = 'Next';
        } else if (event.target === controls) {
            event.preventDefault(); event.stopImmediatePropagation();
            if (!revealed) return;
            state.rate(event.submitter?.value !== 'again'); state.next(); render();
        } else if (event.target.matches('.session-reset, [data-sample-restart-form]')) {
            event.preventDefault(); event.stopImmediatePropagation(); state.restart(); render();
        }
    }, true);
    root.querySelector('.flashcard')?.addEventListener('click', event => {
        if (!event.target.closest('button, a') && !revealed) reveal.click();
    });
    document.addEventListener('keydown', event => {
        if (!flash || state.done || document.querySelector('dialog[open]:not(#app-settings)') || event.target.matches('input, textarea, select, button, [contenteditable=true]')) return;
        if (!revealed && (event.key === ' ' || event.key === 'Enter')) {
            event.preventDefault(); reveal.click();
        } else if (revealed && /^[1-4]$/.test(event.key)) {
            const rating = controls.querySelector(`[data-rating-key="${event.key}"]`);
            if (rating) { event.preventDefault(); rating.click(); }
        }
    });
    restart.addEventListener('click', () => { state.restart(); render(); });
    render();
}

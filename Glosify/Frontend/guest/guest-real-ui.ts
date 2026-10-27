import { requiredElement } from '../shared/dom.js';
import { createPractice, isReversePractice } from './guest-practice-state.js';

const section = document.body.dataset.guestSection;
if (section) {
    const promptLogin = (event: Event) => {
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
        if (!(form instanceof HTMLFormElement)) return;
        if (form.matches('[data-sample-account-form]')) { promptLogin(event); return; }
        if (form.matches('[data-sample-settings]')) {
            event.preventDefault(); event.stopImmediatePropagation();
            const data = new FormData(form);
            const mode = data.get('Mode') === 'typing' ? 'typing' : 'flashcards';
            const query = new URLSearchParams();
            for (const key of ['WordCount', 'PracticeDirection', 'WordRangeStart', 'WordRangeEnd', 'SelectedWordIds']) query.set(key, String(data.get(key) || ''));
            location.assign(`/preview/quiz/sample/practice/${mode}?${query}`);
        }
    }, true);
    window.addEventListener('dragstart', event => event.preventDefault(), true);

    const typing = document.querySelector<HTMLElement>('[data-typing-quiz]');
    const flash = document.querySelector<HTMLElement>('[data-flashcard-quiz], [data-anki-study]');
    if (typing || flash) initializePractice(typing, flash);
}

function initializePractice(typing: HTMLElement | null, flash: HTMLElement | null) {
    const query = new URLSearchParams(location.search);
    let cards: { Source: string; Target: string }[] = JSON.parse(document.body.dataset.guestCards || '[]');
    const selected = query.get('SelectedWordIds');
    if (selected) {
        const ids = new Set(selected.split(','));
        cards = cards.filter((_, index) => ids.has(`sample-${index}`));
    } else {
        const bound = (value: string | null, fallback: number) => value === null || value === '' || !Number.isFinite(Number(value)) ? fallback : Math.max(0, Math.min(100, Number(value)));
        const start = bound(query.get('WordRangeStart'), 0);
        const end = bound(query.get('WordRangeEnd'), 100);
        cards = cards.slice(Math.floor(cards.length * start / 100), Math.ceil(cards.length * end / 100));
    }
    const limit = Number(query.get('WordCount'));
    if (Number.isInteger(limit) && limit > 0) cards = cards.slice(0, limit);
    const reverse = isReversePractice(location.search);
    if (reverse) cards = cards.map(card => ({ Source: card.Target, Target: card.Source }));
    const sourceLanguage = document.body.dataset.guestSourceLanguage || '';
    const targetLanguage = document.body.dataset.guestTargetLanguage || '';
    const isFreestyle = document.body.dataset.guestLanguageCode === 'free';
    const answerLocale = reverse ? document.body.dataset.guestSourceCode : document.body.dataset.guestLanguageCode;
    const state = createPractice(cards, answerLocale === 'free' ? 'en' : answerLocale);
    const root = typing || flash;
    if (!root) return;
    const prompt = root.querySelector<HTMLElement>(typing ? '[data-prompt]' : '.card-word .card-text');
    const answer = root.querySelector<HTMLElement>('.card-answer .card-text');
    const answerPanel = root.querySelector<HTMLElement>('.card-answer');
    const controls = root.querySelector<HTMLFormElement>('form.controls');
    const form = root.querySelector<HTMLFormElement>('[data-typing-form]');
    const feedback = root.querySelector<HTMLElement>('[data-feedback]');
    const check = root.querySelector<HTMLElement>('[data-check-button]');
    const input = root.querySelector<HTMLInputElement>('[data-answer-input]');
    const progress = root.querySelector<HTMLElement>('.progress-meta .progress-count') || document.createElement('p');
    progress.className = 'progress-count'; progress.setAttribute('aria-live', 'polite'); progress.dataset.sampleProgress = '';
    if (!progress.isConnected) root.prepend(progress);
    const reveal = document.createElement('button');
    reveal.type = 'button'; reveal.className = 'btn-main'; reveal.textContent = 'Show answer'; reveal.dataset.sampleReveal = '';
    if (flash) controls?.before(reveal);
    const restart = document.createElement('button');
    restart.type = 'button'; restart.className = 'btn-linkish'; restart.textContent = 'Restart all cards'; restart.dataset.sampleRestart = '';
    // Typing and flashcard views render their own restart forms; only the Anki study view lacks one.
    if (!root.querySelector<HTMLElement>('.session-reset, [data-sample-restart-form]')) root.append(restart);
    let waiting = false;
    let revealed = false;
    const render = () => {
        waiting = false; revealed = false;
        progress.textContent = state.done ? `Complete · ${state.correct} of ${cards.length} correct` : `Card ${state.index + 1} of ${cards.length} · ${state.correct} correct`;
        if (prompt) prompt.textContent = state.done ? 'Practice complete' : state.current.Source;
        if (answer) answer.textContent = state.done ? '' : state.current.Target;
        if (answerPanel) answerPanel.hidden = true;
        if (controls) controls.hidden = true;
        reveal.hidden = state.done;
        root.querySelector<HTMLElement>('.flashcard')?.classList.remove('is-revealed');
        if (form) form.hidden = state.done;
        if (input) input.value = '';
        if (feedback) feedback.textContent = '';
        if (check) check.textContent = 'Check answer';
        const score = root.querySelector<HTMLElement>('.progress-meta .progress-count:last-child');
        if (score && score !== progress) score.textContent = `${state.correct} correct`;
        const bar = root.querySelector<HTMLElement>('.progress-fill');
        if (bar) bar.style.width = `${cards.length ? state.index / cards.length * 100 : 100}%`;
        const results = root.querySelector<HTMLElement>('[data-results]');
        if (results) {
            results.hidden = !state.done;
            results.classList.toggle('visible', state.done);
            requiredElement(root, '.typing-stage').hidden = state.done;
            requiredElement(root, '[data-result-score]').textContent = `${cards.length ? Math.round(state.correct / cards.length * 100) : 0}%`;
            requiredElement(root, '[data-result-correct]').textContent = String(state.correct);
            requiredElement(root, '[data-result-incorrect]').textContent = String(state.index - state.correct);
            requiredElement(root, '.result-stat:last-child span:last-child').textContent = String(cards.length);
        }
        const stats = root.querySelector<HTMLElement>('.anki-stats');
        if (stats) stats.textContent = `${Math.max(0, cards.length - state.index)} sample cards remaining · ${state.index} practiced`;
        const card = root.querySelector<HTMLElement>('.flashcard');
        if (card) { card.tabIndex = 0; card.classList.toggle('flashcard-clickable', !state.done); }
        root.querySelectorAll<HTMLElement>('[data-tts]').forEach(button => {
            const speaksPrompt = Boolean(button.closest('.card-word, .typing-prompt'));
            button.dataset.tts = state.done ? '' : speaksPrompt ? state.current.Source : state.current.Target;
            button.dataset.ttsLang = speaksPrompt === reverse ? targetLanguage : sourceLanguage;
            button.setAttribute('aria-label', `Play pronunciation: ${button.dataset.tts}`);
        });
        const numberLabel = root.querySelector<HTMLElement>('[data-card-label], .card-label:not(.card-label-language)');
        if (numberLabel) numberLabel.textContent = state.done ? 'Complete' : `${isFreestyle ? 'Question' : 'Word'} ${state.index + 1}`;
        const heading = root.querySelector<HTMLElement>('.flash-kicker, .card-label-language');
        if (heading) heading.textContent = isFreestyle
            ? (reverse ? 'Answer → Question' : 'Question → Answer')
            : (reverse ? `${targetLanguage} → ${sourceLanguage}` : `${sourceLanguage} → ${targetLanguage}`);
        const typingLabel = root.querySelector<HTMLElement>('.typing-prompt-label');
        if (typingLabel) typingLabel.textContent = isFreestyle
            ? (reverse ? 'Answer' : 'Question') : (reverse ? targetLanguage : sourceLanguage);
        const subtitle = root.querySelector<HTMLElement>('.flash-subtitle');
        if (subtitle && typing) subtitle.textContent = isFreestyle
            ? (reverse ? 'Type the question.' : 'Type the answer.')
            : `Type the ${reverse ? sourceLanguage : targetLanguage} translation.`;
    };
    reveal.addEventListener('click', () => {
        if (state.done || !answerPanel || !controls) return;
        revealed = true; reveal.hidden = true; answerPanel.hidden = false; controls.hidden = false;
        root.querySelector<HTMLElement>('.flashcard')?.classList.add('is-revealed');
    });
    window.addEventListener('submit', event => {
        const target = event.target as Element | null;
        if (!target || !root.contains(target)) return;
        if (event.target === form && input && feedback && check) {
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
            state.rate((event.submitter as HTMLButtonElement | null)?.value !== 'again'); state.next(); render();
        } else if (target.matches('.session-reset, [data-sample-restart-form]')) {
            event.preventDefault(); event.stopImmediatePropagation(); state.restart(); render();
        }
    }, true);
    root.querySelector<HTMLElement>('.flashcard')?.addEventListener('click', event => {
        if (!(event.target as Element | null)?.closest('button, a') && !revealed) reveal.click();
    });
    document.addEventListener('keydown', event => {
        if (!flash || state.done || document.querySelector<HTMLElement>('dialog[open]:not(#app-settings)') || (event.target as Element | null)?.matches('input, textarea, select, button, [contenteditable=true]')) return;
        if (!revealed && (event.key === ' ' || event.key === 'Enter')) {
            event.preventDefault(); reveal.click();
        } else if (revealed && /^[1-4]$/.test(event.key)) {
            const rating = controls?.querySelector<HTMLElement>(`[data-rating-key="${event.key}"]`);
            if (rating) { event.preventDefault(); rating.click(); }
        }
    });
    restart.addEventListener('click', () => { state.restart(); render(); });
    render();
}

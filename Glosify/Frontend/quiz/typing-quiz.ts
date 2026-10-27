import { text as t } from '../shared/localization.js';
import { requiredElement } from '../shared/dom.js';
import { attachTypingKeyboard } from './typing-keyboard.js';

interface TypingWord { id: string; prompt: string; }
interface TypingAnswerResult {
    currentIndex: number; correctCount: number; incorrectCount: number; totalWords: number;
    nextWord: TypingWord | null; isComplete: boolean; isCorrect: boolean; correctAnswer: string;
    exampleSentence?: string | null; exampleTranslation?: string | null;
}

(() => {
    const shell = document.querySelector<HTMLElement>('[data-typing-quiz]');
    if (!shell) return;

    const state = {
        index: Number(shell.dataset.initialIndex) || 0,
        correct: Number(shell.dataset.correctCount) || 0,
        incorrect: Number(shell.dataset.incorrectCount) || 0,
        total: Number(shell.dataset.totalWords) || 0,
        checked: false,
        submitting: false,
        sessionExpired: false,
        nextWord: null as TypingWord | null,
        isComplete: shell.dataset.isComplete === 'true',
        itemPluralLabel: shell.dataset.itemPluralLabel || 'words',
        cardLabel: shell.dataset.cardLabel || 'Word'
    };

    if (state.total === 0) return;

    const form = requiredElement<HTMLFormElement>(shell, '[data-typing-form]');
    const input = requiredElement<HTMLInputElement>(shell, '[data-answer-input]');
    const prompt = requiredElement(shell, '[data-prompt]');
    const promptTts = shell.querySelector('.typing-prompt [data-tts]');
    const feedback = requiredElement(shell, '[data-feedback]');
    const progressCount = requiredElement(shell, '[data-progress-count]');
    const accuracy = requiredElement(shell, '[data-accuracy]');
    const progressFill = requiredElement(shell, '[data-progress-fill]');
    const checkButton = requiredElement<HTMLButtonElement>(shell, '[data-check-button]');
    const cardLabel = requiredElement(shell, '[data-card-label]');
    const reveal = requiredElement(shell, '[data-reveal]');
    const correctAnswer = requiredElement(shell, '[data-correct-answer]');
    const example = requiredElement(shell, '[data-example]');
    const card = requiredElement(shell, '[data-card]');
    const results = requiredElement(shell, '[data-results]');
    const practiceIncorrect = shell.querySelector<HTMLElement>('[data-practice-incorrect]');
    const token = form.querySelector<HTMLInputElement>('input[name="__RequestVerificationToken"]')?.value || '';

    const setSubmitting = (submitting: boolean) => {
        state.submitting = submitting;
        form?.setAttribute('aria-busy', submitting ? 'true' : 'false');
        checkButton.disabled = submitting || state.sessionExpired;

        if (submitting) {
            input.disabled = true;
        } else if (!state.checked && !state.sessionExpired) {
            input.disabled = false;
            input.focus();
        }
    };

    const updateProgress = () => {
        if (!state.total) return;

        const completed = state.correct + state.incorrect;
        progressCount.textContent = t('Quiz.Progress', '{0} of {1} {2}', Math.min(state.index + 1, state.total), state.total, state.itemPluralLabel);
        accuracy.textContent = t('Quiz.CorrectCount', '{0} correct', state.correct);
        progressFill.style.width = `${Math.round(completed * 100 / state.total)}%`;
    };

    const renderWord = (word: TypingWord) => {
        state.checked = false;
        input.value = '';
        input.disabled = false;
        input.focus();
        prompt.textContent = word.prompt;
        if (promptTts) {
            promptTts.setAttribute('data-tts', word.prompt || '');
            promptTts.setAttribute('aria-label', `Play pronunciation of ${word.prompt || ''}`);
        }
        feedback.textContent = '';
        feedback.className = 'typing-feedback';
        cardLabel.textContent = `${state.cardLabel} ${state.index + 1}`;
        checkButton.innerHTML = `${t('Quiz.CheckAnswer', 'Check Answer')} <span class="material-symbols-outlined">arrow_forward</span>`;
        reveal.hidden = true;
        card.classList.remove('is-correct', 'is-incorrect');
        updateProgress();
    };

    const showResults = () => {
        card.hidden = true;
        shell.querySelector('[data-ukrainian-keyboard]')?.setAttribute('hidden', '');
        results.hidden = false;
        const score = Math.round(state.correct * 100 / state.total);
        requiredElement(shell, '[data-result-score]').textContent = `${score}%`;
        requiredElement(shell, '[data-result-correct]').textContent = String(state.correct);
        requiredElement(shell, '[data-result-incorrect]').textContent = String(state.incorrect);
        progressFill.style.width = '100%';
        if (practiceIncorrect) {
            practiceIncorrect.hidden = state.incorrect === 0;
        }
    };

    const checkAnswer = async () => {
        const response = await fetch(shell.dataset.submitUrl!, {
            method: 'post',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': token
            },
            body: JSON.stringify({
                sessionId: shell.dataset.sessionId,
                userAnswer: input.value
            })
        });

        if (!response.ok) {
            const problem = await response.json().catch(() => null);
            const expired = problem?.code === 'unauthorized'
                || problem?.code === 'gone'
                || response.status === 401
                || response.status === 410;
            state.sessionExpired = expired;
            feedback.textContent = expired
                ? t('Client.SessionExpired', 'Session expired. Restart the quiz to continue.')
                : t('Client.CheckFailed', 'Could not check your answer. Please try again.');
            feedback.className = 'typing-feedback is-incorrect';
            input.disabled = expired;
            return;
        }

        const result: TypingAnswerResult = await response.json();
        state.checked = true;
        state.index = result.currentIndex;
        state.correct = result.correctCount;
        state.incorrect = result.incorrectCount;
        state.total = result.totalWords;
        state.nextWord = result.nextWord;
        state.isComplete = result.isComplete;
        input.disabled = true;

        if (result.isCorrect) {
            feedback.textContent = t('Client.Correct', 'Correct');
            feedback.classList.add('is-correct');
            card.classList.add('is-correct');
        } else {
            feedback.textContent = t('Client.NotQuite', 'Not quite');
            feedback.classList.add('is-incorrect');
            card.classList.add('is-incorrect');
        }

        correctAnswer.textContent = result.correctAnswer;
        const exampleText = [result.exampleSentence, result.exampleTranslation].filter(Boolean).join(' ');
        example.textContent = exampleText;
        example.hidden = !exampleText;
        reveal.hidden = false;
        checkButton.innerHTML = state.isComplete
            ? `${t('Client.ShowResults', 'Show Results')} <span class="material-symbols-outlined">flag</span>`
            : `${t('Client.NextWord', 'Next Word')} <span class="material-symbols-outlined">arrow_forward</span>`;
        updateProgress();
    };

    const nextWord = () => {
        if (state.isComplete || !state.nextWord) {
            showResults();
            return;
        }

        renderWord(state.nextWord);
    };

    attachTypingKeyboard(shell, input);

    form?.addEventListener('submit', async event => {
        event.preventDefault();
        if (state.submitting) {
            return;
        }

        if (state.checked) {
            // Keep the same submit control disabled through the browser's next paint.
            // Otherwise the second click of a double-click lands on the newly rendered
            // word and submits its still-empty answer.
            setSubmitting(true);
            nextWord();
            window.requestAnimationFrame(() => setSubmitting(false));
            return;
        }

        setSubmitting(true);
        try {
            await checkAnswer();
        } catch {
            if (!state.sessionExpired) {
                feedback.textContent = t('Client.CheckFailed', 'Could not check that answer. Try again.');
                feedback.className = 'typing-feedback is-incorrect';
            }
        } finally {
            setSubmitting(false);
        }
    });

    if (state.isComplete) {
        showResults();
    } else {
        input?.focus();
    }
})();

(() => {
    const shell = document.querySelector('[data-typing-quiz]');
    if (!shell) return;

    const state = {
        index: Number(shell.dataset.initialIndex) || 0,
        correct: Number(shell.dataset.correctCount) || 0,
        incorrect: Number(shell.dataset.incorrectCount) || 0,
        total: Number(shell.dataset.totalWords) || 0,
        checked: false,
        nextWord: null,
        isComplete: shell.dataset.isComplete === 'true',
        itemPluralLabel: shell.dataset.itemPluralLabel || 'words',
        cardLabel: shell.dataset.cardLabel || 'Word'
    };

    const form = shell.querySelector('[data-typing-form]');
    const input = shell.querySelector('[data-answer-input]');
    const prompt = shell.querySelector('[data-prompt]');
    const feedback = shell.querySelector('[data-feedback]');
    const progressCount = shell.querySelector('[data-progress-count]');
    const accuracy = shell.querySelector('[data-accuracy]');
    const progressFill = shell.querySelector('[data-progress-fill]');
    const checkButton = shell.querySelector('[data-check-button]');
    const cardLabel = shell.querySelector('[data-card-label]');
    const reveal = shell.querySelector('[data-reveal]');
    const correctAnswer = shell.querySelector('[data-correct-answer]');
    const example = shell.querySelector('[data-example]');
    const card = shell.querySelector('[data-card]');
    const results = shell.querySelector('[data-results]');
    const practiceIncorrect = shell.querySelector('[data-practice-incorrect]');
    const token = form?.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

    const updateProgress = () => {
        if (!state.total) return;

        const completed = state.correct + state.incorrect;
        progressCount.textContent = `${Math.min(state.index + 1, state.total)} of ${state.total} ${state.itemPluralLabel}`;
        accuracy.textContent = `${state.correct} correct`;
        progressFill.style.width = `${Math.round(completed * 100 / state.total)}%`;
    };

    const renderWord = word => {
        state.checked = false;
        input.value = '';
        input.disabled = false;
        input.focus();
        prompt.textContent = word.prompt;
        feedback.textContent = '';
        feedback.className = 'typing-feedback';
        cardLabel.textContent = `${state.cardLabel} ${state.index + 1}`;
        checkButton.innerHTML = 'Check Answer <span class="material-symbols-outlined">arrow_forward</span>';
        reveal.hidden = true;
        card.classList.remove('is-correct', 'is-incorrect');
        updateProgress();
    };

    const showResults = () => {
        card.hidden = true;
        shell.querySelector('[data-ukrainian-keyboard]')?.setAttribute('hidden', '');
        results.hidden = false;
        const score = Math.round(state.correct * 100 / state.total);
        shell.querySelector('[data-result-score]').textContent = `${score}%`;
        shell.querySelector('[data-result-correct]').textContent = state.correct;
        shell.querySelector('[data-result-incorrect]').textContent = state.incorrect;
        progressFill.style.width = '100%';
        if (practiceIncorrect) {
            practiceIncorrect.hidden = state.incorrect === 0;
        }
    };

    const checkAnswer = async () => {
        const response = await fetch(shell.dataset.submitUrl, {
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
            feedback.textContent = 'Session expired. Restart the quiz to continue.';
            feedback.className = 'typing-feedback is-incorrect';
            input.disabled = true;
            return;
        }

        const result = await response.json();
        state.checked = true;
        state.index = result.currentIndex;
        state.correct = result.correctCount;
        state.incorrect = result.incorrectCount;
        state.total = result.totalWords;
        state.nextWord = result.nextWord;
        state.isComplete = result.isComplete;
        input.disabled = true;

        if (result.isCorrect) {
            feedback.textContent = 'Correct';
            feedback.classList.add('is-correct');
            card.classList.add('is-correct');
        } else {
            feedback.textContent = 'Not quite';
            feedback.classList.add('is-incorrect');
            card.classList.add('is-incorrect');
        }

        correctAnswer.textContent = result.correctAnswer;
        const exampleText = [result.exampleSentence, result.exampleTranslation].filter(Boolean).join(' ');
        example.textContent = exampleText;
        example.hidden = !exampleText;
        reveal.hidden = false;
        checkButton.innerHTML = state.isComplete
            ? 'Show Results <span class="material-symbols-outlined">flag</span>'
            : 'Next Word <span class="material-symbols-outlined">arrow_forward</span>';
        updateProgress();
    };

    const nextWord = () => {
        if (state.isComplete || !state.nextWord) {
            showResults();
            return;
        }

        renderWord(state.nextWord);
    };

    if (shell.dataset.showUkrainianKeyboard === 'true') {
        const qwertyToUkrainian = {
            'q':'й','w':'ц','e':'у','r':'к','t':'е','y':'н','u':'г','i':'ш','o':'щ','p':'з','[':'х',']':'ї',
            'a':'ф','s':'і','d':'в','f':'а','g':'п','h':'р','j':'о','k':'л','l':'д',';':'ж',"'":'є',
            '\\':'ґ','z':'я','x':'ч','c':'с','v':'м','b':'и','n':'т','m':'ь',',':'б','.':'ю','/':"'"
        };

        input.addEventListener('keydown', event => {
            if (event.ctrlKey || event.metaKey || event.altKey) return;
            if (event.key.length !== 1) return;

            const lower = event.key.toLowerCase();
            const mapped = qwertyToUkrainian[lower];
            if (!mapped) return;

            event.preventDefault();
            const value = event.shiftKey ? mapped.toLocaleUpperCase('uk-UA') : mapped;
            const start = input.selectionStart ?? input.value.length;
            const end = input.selectionEnd ?? input.value.length;
            input.value = input.value.slice(0, start) + value + input.value.slice(end);
            const cursor = start + value.length;
            input.setSelectionRange(cursor, cursor);
        });
    }

    form?.addEventListener('submit', event => {
        event.preventDefault();
        if (state.checked) {
            nextWord();
        } else {
            checkAnswer().catch(() => {
                feedback.textContent = 'Could not check that answer. Try again.';
                feedback.className = 'typing-feedback is-incorrect';
            });
        }
    });

    shell.querySelector('[data-ukrainian-keyboard]')?.addEventListener('click', event => {
        const key = event.target.closest('[data-key-value], [data-key-action]');
        if (!key || input.disabled) return;

        input.focus();
        const start = input.selectionStart ?? input.value.length;
        const end = input.selectionEnd ?? input.value.length;

        if (key.dataset.keyAction === 'backspace') {
            if (start === end && start > 0) {
                input.value = input.value.slice(0, start - 1) + input.value.slice(end);
                input.setSelectionRange(start - 1, start - 1);
            } else {
                input.value = input.value.slice(0, start) + input.value.slice(end);
                input.setSelectionRange(start, start);
            }
            return;
        }

        const value = key.dataset.keyValue || '';
        input.value = input.value.slice(0, start) + value + input.value.slice(end);
        const cursor = start + value.length;
        input.setSelectionRange(cursor, cursor);
    });

    if (state.isComplete) {
        showResults();
    } else {
        input?.focus();
    }
})();

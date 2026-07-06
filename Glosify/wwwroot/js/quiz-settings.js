(() => {
    const modeLabel = document.querySelector('[data-selected-mode]');
    const modes = document.querySelectorAll('input[name="Mode"]');
    const directionLabel = document.querySelector('[data-selected-direction]');
    const directions = document.querySelectorAll('input[name="PracticeDirection"]');
    const itemTypes = document.querySelectorAll('input[name="PracticeItemType"]');
    const contentLabel = document.querySelector('[data-selected-content]');
    const availableLabel = document.querySelector('[data-selected-available]');
    const availableCount = document.querySelector('[data-available-count]');
    const availableItemLabel = document.querySelector('[data-available-label]');
    const emptyCallout = document.querySelector('[data-empty-callout]');
    const startButton = document.querySelector('[data-start-button]');
    const lengthOptions = document.querySelectorAll('input[name="WordCount"]');
    if (!modeLabel || !modes.length) return;

    const labels = {
        flashcards: 'Flashcards',
        typing: 'Typing',
        'multiple-choice': 'Choices'
    };

    modes.forEach(mode => {
        mode.addEventListener('change', () => {
            if (mode.checked) {
                modeLabel.textContent = labels[mode.value] || mode.value;
            }
        });
    });

    directions.forEach(direction => {
        direction.addEventListener('change', () => {
            if (direction.checked && directionLabel) {
                directionLabel.textContent = direction.dataset.directionLabel || direction.value;
            }
        });
    });

    const refreshContent = () => {
        const selected = document.querySelector('input[name="PracticeItemType"]:checked');
        if (!selected) return;

        const isSentences = selected.value === 'sentences';
        const count = Number(selected.dataset.count || '0');
        const itemLabel = isSentences ? 'sentences' : 'words';
        const singularLabel = isSentences ? 'sentence' : 'word';

        if (contentLabel) contentLabel.textContent = selected.dataset.itemLabel || selected.value;
        if (availableLabel) availableLabel.textContent = `${count} ${itemLabel}`;
        if (availableCount) availableCount.textContent = `${count}`;
        if (availableItemLabel) availableItemLabel.textContent = itemLabel;

        lengthOptions.forEach(option => {
            const value = isSentences ? option.dataset.sentenceValue : option.dataset.wordValue;
            if (!value) return;
            option.value = value;
            option.closest('.choice')?.querySelector('[data-length-num]')?.replaceChildren(document.createTextNode(value));
        });

        if (emptyCallout) {
            emptyCallout.hidden = count > 0;
            emptyCallout.textContent = `Add at least one ${singularLabel} before starting a practice session.`;
        }

        if (startButton) {
            startButton.disabled = count === 0;
        }
    };

    itemTypes.forEach(itemType => {
        itemType.addEventListener('change', refreshContent);
    });
    refreshContent();
})();

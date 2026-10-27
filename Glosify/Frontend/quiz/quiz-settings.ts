import { text as t } from '../shared/localization.js';
import { initializePracticeRange } from './practice-range.js';
import { initializeWordPicker } from './word-picker.js';

(() => {
    const initialize = () => {
        document.querySelectorAll<HTMLInputElement>('[data-browser-timezone]').forEach(input => {
            input.value = Intl.DateTimeFormat().resolvedOptions().timeZone || input.value || 'UTC';
        });
        const modeLabel = document.querySelector<HTMLElement>('[data-selected-mode]');
        const modes = document.querySelectorAll<HTMLInputElement>('input[name="Mode"]');
        const directionLabel = document.querySelector<HTMLElement>('[data-selected-direction]');
        const directions = document.querySelectorAll<HTMLInputElement>('input[name="PracticeDirection"]');
        const itemTypes = document.querySelectorAll<HTMLInputElement>('input[name="PracticeItemType"]');
        const contentLabel = document.querySelector<HTMLElement>('[data-selected-content]');
        const availableLabel = document.querySelector<HTMLElement>('[data-selected-available]');
        const availableCount = document.querySelector<HTMLElement>('[data-available-count]');
        const availableItemLabel = document.querySelector<HTMLElement>('[data-available-label]');
        const emptyCallout = document.querySelector<HTMLElement>('[data-empty-callout]');
        const startButton = document.querySelector<HTMLButtonElement>('[data-start-button]');
        const lengthOptions = document.querySelectorAll<HTMLInputElement>('input[name="WordCount"]');
        if (!modeLabel || !modes.length) return;

        const labels: Record<string, string> = {
            flashcards: t('Settings.Flashcards', 'Flashcards'),
            typing: t('Settings.Typing', 'Typing'),
            'multiple-choice': t('Settings.Choices', 'Choices')
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

        const allLengthOption = document.querySelector<HTMLInputElement>('input[name="WordCount"][data-length-all]');

        const getRangeItemCount = (total: number) => {
            const minEl = document.querySelector<HTMLInputElement>('[data-range-min]');
            const maxEl = document.querySelector<HTMLInputElement>('[data-range-max]');
            if (!minEl || !maxEl) return total;
            const minVal = Number(minEl.value);
            const maxVal = Number(maxEl.value);
            // Match PracticeRange.Slice: round each endpoint, not just the width.
            return Math.max(1, Math.ceil(total * maxVal / 100) - Math.floor(total * minVal / 100));
        };

        const refreshAllLength = () => {
            if (!allLengthOption) return;
            const selected = document.querySelector<HTMLInputElement>('input[name="PracticeItemType"]:checked');
            const isSentences = selected?.value === 'sentences';
            const total = Number((isSentences ? allLengthOption.dataset.sentenceTotal : allLengthOption.dataset.wordTotal) || '0');
            const rangeCount = getRangeItemCount(total);
            const maximum = Number(allLengthOption.dataset.maxItems);
            const newValue = Math.max(1, Math.min(maximum, rangeCount));
            if (isSentences) {
                allLengthOption.dataset.sentenceValue = String(newValue);
            } else {
                allLengthOption.dataset.wordValue = String(newValue);
            }
            allLengthOption.value = String(newValue);
            allLengthOption.closest('.choice')?.querySelector<HTMLElement>('[data-length-num]')?.replaceChildren(document.createTextNode(String(newValue)));
            const label = allLengthOption.closest('.choice')?.querySelector<HTMLElement>('[data-length-label]');
            if (label) label.textContent = rangeCount > maximum
                ? t('Settings.Maximum', 'Maximum')
                : t('Common.All', 'All');
        };

        const refreshContent = () => {
            const selected = document.querySelector<HTMLInputElement>('input[name="PracticeItemType"]:checked');
            if (!selected) return;

            const isSentences = selected.value === 'sentences';
            const count = Number(selected.dataset.count || '0');
            const itemLabel = isSentences ? t('Settings.SentencesLower', 'sentences') : t('Settings.WordsLower', 'words');
            const singularLabel = isSentences ? t('Settings.SentenceLower', 'sentence') : t('Settings.WordLower', 'word');
            const countLabel = count === 1 ? singularLabel : itemLabel;

            if (contentLabel) contentLabel.textContent = selected.dataset.itemLabel || selected.value;
            if (availableLabel) availableLabel.textContent = `${count} ${countLabel}`;
            if (availableCount) availableCount.textContent = `${count}`;
            if (availableItemLabel) availableItemLabel.textContent = itemLabel;

            refreshAllLength();

            lengthOptions.forEach(option => {
                const value = isSentences ? option.dataset.sentenceValue : option.dataset.wordValue;
                if (!value) return;
                option.value = value;
                option.closest('.choice')?.querySelector<HTMLElement>('[data-length-num]')?.replaceChildren(document.createTextNode(value));
            });

            if (emptyCallout) {
                emptyCallout.hidden = count > 0;
                emptyCallout.textContent = t('Settings.NeedItem', 'Add at least one {0} before starting a practice session.', singularLabel);
            }

            if (startButton) {
                startButton.disabled = count === 0;
            }
        };

        itemTypes.forEach(itemType => {
            itemType.addEventListener('change', refreshContent);
        });
        refreshContent();

        initializePracticeRange(refreshAllLength);
        initializeWordPicker(itemTypes);
    };
    initialize();
    window.addEventListener('learning:updated', initialize);
})();

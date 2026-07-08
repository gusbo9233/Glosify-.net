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

    const rangeSlider = document.querySelector('[data-range-slider]');
    if (rangeSlider) {
        const minInput = rangeSlider.querySelector('[data-range-min]');
        const maxInput = rangeSlider.querySelector('[data-range-max]');
        const fill = rangeSlider.querySelector('[data-range-fill]');
        const rangeLabel = document.querySelector('[data-range-label]');
        const selectedRange = document.querySelector('[data-selected-range]');
        const presets = document.querySelectorAll('[data-range-preset]');
        const minGap = 5;

        const describeRange = (minVal, maxVal) => {
            const width = maxVal - minVal;
            if (minVal === 0 && maxVal === 100) return 'All';
            if (maxVal === 100) return `Newest ${width}%`;
            if (minVal === 0) return `Oldest ${width}%`;
            return `${minVal}%–${maxVal}%`;
        };

        const render = () => {
            const minVal = Number(minInput.value);
            const maxVal = Number(maxInput.value);

            if (fill) {
                fill.style.left = `${minVal}%`;
                fill.style.width = `${maxVal - minVal}%`;
            }

            if (rangeLabel) {
                rangeLabel.textContent = minVal === 0 && maxVal === 100
                    ? 'Practicing all words, oldest to newest.'
                    : `Practicing ${describeRange(minVal, maxVal).toLowerCase()} of the list (oldest to newest).`;
            }

            if (selectedRange) {
                selectedRange.textContent = describeRange(minVal, maxVal);
            }
        };

        const clamp = changed => {
            let minVal = Number(minInput.value);
            let maxVal = Number(maxInput.value);

            if (minVal > maxVal - minGap) {
                if (changed === 'max') {
                    minVal = Math.max(0, maxVal - minGap);
                    minInput.value = String(minVal);
                } else {
                    maxVal = Math.min(100, minVal + minGap);
                    maxInput.value = String(maxVal);
                }
            }

            render();
        };

        minInput.addEventListener('input', () => clamp('min'));
        maxInput.addEventListener('input', () => clamp('max'));

        presets.forEach(preset => {
            preset.addEventListener('click', () => {
                const [start, end] = preset.dataset.rangePreset.split(',').map(Number);
                minInput.value = String(start);
                maxInput.value = String(end);
                render();
            });
        });

        render();
    }

    const wordPickerDialog = document.querySelector('[data-word-picker-dialog]');
    if (wordPickerDialog) {
        const openBtn = document.querySelector('[data-open-word-picker]');
        const closeBtn = wordPickerDialog.querySelector('[data-close-word-picker]');
        const applyBtn = wordPickerDialog.querySelector('[data-apply-word-picker]');
        const selectAllBtn = wordPickerDialog.querySelector('[data-select-all]');
        const deselectAllBtn = wordPickerDialog.querySelector('[data-deselect-all]');
        const searchInput = wordPickerDialog.querySelector('[data-word-picker-search]');
        const countLabel = wordPickerDialog.querySelector('[data-word-picker-count]');
        const items = Array.from(wordPickerDialog.querySelectorAll('[data-word-picker-item]'));
        const checkboxes = Array.from(wordPickerDialog.querySelectorAll('[data-word-picker-checkbox]'));
        const hiddenInput = document.querySelector('[data-selected-word-ids]');
        const summary = document.querySelector('[data-word-picker-summary]');
        const clearBtn = document.querySelector('[data-clear-word-picker]');
        const pickerRow = document.querySelector('[data-word-picker-row]');
        const rangeInputs = document.querySelectorAll('[data-range-min], [data-range-max]');
        const selectedRangeLabel = document.querySelector('[data-selected-range]');
        const total = checkboxes.length;

        const visibleCheckboxes = () => checkboxes.filter(cb => !cb.closest('[data-word-picker-item]').hidden);

        const updateCount = () => {
            const checked = checkboxes.filter(cb => cb.checked).length;
            if (countLabel) countLabel.textContent = `${checked} of ${total} selected`;
        };

        checkboxes.forEach(cb => cb.addEventListener('change', updateCount));

        openBtn?.addEventListener('click', () => {
            updateCount();
            wordPickerDialog.showModal();
        });

        closeBtn?.addEventListener('click', () => wordPickerDialog.close());

        selectAllBtn?.addEventListener('click', () => {
            visibleCheckboxes().forEach(cb => { cb.checked = true; });
            updateCount();
        });

        deselectAllBtn?.addEventListener('click', () => {
            visibleCheckboxes().forEach(cb => { cb.checked = false; });
            updateCount();
        });

        searchInput?.addEventListener('input', () => {
            const query = searchInput.value.trim().toLowerCase();
            items.forEach(item => {
                item.hidden = !!query && !item.dataset.lemma.includes(query) && !item.dataset.translation.includes(query);
            });
        });

        const applySelection = () => {
            const checkedIds = checkboxes.filter(cb => cb.checked).map(cb => cb.value);
            const isOverrideActive = checkedIds.length > 0 && checkedIds.length < total;

            if (hiddenInput) hiddenInput.value = isOverrideActive ? checkedIds.join(',') : '';

            if (summary) {
                summary.hidden = !isOverrideActive;
                summary.textContent = isOverrideActive ? `${checkedIds.length} of ${total} words picked individually` : '';
            }
            if (clearBtn) clearBtn.hidden = !isOverrideActive;

            rangeInputs.forEach(input => { input.disabled = isOverrideActive; });

            if (selectedRangeLabel) {
                if (isOverrideActive) {
                    selectedRangeLabel.textContent = `${checkedIds.length} picked`;
                } else {
                    const minInput = document.querySelector('[data-range-min]');
                    const maxInput = document.querySelector('[data-range-max]');
                    if (minInput && maxInput) {
                        minInput.dispatchEvent(new Event('input', { bubbles: true }));
                    }
                }
            }
        };

        applyBtn?.addEventListener('click', () => {
            applySelection();
            wordPickerDialog.close();
        });

        clearBtn?.addEventListener('click', () => {
            checkboxes.forEach(cb => { cb.checked = true; });
            applySelection();
        });

        if (pickerRow) {
            const syncPickerVisibility = () => {
                const selected = document.querySelector('input[name="PracticeItemType"]:checked');
                pickerRow.hidden = selected?.value === 'sentences';
            };
            itemTypes.forEach(itemType => itemType.addEventListener('change', syncPickerVisibility));
            syncPickerVisibility();
        }

        updateCount();
    }
})();

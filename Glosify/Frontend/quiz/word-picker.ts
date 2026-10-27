import { text as t } from '../shared/localization.js';

export function initializeWordPicker(itemTypes: NodeListOf<HTMLInputElement>) {
    const wordPickerDialog = document.querySelector<HTMLDialogElement>('[data-word-picker-dialog]');
    if (wordPickerDialog) {
        const openBtn = document.querySelector<HTMLElement>('[data-open-word-picker]');
        const closeBtn = wordPickerDialog.querySelector<HTMLElement>('[data-close-word-picker]');
        const applyBtn = wordPickerDialog.querySelector<HTMLElement>('[data-apply-word-picker]');
        const selectAllBtn = wordPickerDialog.querySelector<HTMLElement>('[data-select-all]');
        const deselectAllBtn = wordPickerDialog.querySelector<HTMLElement>('[data-deselect-all]');
        const searchInput = wordPickerDialog.querySelector<HTMLInputElement>('[data-word-picker-search]');
        const countLabel = wordPickerDialog.querySelector<HTMLElement>('[data-word-picker-count]');
        const items = Array.from(wordPickerDialog.querySelectorAll<HTMLElement>('[data-word-picker-item]'));
        const checkboxes = Array.from(wordPickerDialog.querySelectorAll<HTMLInputElement>('[data-word-picker-checkbox]'));
        const hiddenInput = document.querySelector<HTMLInputElement>('[data-selected-word-ids]');
        const summary = document.querySelector<HTMLElement>('[data-word-picker-summary]');
        const clearBtn = document.querySelector<HTMLElement>('[data-clear-word-picker]');
        const pickerRow = document.querySelector<HTMLElement>('[data-word-picker-row]');
        const rangeInputs = document.querySelectorAll<HTMLInputElement>('[data-range-min], [data-range-max]');
        const selectedRangeLabel = document.querySelector<HTMLElement>('[data-selected-range]');
        const total = checkboxes.length;

        const visibleCheckboxes = () => checkboxes.filter(cb => !cb.closest<HTMLElement>('[data-word-picker-item]')!.hidden);

        const updateCount = () => {
            const checked = checkboxes.filter(cb => cb.checked).length;
            if (countLabel) countLabel.textContent = t('Settings.SelectedCount', '{0} of {1} selected', checked, total);
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
                item.hidden = !!query && !(item.dataset.lemma || '').includes(query) && !(item.dataset.translation || '').includes(query);
            });
        });

        const applySelection = () => {
            const checkedIds = checkboxes.filter(cb => cb.checked).map(cb => cb.value);
            const isOverrideActive = checkedIds.length > 0 && checkedIds.length < total;

            if (hiddenInput) hiddenInput.value = isOverrideActive ? checkedIds.join(',') : '';

            if (summary) {
                summary.hidden = !isOverrideActive;
                summary.textContent = isOverrideActive ? t('Settings.PickedWords', '{0} of {1} words picked individually', checkedIds.length, total) : '';
            }
            if (clearBtn) clearBtn.hidden = !isOverrideActive;

            rangeInputs.forEach(input => { input.disabled = isOverrideActive; });

            if (selectedRangeLabel) {
                if (isOverrideActive) {
                    selectedRangeLabel.textContent = t('Settings.PickedCount', '{0} picked', checkedIds.length);
                } else {
                    const minInput = document.querySelector<HTMLInputElement>('[data-range-min]');
                    const maxInput = document.querySelector<HTMLInputElement>('[data-range-max]');
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
                const selected = document.querySelector<HTMLInputElement>('input[name="PracticeItemType"]:checked');
                const isSentences = selected?.value === 'sentences';
                pickerRow.hidden = isSentences;
                if (hiddenInput) hiddenInput.disabled = isSentences;
                if (isSentences) {
                    checkboxes.forEach(cb => { cb.checked = true; });
                    applySelection();
                    updateCount();
                    wordPickerDialog.close();
                }
            };
            itemTypes.forEach(itemType => itemType.addEventListener('change', syncPickerVisibility));
            syncPickerVisibility();
        }

        updateCount();
    }
}

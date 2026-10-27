import { text as t } from '../shared/localization.js';
import { requiredElement } from '../shared/dom.js';

export function initializePracticeRange(refreshAllLength: () => void) {
    const rangeSlider = document.querySelector<HTMLElement>('[data-range-slider]');
    if (rangeSlider) {
        const minInput = requiredElement<HTMLInputElement>(rangeSlider, '[data-range-min]');
        const maxInput = requiredElement<HTMLInputElement>(rangeSlider, '[data-range-max]');
        const fill = rangeSlider.querySelector<HTMLElement>('[data-range-fill]');
        const rangeLabel = document.querySelector<HTMLElement>('[data-range-label]');
        const selectedRange = document.querySelector<HTMLElement>('[data-selected-range]');
        const presets = document.querySelectorAll<HTMLElement>('[data-range-preset]');
        const minGap = 5;

        const describeRange = (minVal: number, maxVal: number) => {
            const width = maxVal - minVal;
            if (minVal === 0 && maxVal === 100) return t('Common.All', 'All');
            if (maxVal === 100) return t('Settings.Newest', 'Newest {0}%', width);
            if (minVal === 0) return t('Settings.Oldest', 'Oldest {0}%', width);
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
                    ? t('Settings.AllWordsOrder', 'Practicing all words, oldest to newest.')
                    : t('Settings.RangeDynamic', 'Practicing {0} of the list (oldest to newest).', describeRange(minVal, maxVal));
            }

            if (selectedRange) {
                selectedRange.textContent = describeRange(minVal, maxVal);
            }

            refreshAllLength();
        };

        const clamp = (changed: 'min' | 'max') => {
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
                const [start, end] = preset.dataset.rangePreset!.split(',').map(Number);
                minInput.value = String(start);
                maxInput.value = String(end);
                render();
            });
        });

        render();
    }

}

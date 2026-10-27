export function attachTypingKeyboard(shell: HTMLElement, input: HTMLInputElement) {
    if (shell.dataset.showUkrainianKeyboard === 'true') {
        const qwertyToUkrainian: Record<string, string> = {
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

    shell.querySelector<HTMLElement>('[data-ukrainian-keyboard]')?.addEventListener('click', event => {
        const key = (event.target as Element | null)?.closest<HTMLElement>('[data-key-value], [data-key-action]');
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

}

(() => {
    const root = document.querySelector('[data-custom-builder]');
    if (!root) return;

    const canvas = root.querySelector('[data-custom-canvas]');
    const inspector = root.querySelector('[data-custom-inspector]');
    const previewHost = root.querySelector('[data-custom-preview-host]');
    const nameInput = root.querySelector('[data-custom-name]');
    const saveButton = root.querySelector('[data-custom-save]');
    const undoButton = root.querySelector('[data-custom-undo]');
    const previewButton = root.querySelector('[data-custom-preview]');
    const errorHost = root.querySelector('[data-custom-errors]');
    const messageHost = root.querySelector('[data-custom-message]');
    const runtimeHost = root.querySelector('[data-custom-runtime]');
    const token = root.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    const words = JSON.parse(root.querySelector('[data-custom-words]').value || '[]');
    const templates = JSON.parse(root.querySelector('[data-custom-templates]')?.value || '[]');
    let documentModel = JSON.parse(root.querySelector('[data-custom-document]').value || '{"schemaVersion":1,"blocks":[]}');
    let customId = root.dataset.customId || '';
    let rowVersion = root.dataset.rowVersion || '';
    let selectedId = documentModel.blocks[0]?.id || '';
    let dirty = false;
    let previewing = false;
    let dragState = null;
    const history = [];

    const labels = {
        quiz_heading: 'Heading', instruction_label: 'Instruction', prompt_label: 'Word label',
        translation_label: 'Translation label', text_input: 'Text input', textarea: 'Long answer',
        checkbox: 'Checkbox', radio_group: 'Radio choices', multi_select_group: 'Checkbox choices',
        select_menu: 'Select menu', word_bank: 'Word bank', submit_button: 'Submit button',
        feedback_message: 'Feedback'
    };
    const answerTypes = new Set(['text_input', 'textarea', 'checkbox', 'radio_group', 'multi_select_group', 'select_menu']);
    const choiceTypes = new Set(['radio_group', 'multi_select_group', 'select_menu']);
    const spans = [3, 4, 6, 12];
    let uidSequence = 0;
    const uid = () => {
        const uuid = globalThis.crypto?.randomUUID?.();
        if (uuid) return uuid.replace(/-/g, '');
        uidSequence += 1;
        return `${Date.now().toString(36)}${uidSequence.toString(36)}${Math.random().toString(36).slice(2)}`;
    };
    const byId = id => documentModel.blocks.find(block => block.id === id);
    const resolve = binding => {
        const word = words.find(item => item.id === binding?.wordId);
        return word ? (binding.field === 'translation' ? word.translation : word.lemma) : '';
    };
    const element = (tag, className, text) => {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    };
    const setStatus = (host, text) => {
        host.textContent = text || '';
        host.hidden = !text;
    };
    const snapshot = () => JSON.stringify(documentModel);
    const sortOrders = () => documentModel.blocks
        .sort((a, b) => a.gridRow - b.gridRow || a.gridColumn - b.gridColumn || a.order - b.order)
        .forEach((block, index) => { block.order = index; });
    const overlaps = (block, row, column, span) => block.gridRow === row
        && column < block.gridColumn + block.columnSpan
        && block.gridColumn < column + span;
    const findPlacement = (row, column, span, ignoreId = '', candidates = documentModel.blocks) => {
        const safeSpan = spans.includes(Number(span)) ? Number(span) : 12;
        const preferredRow = Math.max(1, Math.min(500, Number(row) || 1));
        const preferredColumn = Math.max(1, Math.min(13 - safeSpan, Number(column) || 1));
        const columns = Array.from({ length: 13 - safeSpan }, (_, index) => index + 1)
            .sort((a, b) => Math.abs(a - preferredColumn) - Math.abs(b - preferredColumn));
        const maxRow = Math.min(500, Math.max(preferredRow + candidates.length + 2, ...candidates.map(block => block.gridRow || 1)) + 1);
        for (let targetRow = preferredRow; targetRow <= maxRow; targetRow += 1) {
            for (const targetColumn of columns) {
                const occupied = candidates.some(block => block.id !== ignoreId && overlaps(block, targetRow, targetColumn, safeSpan));
                if (!occupied) return { row: targetRow, column: targetColumn, span: safeSpan };
            }
        }
        return { row: maxRow, column: 1, span: safeSpan };
    };
    const normalizeLayout = () => {
        const placed = [];
        documentModel.blocks.sort((a, b) => a.order - b.order).forEach(block => {
            block.columnSpan = spans.includes(Number(block.columnSpan)) ? Number(block.columnSpan) : 12;
            const position = findPlacement(block.gridRow, block.gridColumn, block.columnSpan, block.id, placed);
            block.gridRow = position.row;
            block.gridColumn = position.column;
            placed.push(block);
        });
        sortOrders();
    };
    const mutate = action => {
        history.push(snapshot());
        if (history.length > 100) history.shift();
        action();
        sortOrders();
        dirty = true;
        undoButton.disabled = false;
        setStatus(messageHost, '');
        render();
    };

    const defaultBinding = () => words.length ? { wordId: words[0].id, field: 'lemma' } : null;
    const defaultSpanForType = type => ['quiz_heading', 'instruction_label'].includes(type) ? 12
        : ['prompt_label', 'translation_label', 'submit_button'].includes(type) ? 4 : 6;
    const makeBlock = type => {
        const defaultSpan = defaultSpanForType(type);
        const block = { id: uid(), type, order: documentModel.blocks.length, columnSpan: defaultSpan, gridColumn: 1, gridRow: 1, options: [], targetInputIds: [] };
        if (type === 'quiz_heading') block.text = 'Quiz heading';
        if (type === 'instruction_label') block.text = 'Answer the questions below.';
        if (type === 'prompt_label' || type === 'translation_label') block.binding = defaultBinding();
        if (type === 'text_input' || type === 'textarea') { block.label = type === 'text_input' ? 'Answer {{blank}}' : 'Your answer'; block.expectedBinding = defaultBinding(); }
        if (type === 'checkbox') { block.label = 'Select this option'; block.binding = defaultBinding(); block.expectedChecked = true; }
        if (choiceTypes.has(type)) {
            block.label = 'Choose an answer';
            words.slice(0, Math.min(3, words.length)).forEach((word, index) => block.options.push({ id: uid(), binding: { wordId: word.id, field: 'lemma' }, isCorrect: index === 0 }));
        }
        if (type === 'word_bank') { block.label = 'Word bank'; block.options = words.slice(0, 5).map(word => ({ id: uid(), binding: { wordId: word.id, field: 'lemma' }, isCorrect: false })); }
        if (type === 'submit_button') block.text = 'Check answers';
        return block;
    };
    const addBlockAt = (type, row = 1, column = 1) => mutate(() => {
        const block = makeBlock(type);
        const placement = findPlacement(row, column, block.columnSpan);
        block.gridRow = placement.row;
        block.gridColumn = placement.column;
        documentModel.blocks.push(block);
        selectedId = block.id;
    });
    const addBlock = type => addBlockAt(type, 1, 1);
    const moveBlock = (id, rowDelta, columnDelta) => mutate(() => {
        const block = byId(id);
        const placement = findPlacement(block.gridRow + rowDelta, block.gridColumn + columnDelta, block.columnSpan, block.id);
        block.gridRow = placement.row;
        block.gridColumn = placement.column;
    });

    const applyBlockPosition = (node, block) => {
        node.style.setProperty('--custom-span', block.columnSpan);
        node.style.setProperty('--custom-column', block.gridColumn);
        node.style.setProperty('--custom-row', block.gridRow);
    };

    const splitInlineBlank = value => {
        const label = String(value || 'Answer').trim();
        const match = /\{\{blank\}\}|\{blank\}|_{2,}|\.{3,}/i.exec(label);
        if (!match) return { before: label, after: '' };
        return {
            before: label.slice(0, match.index).trimEnd(),
            after: label.slice(match.index + match[0].length).trimStart()
        };
    };

    const renderQuizContent = (block, host, interactive) => {
        if (block.type === 'quiz_heading') host.append(element('h2', '', block.text || 'Quiz heading'));
        else if (block.type === 'instruction_label') host.append(element('p', 'custom-instruction', block.text || ''));
        else if (['prompt_label', 'translation_label'].includes(block.type)) host.append(element('p', 'custom-bound-label', resolve(block.binding) || 'Choose a word'));
        else if (['text_input', 'textarea'].includes(block.type)) {
            const isInline = block.type === 'text_input';
            const parts = splitInlineBlank(block.label);
            const label = element('label', isInline ? 'custom-inline-answer' : '');
            const control = document.createElement(block.type === 'textarea' ? 'textarea' : 'input');
            control.className = 'form-input';
            if (block.type === 'textarea') control.rows = 4;
            control.dataset.previewAnswer = block.id;
            if (isInline) {
                if (parts.before) label.append(element('span', 'custom-inline-answer-before', parts.before));
                label.append(control);
                if (parts.after) label.append(element('span', 'custom-inline-answer-after', parts.after));
            } else {
                label.append(document.createTextNode(block.label || 'Answer'), control);
            }
            host.append(label, element('div', 'custom-answer-feedback'));
        } else if (block.type === 'checkbox') {
            const label = element('label', 'custom-check');
            const control = document.createElement('input');
            control.type = 'checkbox';
            control.dataset.previewAnswer = block.id;
            label.append(control, element('span', '', `${block.label || ''} ${resolve(block.binding)}`.trim()));
            host.append(label, element('div', 'custom-answer-feedback'));
        } else if (choiceTypes.has(block.type)) {
            const fieldset = document.createElement('fieldset');
            fieldset.append(element('legend', '', block.label || 'Choose'));
            if (block.type === 'select_menu') {
                const select = element('select', 'form-input');
                select.dataset.previewAnswer = block.id;
                const emptyOption = element('option', '', 'Choose...');
                emptyOption.value = '';
                select.append(emptyOption);
                block.options.forEach(option => {
                    const node = element('option', '', resolve(option.binding));
                    node.value = option.id;
                    select.append(node);
                });
                fieldset.append(select);
            } else {
                block.options.forEach(option => {
                    const label = element('label', 'custom-choice');
                    const input = document.createElement('input');
                    input.type = block.type === 'radio_group' ? 'radio' : 'checkbox';
                    input.name = `preview-${block.id}`;
                    input.value = option.id;
                    input.dataset.previewAnswer = block.id;
                    label.append(input, document.createTextNode(resolve(option.binding)));
                    fieldset.append(label);
                });
            }
            host.append(fieldset, element('div', 'custom-answer-feedback'));
        } else if (block.type === 'word_bank') {
            const bank = element('div', 'custom-word-bank');
            bank.append(element('span', '', block.label || 'Word bank'));
            block.options.forEach(option => {
                const button = element('button', '', resolve(option.binding));
                button.type = 'button';
                bank.append(button);
            });
            host.append(bank);
        } else if (block.type === 'submit_button') {
            const button = element('button', 'btn-submit', block.text || 'Check answers');
            button.type = 'submit';
            host.append(button);
        } else if (block.type === 'feedback_message') {
            host.append(element('div', 'custom-overall-feedback', 'Complete the quiz, then submit your answers.'));
        }
        if (!interactive) host.querySelectorAll('input, textarea, select, button').forEach(control => { control.tabIndex = -1; });
    };

    const iconButton = (icon, title, action) => {
        const button = element('button', 'btn-icon');
        button.type = 'button';
        button.title = title;
        button.setAttribute('aria-label', title);
        button.append(element('span', 'material-symbols-outlined', icon));
        button.addEventListener('click', event => { event.stopPropagation(); action(); });
        return button;
    };

    const beginPointerAction = (event, settings) => {
        if (event.button !== undefined && event.button !== 0) return;
        if (settings.kind !== 'palette') event.preventDefault();
        try { settings.source.setPointerCapture?.(event.pointerId); } catch { /* Pointer capture is an enhancement, not a requirement. */ }
        dragState = {
            ...settings,
            pointerId: event.pointerId,
            startX: event.clientX,
            startY: event.clientY,
            lastX: event.clientX,
            lastY: event.clientY,
            started: false,
            placement: null,
            ghost: null
        };
    };

    const rowAtPoint = clientY => {
        const cards = [...canvas.querySelectorAll('[data-block-id]')].filter(card => card.dataset.blockId !== dragState?.blockId);
        if (!cards.length) return 1;
        const rows = new Map();
        cards.forEach(card => {
            const row = Number(byId(card.dataset.blockId)?.gridRow || 1);
            const rect = card.getBoundingClientRect();
            const current = rows.get(row);
            rows.set(row, current ? { top: Math.min(current.top, rect.top), bottom: Math.max(current.bottom, rect.bottom) } : { top: rect.top, bottom: rect.bottom });
        });
        const ordered = [...rows.entries()].sort((a, b) => a[1].top - b[1].top);
        if (clientY <= ordered[0][1].bottom) return ordered[0][0];
        for (let index = 1; index < ordered.length; index += 1) {
            const previous = ordered[index - 1];
            const current = ordered[index];
            if (clientY < current[1].top) return clientY < (previous[1].bottom + current[1].top) / 2 ? previous[0] : current[0];
            if (clientY <= current[1].bottom) return current[0];
        }
        return Math.min(500, Math.max(...ordered.map(item => item[0])) + 1);
    };

    const placementAtPoint = (clientX, clientY, span, ignoreId = '') => {
        const rect = canvas.getBoundingClientRect();
        if (clientX < rect.left || clientX > rect.right || clientY < rect.top || clientY > rect.bottom) return null;
        const cellWidth = rect.width / 12;
        const column = Math.max(1, Math.min(13 - span, Math.floor((clientX - rect.left) / cellWidth) + 1));
        return findPlacement(rowAtPoint(clientY), column, span, ignoreId);
    };

    const dropIndicator = element('div', 'custom-drop-indicator');
    dropIndicator.hidden = true;
    const showDropIndicator = placement => {
        if (!dropIndicator.isConnected) canvas.append(dropIndicator);
        applyBlockPosition(dropIndicator, { columnSpan: placement.span, gridColumn: placement.column, gridRow: placement.row });
        dropIndicator.hidden = false;
    };
    const hideDropIndicator = () => { dropIndicator.hidden = true; };

    const updatePointerAction = event => {
        if (!dragState || event.pointerId !== dragState.pointerId) return;
        dragState.lastX = event.clientX;
        dragState.lastY = event.clientY;
        const distance = Math.hypot(event.clientX - dragState.startX, event.clientY - dragState.startY);
        if (!dragState.started && distance < 5) return;
        if (!dragState.started) {
            dragState.started = true;
            root.classList.add('is-custom-dragging');
            dragState.card?.classList.add('is-dragging');
            dragState.ghost = element('div', 'custom-drag-ghost', dragState.label);
            document.body.append(dragState.ghost);
        }
        event.preventDefault();
        dragState.ghost.style.left = `${event.clientX + 14}px`;
        dragState.ghost.style.top = `${event.clientY + 14}px`;
        let span = dragState.span;
        if (dragState.kind === 'resize') {
            const cellWidth = canvas.getBoundingClientRect().width / 12;
            const rawSpan = dragState.startSpan + Math.round((event.clientX - dragState.startX) / cellWidth);
            span = spans.reduce((best, candidate) => Math.abs(candidate - rawSpan) < Math.abs(best - rawSpan) ? candidate : best, spans[0]);
        }
        dragState.placement = dragState.kind === 'resize'
            ? findPlacement(dragState.startRow, dragState.startColumn, span, dragState.blockId)
            : placementAtPoint(event.clientX, event.clientY, span, dragState.blockId || '');
        if (dragState.placement) showDropIndicator(dragState.placement); else hideDropIndicator();
    };

    const finishPointerAction = event => {
        if (!dragState || event.pointerId !== dragState.pointerId) return;
        const state = dragState;
        dragState = null;
        try { state.source.releasePointerCapture?.(event.pointerId); } catch { /* Capture may already have been released by the browser. */ }
        state.ghost?.remove();
        state.card?.classList.remove('is-dragging');
        root.classList.remove('is-custom-dragging');
        hideDropIndicator();
        if (event.type === 'pointercancel') return;
        if (!state.started) return;
        if (state.kind === 'palette') state.source.dataset.suppressClickUntil = String(Date.now() + 500);
        if (!state.placement) return;
        if (state.kind === 'palette') addBlockAt(state.type, state.placement.row, state.placement.column);
        else mutate(() => {
            const block = byId(state.blockId);
            block.gridRow = state.placement.row;
            block.gridColumn = state.placement.column;
            block.columnSpan = state.placement.span;
            selectedId = block.id;
        });
    };
    document.addEventListener('pointermove', updatePointerAction, { passive: false });
    document.addEventListener('pointerup', finishPointerAction);
    document.addEventListener('pointercancel', finishPointerAction);

    const renderCanvas = () => {
        canvas.replaceChildren();
        if (!documentModel.blocks.length) canvas.append(element('div', 'custom-canvas-empty', 'Drag a block anywhere onto this canvas to begin.'));
        documentModel.blocks.forEach(block => {
            const card = element('section', `custom-play-block custom-canvas-block${block.id === selectedId ? ' is-selected' : ''}`);
            card.dataset.blockId = block.id;
            card.dataset.blockType = block.type;
            card.tabIndex = 0;
            applyBlockPosition(card, block);

            const chrome = element('div', 'custom-canvas-chrome');
            const handle = iconButton('drag_indicator', `Drag ${labels[block.type]}`, () => {});
            handle.classList.add('custom-drag-handle');
            handle.addEventListener('pointerdown', event => beginPointerAction(event, {
                kind: 'move', source: handle, card, blockId: block.id, span: block.columnSpan, label: labels[block.type]
            }));
            const typeLabel = element('span', 'custom-canvas-type', labels[block.type] || block.type);
            const controls = element('div', 'custom-canvas-controls');
            controls.append(
                iconButton('arrow_back', 'Move left', () => moveBlock(block.id, 0, -1)),
                iconButton('arrow_upward', 'Move up', () => moveBlock(block.id, -1, 0)),
                iconButton('arrow_downward', 'Move down', () => moveBlock(block.id, 1, 0)),
                iconButton('arrow_forward', 'Move right', () => moveBlock(block.id, 0, 1)),
                iconButton('delete', 'Remove block', () => mutate(() => {
                    documentModel.blocks = documentModel.blocks.filter(item => item.id !== block.id);
                    selectedId = documentModel.blocks[0]?.id || '';
                }))
            );
            chrome.append(handle, typeLabel, controls);
            const content = element('div', 'custom-block-content');
            renderQuizContent(block, content, false);
            const resize = element('button', 'custom-resize-handle');
            resize.type = 'button';
            resize.title = 'Drag to resize width';
            resize.setAttribute('aria-label', `Resize ${labels[block.type]}`);
            resize.addEventListener('pointerdown', event => beginPointerAction(event, {
                kind: 'resize', source: resize, card, blockId: block.id, span: block.columnSpan, startSpan: block.columnSpan,
                startRow: block.gridRow, startColumn: block.gridColumn, label: `Resize ${labels[block.type]}`
            }));
            card.append(chrome, content, resize);
            card.addEventListener('click', () => { selectedId = block.id; render(); });
            card.addEventListener('keydown', event => {
                if (!event.altKey) return;
                const moves = { ArrowLeft: [0, -1], ArrowRight: [0, 1], ArrowUp: [-1, 0], ArrowDown: [1, 0] };
                if (moves[event.key]) { event.preventDefault(); moveBlock(block.id, ...moves[event.key]); }
            });
            canvas.append(card);
        });
        canvas.append(dropIndicator);
    };

    const addField = (labelText, value, onChange, type = 'text') => {
        const label = element('label', 'custom-inspector-field');
        label.append(element('span', '', labelText));
        const input = document.createElement('input');
        input.className = 'form-input'; input.type = type; input.value = value ?? '';
        input.addEventListener('change', () => mutate(() => onChange(input.type === 'checkbox' ? input.checked : input.value)));
        if (type === 'checkbox') input.checked = Boolean(value);
        label.append(input); inspector.append(label); return input;
    };
    const addSelect = (labelText, value, options, onChange) => {
        const label = element('label', 'custom-inspector-field'); label.append(element('span', '', labelText));
        const select = element('select', 'form-input');
        options.forEach(option => { const node = element('option', '', option.label); node.value = option.value; node.selected = option.value === value; select.append(node); });
        select.addEventListener('change', () => mutate(() => onChange(select.value)));
        label.append(select); inspector.append(label); return select;
    };
    const wordOptions = () => words.map(word => ({ value: word.id, label: `${word.translation} — ${word.lemma}` }));
    const bindingEditor = (title, binding, setter) => {
        const group = element('fieldset', 'custom-binding-editor'); group.append(element('legend', '', title));
        inspector.append(group);
        const wordLabel = element('label', 'custom-inspector-field'); wordLabel.append(element('span', '', 'Word'));
        const wordSelect = element('select', 'form-input');
        wordOptions().forEach(option => { const node = element('option', '', option.label); node.value = option.value; node.selected = option.value === binding?.wordId; wordSelect.append(node); });
        wordSelect.addEventListener('change', () => mutate(() => setter({ wordId: wordSelect.value, field: binding?.field || 'lemma' })));
        wordLabel.append(wordSelect); group.append(wordLabel);
        const fieldLabel = element('label', 'custom-inspector-field'); fieldLabel.append(element('span', '', 'Display field'));
        const fieldSelect = element('select', 'form-input');
        [{ value: 'lemma', label: 'Word / lemma' }, { value: 'translation', label: 'Translation' }].forEach(option => {
            const node = element('option', '', option.label); node.value = option.value; node.selected = option.value === binding?.field; fieldSelect.append(node);
        });
        fieldSelect.addEventListener('change', () => mutate(() => setter({ wordId: binding?.wordId || words[0]?.id || '', field: fieldSelect.value })));
        fieldLabel.append(fieldSelect); group.append(fieldLabel);
    };
    const optionEditor = block => {
        inspector.append(element('h3', '', block.type === 'word_bank' ? 'Words' : 'Options'));
        block.options.forEach((option, index) => {
            const row = element('div', 'custom-option-editor');
            const select = element('select', 'form-input');
            wordOptions().forEach(item => { const node = element('option', '', item.label); node.value = item.value; node.selected = item.value === option.binding.wordId; select.append(node); });
            select.addEventListener('change', () => mutate(() => { option.binding.wordId = select.value; }));
            const field = element('select', 'form-input');
            [['lemma', 'Word'], ['translation', 'Translation']].forEach(([value, label]) => { const node = element('option', '', label); node.value = value; node.selected = value === option.binding.field; field.append(node); });
            field.addEventListener('change', () => mutate(() => { option.binding.field = field.value; }));
            row.append(select, field);
            if (choiceTypes.has(block.type)) {
                const correct = document.createElement('input'); correct.type = block.type === 'multi_select_group' ? 'checkbox' : 'radio'; correct.name = `correct-${block.id}`; correct.checked = option.isCorrect; correct.title = 'Correct option';
                correct.addEventListener('change', () => mutate(() => {
                    if (block.type !== 'multi_select_group') block.options.forEach(item => { item.isCorrect = false; });
                    option.isCorrect = correct.checked;
                }));
                row.append(correct);
            }
            row.append(iconButton('close', 'Remove option', () => mutate(() => { block.options.splice(index, 1); })));
            inspector.append(row);
        });
        const add = element('button', 'btn-secondary', 'Add option'); add.type = 'button'; add.disabled = words.length === 0;
        add.addEventListener('click', () => mutate(() => block.options.push({ id: uid(), binding: defaultBinding(), isCorrect: false })));
        inspector.append(add);
    };
    const changeBlockWidth = (block, value) => {
        const placement = findPlacement(block.gridRow, block.gridColumn, Number(value), block.id);
        block.columnSpan = Number(value); block.gridRow = placement.row; block.gridColumn = placement.column;
    };
    const changeBlockPosition = (block, row, column) => {
        const placement = findPlacement(row, column, block.columnSpan, block.id);
        block.gridRow = placement.row; block.gridColumn = placement.column;
    };
    const renderInspector = () => {
        inspector.replaceChildren(element('h2', '', 'Properties'));
        const block = byId(selectedId);
        if (!block) { inspector.append(element('p', '', 'Select a block to edit its content and bindings.')); return; }
        inspector.append(element('p', 'custom-inspector-type', labels[block.type] || block.type));
        addSelect('Width', String(block.columnSpan), spans.map(span => ({ value: String(span), label: `${Math.round(span / 12 * 100)}%` })), value => changeBlockWidth(block, value));
        addSelect('Column', String(block.gridColumn), Array.from({ length: 13 - block.columnSpan }, (_, index) => ({ value: String(index + 1), label: `Column ${index + 1}` })), value => changeBlockPosition(block, block.gridRow, Number(value)));
        addField('Row', block.gridRow, value => changeBlockPosition(block, Math.max(1, Number(value) || 1), block.gridColumn), 'number');
        if (['quiz_heading', 'instruction_label', 'submit_button'].includes(block.type)) addField('Text', block.text, value => { block.text = value; });
        if (['prompt_label', 'translation_label'].includes(block.type)) bindingEditor('Live word binding', block.binding, value => { block.binding = value; });
        if (answerTypes.has(block.type)) addField(block.type === 'text_input' ? 'Exercise row (use {{blank}})' : 'Accessible label', block.label, value => { block.label = value; });
        if (['text_input', 'textarea'].includes(block.type)) {
            addField('Custom expected answer', block.expectedText, value => {
                block.expectedText = value;
                if (value?.trim()) block.expectedBinding = null;
            });
            if (!block.expectedText?.trim()) bindingEditor('Expected word binding', block.expectedBinding, value => { block.expectedBinding = value; });
        }
        if (block.type === 'checkbox') {
            bindingEditor('Displayed word', block.binding, value => { block.binding = value; });
            addField('Expected to be checked', block.expectedChecked, value => { block.expectedChecked = value; }, 'checkbox');
        }
        if (choiceTypes.has(block.type) || block.type === 'word_bank') optionEditor(block);
        if (block.type === 'word_bank') {
            const inputs = documentModel.blocks.filter(item => ['text_input', 'textarea'].includes(item.type));
            inspector.append(element('h3', '', 'Target inputs'));
            inputs.forEach(inputBlock => {
                const label = element('label', 'custom-check'); const check = document.createElement('input'); check.type = 'checkbox'; check.checked = block.targetInputIds.includes(inputBlock.id);
                check.addEventListener('change', () => mutate(() => { if (check.checked) block.targetInputIds.push(inputBlock.id); else block.targetInputIds = block.targetInputIds.filter(id => id !== inputBlock.id); }));
                label.append(check, element('span', '', inputBlock.label || inputBlock.id)); inspector.append(label);
            });
        }
    };
    const renderPreview = () => {
        previewHost.replaceChildren();
        const form = element('form', 'custom-player-grid custom-preview-grid');
        documentModel.blocks.forEach(block => {
            const section = element('section', 'custom-play-block');
            section.dataset.blockType = block.type;
            applyBlockPosition(section, block);
            renderQuizContent(block, section, true);
            form.append(section);
        });
        form.addEventListener('submit', event => {
            event.preventDefault();
            const feedback = form.querySelector('.custom-overall-feedback');
            if (feedback) feedback.textContent = 'Preview only — save and play to record a graded attempt.';
        });
        previewHost.append(form);
    };
    const render = () => {
        root.dataset.customStyle = documentModel.stylePreset || 'editorial';
        renderCanvas(); renderInspector(); renderPreview();
        canvas.hidden = previewing; inspector.hidden = previewing; previewHost.hidden = !previewing;
        previewButton.textContent = previewing ? 'Back to editor' : 'Preview';
        if (runtimeHost) {
            runtimeHost.textContent = previewing
                ? `Previewing ${documentModel.blocks.length} block${documentModel.blocks.length === 1 ? '' : 's'}.`
                : `Editor ready · ${documentModel.blocks.length} block${documentModel.blocks.length === 1 ? '' : 's'} on the canvas. Use + to add or drag a block.`;
            runtimeHost.classList.add('is-ready');
        }
        root.querySelectorAll('[data-template-card]').forEach(card => {
            const template = templates.find(item => item.id === card.dataset.templateCard);
            card.classList.toggle('is-active', template?.stylePreset === documentModel.stylePreset);
        });
    };

    const templateById = id => templates.find(template => template.id === id);
    root.querySelectorAll('[data-template-style]').forEach(button => {
        button.addEventListener('click', () => {
            const template = templateById(button.dataset.templateStyle);
            if (!template) return;
            mutate(() => { documentModel.stylePreset = template.stylePreset; });
        });
    });
    root.querySelectorAll('[data-template-apply]').forEach(button => {
        button.addEventListener('click', () => {
            const template = templateById(button.dataset.templateApply);
            if (!template) return;
            if (documentModel.blocks.length && !window.confirm(`Replace the current canvas with the ${template.name} layout? You can Undo this change.`)) return;
            mutate(() => {
                documentModel = JSON.parse(JSON.stringify(template.document));
                normalizeLayout();
                selectedId = documentModel.blocks[0]?.id || '';
            });
        });
    });

    root.querySelectorAll('[data-palette-drag]').forEach(button => {
        button.addEventListener('pointerdown', event => beginPointerAction(event, {
            kind: 'palette', source: button, type: button.dataset.paletteType, span: defaultSpanForType(button.dataset.paletteType),
            label: labels[button.dataset.paletteType]
        }));
        button.addEventListener('click', () => {
            const suppressUntil = Number(button.dataset.suppressClickUntil || 0);
            delete button.dataset.suppressClickUntil;
            if (Date.now() <= suppressUntil) return;
            addBlock(button.dataset.paletteType);
        });
        button.addEventListener('keydown', event => { if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); addBlock(button.dataset.paletteType); } });
    });
    root.querySelectorAll('[data-palette-add]').forEach(button => {
        button.addEventListener('click', () => addBlock(button.dataset.paletteType));
    });
    nameInput.addEventListener('input', () => { dirty = true; });
    previewButton.addEventListener('click', () => { previewing = !previewing; render(); });
    undoButton.addEventListener('click', () => {
        if (!history.length) return;
        documentModel = JSON.parse(history.pop()); dirty = true; undoButton.disabled = history.length === 0;
        if (!byId(selectedId)) selectedId = documentModel.blocks[0]?.id || '';
        normalizeLayout(); render();
    });
    saveButton.addEventListener('click', async () => {
        saveButton.disabled = true; setStatus(errorHost, ''); setStatus(messageHost, '');
        try {
            const response = await fetch(customId ? `/CustomQuizzes/${customId}` : '/CustomQuizzes', {
                method: customId ? 'PUT' : 'POST',
                headers: { 'Content-Type': 'application/json', 'Accept': 'application/json', 'RequestVerificationToken': token },
                body: JSON.stringify({ quizId: root.dataset.quizId, name: nameInput.value, document: documentModel, rowVersion })
            });
            const result = await response.json().catch(() => null);
            if (!response.ok) { setStatus(errorHost, result?.error || result?.errors?.join(' ') || 'Could not save this custom quiz.'); return; }
            customId = result.id; rowVersion = result.rowVersion || ''; documentModel = result.document; normalizeLayout(); dirty = false; history.length = 0; undoButton.disabled = true;
            root.dataset.customId = customId; setStatus(messageHost, result.isPlayable ? 'Saved. This quiz is ready to play.' : `Draft saved. ${result.playabilityErrors.join(' ')}`);
            window.history.replaceState(null, '', `/CustomQuizzes/${customId}/Edit`); render();
        } catch { setStatus(errorHost, 'The save request stopped unexpectedly. Try again.'); }
        finally { saveButton.disabled = false; }
    });
    window.addEventListener('beforeunload', event => { if (dirty) { event.preventDefault(); event.returnValue = ''; } });
    normalizeLayout();
    render();
})();

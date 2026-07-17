(() => {
    const root = document.querySelector('[data-custom-player]');
    if (!root) return;
    const form = root.querySelector('[data-custom-player-form]');
    const token = root.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    let focusedInput = null;

    root.querySelectorAll('input[type="text"], textarea').forEach(input => input.addEventListener('focus', () => { focusedInput = input; }));
    root.querySelectorAll('[data-word-bank]').forEach(bank => {
        const targets = (bank.dataset.targetInputs || '').split(',').filter(Boolean);
        bank.querySelectorAll('[data-bank-value]').forEach(button => button.addEventListener('click', () => {
            let target = focusedInput?.dataset.answerBlock && targets.includes(focusedInput.dataset.answerBlock) ? focusedInput : null;
            if (!target) target = targets.map(id => root.querySelector(`[data-answer-block="${CSS.escape(id)}"]`)).find(input => input && !input.value);
            if (!target) target = root.querySelector(`[data-answer-block="${CSS.escape(targets[0] || '')}"]`);
            if (target) { target.value = button.dataset.bankValue || button.textContent; target.focus(); }
        }));
    });

    const collectAnswers = () => {
        const ids = [...new Set([...root.querySelectorAll('[data-answer-block]')].map(control => control.dataset.answerBlock))];
        return ids.map(blockId => {
            const controls = [...root.querySelectorAll(`[data-answer-block="${CSS.escape(blockId)}"]`)];
            const type = controls[0]?.closest('[data-block-type]')?.dataset.blockType;
            let values = [];
            if (type === 'checkbox') values = [String(controls[0].checked)];
            else if (type === 'radio_group' || type === 'multi_select_group') values = controls.filter(control => control.checked).map(control => control.value);
            else values = controls[0]?.value ? [controls[0].value] : [];
            return { blockId, values };
        });
    };

    form.addEventListener('submit', async event => {
        event.preventDefault();
        const submit = form.querySelector('button[type="submit"]'); if (submit) submit.disabled = true;
        try {
            const response = await fetch(root.dataset.gradeUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'Accept': 'application/json', 'RequestVerificationToken': token },
                body: JSON.stringify({ attemptId: root.dataset.attemptId, classroomId: root.dataset.classroomId || null, answers: collectAnswers() })
            });
            const result = await response.json().catch(() => null);
            if (!response.ok || !result) throw new Error();
            root.querySelectorAll('[data-answer-feedback]').forEach(host => { host.textContent = ''; host.className = 'custom-answer-feedback'; });
            result.blocks.forEach(grade => {
                const block = root.querySelector(`[data-play-block="${CSS.escape(grade.blockId)}"]`);
                const host = block?.querySelector('[data-answer-feedback]');
                if (host) { host.textContent = grade.message; host.classList.add(`is-${grade.state}`); }
            });
            const overall = root.querySelector('[data-overall-feedback]');
            if (overall) overall.textContent = result.state === 'incomplete' ? 'Complete every answer before submitting.' : `Score: ${result.correctCount} of ${result.totalCount} (${result.scorePercent}%).`;
            if (result.state !== 'incomplete') root.querySelectorAll('[data-answer-block]').forEach(control => { control.disabled = true; });
        } catch {
            const overall = root.querySelector('[data-overall-feedback]'); if (overall) overall.textContent = 'Could not grade this quiz. Try again.';
        } finally { if (submit) submit.disabled = false; }
    });
})();

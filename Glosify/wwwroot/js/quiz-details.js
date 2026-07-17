(() => {
    const tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
    const messageHost = document.querySelector('[data-ai-message-host]');

    const setMessage = (text, kind = 'success') => {
        if (!messageHost) return;
        messageHost.innerHTML = '';
        const message = document.createElement('div');
        message.className = `panel-message ${kind}`;
        message.textContent = text;
        messageHost.appendChild(message);
    };

    const postRepair = async (button, body) => {
        if (!button?.dataset.repairUrl) return;
        const originalHtml = button.innerHTML;
        button.disabled = true;
        button.innerHTML = '<span class="material-symbols-outlined">progress_activity</span>';

        try {
            const response = await fetch(button.dataset.repairUrl, {
                method: 'POST',
                headers: {
                    'Accept': 'application/json',
                    'RequestVerificationToken': tokenInput?.value ?? ''
                },
                body
            });
            const data = await response.json().catch(() => null);
            if (!response.ok) {
                setMessage(data?.error || 'Repair failed. Please try again.', 'error');
                return;
            }

            setMessage(data?.message || 'Repair finished.');
            window.setTimeout(() => window.location.reload(), 450);
        } catch {
            setMessage('The repair request stopped unexpectedly. Please try again.', 'error');
        } finally {
            button.disabled = false;
            button.innerHTML = originalHtml;
        }
    };

    document.querySelectorAll('[data-repair-word-button]').forEach(button => {
        button.addEventListener('click', event => {
            postRepair(event.currentTarget, new FormData());
        });
    });

    document.querySelectorAll('[data-repair-sentence-button]').forEach(button => {
        button.addEventListener('click', event => {
            const body = new FormData();
            body.append('quizId', event.currentTarget.dataset.quizId || '');
            body.append('text', event.currentTarget.dataset.sentenceText || '');
            postRepair(event.currentTarget, body);
        });
    });

    document.querySelectorAll('[data-delete-custom-quiz]').forEach(button => {
        button.addEventListener('click', async () => {
            if (!window.confirm('Delete this custom quiz?')) return;
            button.disabled = true;
            try {
                const response = await fetch(button.dataset.deleteUrl, {
                    method: 'DELETE',
                    headers: { 'RequestVerificationToken': tokenInput?.value ?? '' }
                });
                if (!response.ok) throw new Error();
                button.closest('[data-custom-quiz-card]')?.remove();
            } catch {
                setMessage('Could not delete the custom quiz. Try again.', 'error');
                button.disabled = false;
            }
        });
    });
})();

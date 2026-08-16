export function problemMessages(problem) {
    const messages = [];
    if (problem?.errors && typeof problem.errors === 'object') {
        Object.entries(problem.errors).forEach(([path, values]) => {
            const entries = Array.isArray(values) ? values : [values];
            entries.filter(Boolean).forEach(value => messages.push(`${path}: ${value}`));
        });
    }
    if (messages.length === 0 && (problem?.error || problem?.detail || problem?.title)) {
        messages.push(problem.error || problem.detail || problem.title);
    }
    return messages.length > 0 ? messages : ['The request could not be completed.'];
}

export function buildExternalRepairPrompt(json, messages) {
    return `Repair this into Glosify version 1 quiz-import JSON. Return only the complete JSON object. Preserve all usable learning content and translations; do not invent ids, visibility, target_language, or custom quizzes.\n\nValidation errors:\n${messages.join('\n')}\n\nJSON to repair:\n${json}`;
}

export function previewMatches(canonicalJson, currentJson) {
    return typeof canonicalJson === 'string' && canonicalJson === currentJson;
}

export function isRequestTimeout(error) {
    return error?.name === 'AbortError' || error?.name === 'TimeoutError';
}

const previewTimeoutMs = 30_000;
const applyTimeoutMs = 60_000;
const aiRepairTimeoutMs = 195_000;

function initialize() {
    const form = document.querySelector('[data-json-import-form]');
    if (!form) {
        return;
    }

    const input = form.querySelector('[data-json-import-text]');
    const status = form.querySelector('[data-json-import-status]');
    const errors = form.querySelector('[data-json-import-errors]');
    const preview = form.querySelector('[data-json-import-preview]');
    const totals = form.querySelector('[data-json-import-totals]');
    const tree = form.querySelector('[data-json-import-tree]');
    const warnings = form.querySelector('[data-json-import-warnings]');
    const success = form.querySelector('[data-json-import-success]');
    const size = form.querySelector('[data-json-import-size]');
    const previewButton = form.querySelector('[data-json-import-preview-button]');
    const applyButton = form.querySelector('[data-json-import-apply]');
    const aiRepairButton = form.querySelector('[data-json-import-ai-repair]');
    const repairPromptButton = form.querySelector('[data-copy-json-repair-prompt]');
    const generationPrompt = document.querySelector('[data-json-import-generation-prompt]');
    const generationCopyButton = document.querySelector('[data-copy-json-import-prompt]');
    const token = form.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    const parentCollectionId = form.querySelector('input[name="ParentCollectionId"]')?.value || '';
    let previewCanonicalJson = null;
    let lastErrorMessages = [];

    const setHidden = (element, hidden) => {
        if (element) {
            element.hidden = hidden;
        }
    };

    const setStatus = (message) => {
        status.textContent = message;
        setHidden(status, !message);
    };

    const updateSize = () => {
        const bytes = new TextEncoder().encode(input.value).length;
        size.textContent = `${Math.ceil(bytes / 1024)} / 64 KiB`;
        size.classList.toggle('is-over-limit', bytes > 64 * 1024);
    };

    const invalidatePreview = () => {
        previewCanonicalJson = null;
        setHidden(preview, true);
        setHidden(applyButton, true);
        setHidden(success, true);
        setHidden(errors, true);
        setHidden(aiRepairButton, true);
        setHidden(repairPromptButton, true);
        setStatus('');
    };

    const setBusy = (busy, message = '') => {
        [previewButton, applyButton, aiRepairButton, repairPromptButton]
            .filter(Boolean)
            .forEach(button => { button.disabled = busy; });
        if (message) {
            setStatus(message);
        }
    };

    const request = async (url, timeoutMs) => {
        const body = new FormData();
        body.set('Json', input.value);
        body.set('ParentCollectionId', parentCollectionId);
        body.set('__RequestVerificationToken', token);
        const controller = new AbortController();
        const timeout = window.setTimeout(() => controller.abort(), timeoutMs);
        let response;
        try {
            response = await fetch(url, {
                method: 'POST',
                body,
                headers: { 'X-Requested-With': 'XMLHttpRequest' },
                signal: controller.signal
            });
        } finally {
            window.clearTimeout(timeout);
        }
        let payload = null;
        try {
            payload = await response.json();
        } catch {
            payload = { detail: response.status === 413 ? 'The JSON request is too large.' : 'The server returned an unreadable response.' };
        }
        return { response, payload };
    };

    const addQuiz = (list, quiz) => {
        const item = document.createElement('li');
        const name = document.createElement('strong');
        name.textContent = quiz.name;
        const detail = document.createElement('small');
        detail.textContent = `${quiz.sourceLanguage} → ${quiz.targetLanguage} · ${quiz.wordCount} words · ${quiz.sentenceCount} sentences`;
        item.append(name, detail);
        list.append(item);
    };

    const addCollection = (list, collection) => {
        const item = document.createElement('li');
        const name = document.createElement('strong');
        name.textContent = collection.name;
        const detail = document.createElement('small');
        detail.textContent = `Collection · ${collection.quizzes.length} direct quizzes · ${collection.collections.length} nested collections`;
        item.append(name, detail);
        if (collection.quizzes.length > 0 || collection.collections.length > 0) {
            const children = document.createElement('ul');
            collection.quizzes.forEach(quiz => addQuiz(children, quiz));
            collection.collections.forEach(child => addCollection(children, child));
            item.append(children);
        }
        list.append(item);
    };

    const renderPreview = (payload, repairedByAi = false) => {
        input.value = payload.canonicalJson;
        updateSize();
        previewCanonicalJson = payload.canonicalJson;
        lastErrorMessages = [];
        setHidden(errors, true);
        setHidden(aiRepairButton, true);
        setHidden(repairPromptButton, true);
        setHidden(success, true);

        totals.replaceChildren();
        const totalValues = [
            [payload.totals.collectionCount, 'collections'],
            [payload.totals.quizCount, 'quizzes'],
            [payload.totals.wordCount, 'words'],
            [payload.totals.sentenceCount, 'sentences']
        ];
        totalValues.forEach(([value, label]) => {
            const pill = document.createElement('span');
            pill.className = 'json-import-total';
            pill.textContent = `${value} ${label}`;
            totals.append(pill);
        });

        const root = document.createElement('ul');
        payload.quizzes.forEach(quiz => addQuiz(root, quiz));
        payload.collections.forEach(collection => addCollection(root, collection));
        tree.replaceChildren(root);

        if (payload.warnings.length > 0) {
            const title = document.createElement('strong');
            title.textContent = 'Review these automatic removals:';
            const list = document.createElement('ul');
            payload.warnings.forEach(message => {
                const item = document.createElement('li');
                item.textContent = message;
                list.append(item);
            });
            warnings.replaceChildren(title, list);
            setHidden(warnings, false);
        } else {
            warnings.replaceChildren();
            setHidden(warnings, true);
        }

        setHidden(preview, false);
        setHidden(applyButton, false);
        setHidden(previewButton, true);
        const message = repairedByAi
            ? 'AI repair produced a valid preview. Nothing has been imported yet.'
            : payload.wasAutoRepaired
                ? 'Free repair normalized wrappers, comments, or trailing commas. Review the canonical JSON below.'
                : 'JSON is valid. Review the exact hierarchy before importing.';
        setStatus(message);
    };

    const renderProblem = (problem, statusCode) => {
        if (typeof problem?.canonicalJson === 'string') {
            input.value = problem.canonicalJson;
            updateSize();
        }
        previewCanonicalJson = null;
        setHidden(preview, true);
        setHidden(applyButton, true);
        setHidden(previewButton, false);
        setHidden(success, true);
        lastErrorMessages = problemMessages(problem);
        const title = document.createElement('strong');
        title.textContent = 'The import needs attention:';
        const list = document.createElement('ul');
        lastErrorMessages.forEach(message => {
            const item = document.createElement('li');
            item.textContent = message;
            list.append(item);
        });
        errors.replaceChildren(title, list);
        setHidden(errors, false);
        const isRepairable = statusCode === 400 && form.dataset.freestyle !== 'true';
        setHidden(aiRepairButton, !isRepairable);
        setHidden(repairPromptButton, form.dataset.freestyle === 'true' || !(isRepairable || statusCode === 409));
        setStatus(problem?.canonicalJson
            ? 'Free repair normalized the JSON, but schema or content errors remain.'
            : 'No content was created. Fix the errors or use one of the repair options.');
    };

    const previewJson = async () => {
        if (!input.value.trim()) {
            renderProblem({ errors: { '$.json': ['Paste a JSON import document.'] } }, 400);
            return;
        }
        setBusy(true, 'Validating and building the free preview…');
        try {
            const { response, payload } = await request(form.dataset.previewUrl, previewTimeoutMs);
            response.ok ? renderPreview(payload) : renderProblem(payload, response.status);
        } catch (error) {
            renderProblem({ detail: isRequestTimeout(error)
                ? 'The preview timed out. No content was created; try again.'
                : 'Could not reach Glosify. Check your connection and try again.' }, 0);
        } finally {
            setBusy(false);
        }
    };

    const repairWithAi = async () => {
        setBusy(true, 'Repairing with Glosify AI. This uses credits…');
        try {
            const { response, payload } = await request(form.dataset.repairUrl, aiRepairTimeoutMs);
            response.ok ? renderPreview(payload, true) : renderProblem(payload, response.status);
        } catch (error) {
            renderProblem({ detail: isRequestTimeout(error)
                ? 'AI repair timed out. Nothing was imported; try again.'
                : 'Could not reach the AI repair service. Try again.' }, 0);
        } finally {
            setBusy(false);
        }
    };

    const applyImport = async () => {
        if (!previewMatches(previewCanonicalJson, input.value)) {
            invalidatePreview();
            setStatus('The JSON changed after preview. Preview it again before importing.');
            return;
        }
        setBusy(true, 'Creating every collection, quiz, word, and sentence in one transaction…');
        try {
            const { response, payload } = await request(form.dataset.applyUrl, applyTimeoutMs);
            if (!response.ok) {
                renderProblem(payload, response.status);
                return;
            }
            setHidden(preview, true);
            setHidden(errors, true);
            setHidden(applyButton, true);
            setHidden(previewButton, true);
            success.textContent = `Created ${payload.collectionCount} collections, ${payload.quizCount} quizzes, ${payload.wordCount} words, and ${payload.sentenceCount} sentences. Returning to your library…`;
            setHidden(success, false);
            setStatus('Import complete.');
            window.setTimeout(() => { window.location.assign(payload.redirectUrl); }, 1100);
        } catch (error) {
            renderProblem({ detail: isRequestTimeout(error)
                ? 'The import request timed out. Reload the library and check whether content was created before trying again.'
                : 'The import could not be completed. Nothing was created.' }, 0);
        } finally {
            setBusy(false);
        }
    };

    const copyText = async (text, message) => {
        try {
            await navigator.clipboard.writeText(text);
        } catch {
            const helper = document.createElement('textarea');
            helper.value = text;
            helper.setAttribute('readonly', '');
            helper.style.position = 'fixed';
            helper.style.opacity = '0';
            document.body.append(helper);
            helper.select();
            document.execCommand('copy');
            helper.remove();
        }
        setStatus(message);
    };

    input.addEventListener('input', () => {
        updateSize();
        invalidatePreview();
        setHidden(previewButton, false);
    });
    previewButton.addEventListener('click', previewJson);
    aiRepairButton.addEventListener('click', repairWithAi);
    applyButton.addEventListener('click', applyImport);
    generationCopyButton?.addEventListener('click', () => copyText(
        generationPrompt.value,
        'AI instructions copied. Add your topic or content request before sending them.'));
    repairPromptButton.addEventListener('click', () => copyText(
        buildExternalRepairPrompt(input.value, lastErrorMessages),
        'Repair prompt copied. This option uses your external AI, not Glosify credits.'));
    updateSize();
}

if (typeof document !== 'undefined') {
    initialize();
}

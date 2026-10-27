import { requiredElement } from '../shared/dom.js';
import { problemMessages, buildExternalRepairPrompt, previewMatches, isRequestTimeout, type ImportProblem } from './json-import-state.js';
import { addQuiz, addCollection, type ImportPreview, type ImportTotals } from './json-import-tree.js';

const previewTimeoutMs = 30_000;
const applyTimeoutMs = 60_000;
const aiRepairTimeoutMs = 195_000;

function initialize() {
    const form = document.querySelector<HTMLFormElement>('[data-json-import-form]');
    if (!form) {
        return;
    }

    const input = requiredElement<HTMLTextAreaElement>(form, '[data-json-import-text]');
    const status = requiredElement<HTMLElement>(form, '[data-json-import-status]');
    const errors = requiredElement<HTMLElement>(form, '[data-json-import-errors]');
    const preview = requiredElement<HTMLElement>(form, '[data-json-import-preview]');
    const totals = requiredElement<HTMLElement>(form, '[data-json-import-totals]');
    const tree = requiredElement<HTMLElement>(form, '[data-json-import-tree]');
    const warnings = requiredElement<HTMLElement>(form, '[data-json-import-warnings]');
    const success = requiredElement<HTMLElement>(form, '[data-json-import-success]');
    const size = requiredElement<HTMLElement>(form, '[data-json-import-size]');
    const previewButton = requiredElement<HTMLButtonElement>(form, '[data-json-import-preview-button]');
    const applyButton = requiredElement<HTMLButtonElement>(form, '[data-json-import-apply]');
    const aiRepairButton = requiredElement<HTMLButtonElement>(form, '[data-json-import-ai-repair]');
    const repairPromptButton = requiredElement<HTMLButtonElement>(form, '[data-copy-json-repair-prompt]');
    const generationPrompt = document.querySelector<HTMLTextAreaElement>('[data-json-import-generation-prompt]');
    const generationCopyButton = document.querySelector('[data-copy-json-import-prompt]');
    const token = form.querySelector<HTMLInputElement>('input[name="__RequestVerificationToken"]')?.value || '';
    const parentCollectionId = form.querySelector<HTMLInputElement>('input[name="ParentCollectionId"]')?.value || '';
    let previewCanonicalJson: string | null = null;
    let lastErrorMessages: string[] = [];
    let inputRevision = 0;

    const setHidden = (element: HTMLElement | null, hidden: boolean) => {
        if (element) {
            element.hidden = hidden;
        }
    };

    const setStatus = (message: string) => {
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

    const setBusy = (busy: boolean, message = '', lockInput = false) => {
        [previewButton, applyButton, aiRepairButton, repairPromptButton]
            .filter(Boolean)
            .forEach(button => { button.disabled = busy; });
        if (lockInput) {
            input.disabled = busy;
        }
        if (message) {
            setStatus(message);
        }
    };

    const request = async (url: string | undefined, timeoutMs: number, json: string) => {
        if (!url) throw new Error('Import endpoint is missing.');
        const body = new FormData();
        body.set('Json', json);
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
        let payload: unknown = null;
        try {
            payload = await response.json();
        } catch {
            payload = { detail: response.status === 413 ? 'The JSON request is too large.' : 'The server returned an unreadable response.' };
        }
        return { response, payload };
    };

    const renderStaleResponse = (repairedByAi = false) => {
        invalidatePreview();
        setHidden(previewButton, false);
        setStatus(repairedByAi
            ? 'The JSON changed while AI repair was running. Your edits were kept and the repaired response was discarded; the request may still have used credits.'
            : 'The JSON changed while preview was running. Your edits were kept and the older response was discarded; preview it again.');
    };

    const renderPreview = (payload: ImportPreview, repairedByAi = false) => {
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

    const renderProblem = (problem: ImportProblem | null, statusCode: number) => {
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
        const revision = inputRevision;
        const json = input.value;
        setBusy(true, 'Validating and building the free preview…');
        try {
            const { response, payload } = await request(form.dataset.previewUrl, previewTimeoutMs, json);
            if (revision !== inputRevision) {
                renderStaleResponse();
                return;
            }
            if (response.ok) renderPreview(payload as ImportPreview);
            else renderProblem(payload as ImportProblem, response.status);
        } catch (error) {
            if (revision !== inputRevision) {
                renderStaleResponse();
                return;
            }
            renderProblem({ detail: isRequestTimeout(error)
                ? 'The preview timed out. No content was created; try again.'
                : 'Could not reach GlobeGlotter. Check your connection and try again.' }, 0);
        } finally {
            setBusy(false);
        }
    };

    const repairWithAi = async () => {
        const revision = inputRevision;
        const json = input.value;
        setBusy(true, 'Repairing with GlobeGlotter AI. This uses credits…');
        try {
            const { response, payload } = await request(form.dataset.repairUrl, aiRepairTimeoutMs, json);
            if (revision !== inputRevision) {
                renderStaleResponse(true);
                return;
            }
            if (response.ok) renderPreview(payload as ImportPreview, true);
            else renderProblem(payload as ImportProblem, response.status);
        } catch (error) {
            if (revision !== inputRevision) {
                renderStaleResponse(true);
                return;
            }
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
        const json = input.value;
        let completed = false;
        setBusy(true, 'Creating every collection, quiz, word, and sentence in one transaction…', true);
        try {
            const { response, payload } = await request(form.dataset.applyUrl, applyTimeoutMs, json);
            if (!response.ok) {
                renderProblem(payload as ImportProblem, response.status);
                return;
            }
            const result = payload as ImportTotals & { redirectUrl: string };
            completed = true;
            setHidden(preview, true);
            setHidden(errors, true);
            setHidden(applyButton, true);
            setHidden(previewButton, true);
            success.textContent = `Created ${result.collectionCount} collections, ${result.quizCount} quizzes, ${result.wordCount} words, and ${result.sentenceCount} sentences. Returning to your library…`;
            setHidden(success, false);
            setStatus('Import complete.');
            window.setTimeout(() => { window.location.assign(result.redirectUrl); }, 1100);
        } catch (error) {
            renderProblem({ detail: isRequestTimeout(error)
                ? 'The import request timed out. Reload the library and check whether content was created before trying again.'
                : 'The import could not be completed. Nothing was created.' }, 0);
        } finally {
            setBusy(false, '', true);
            input.disabled = completed;
        }
    };

    const copyText = async (text: string, message: string) => {
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
        inputRevision++;
        updateSize();
        invalidatePreview();
        setHidden(previewButton, false);
    });
    previewButton.addEventListener('click', previewJson);
    aiRepairButton.addEventListener('click', repairWithAi);
    applyButton.addEventListener('click', applyImport);
    generationCopyButton?.addEventListener('click', () => copyText(
        generationPrompt?.value || '',
        'AI instructions copied. Add your topic or content request before sending them.'));
    repairPromptButton.addEventListener('click', () => copyText(
        buildExternalRepairPrompt(input.value, lastErrorMessages),
        'Repair prompt copied. This option uses your external AI, not GlobeGlotter credits.'));
    updateSize();
}

if (typeof document !== 'undefined') {
    initialize();
    window.addEventListener('learning:updated', initialize);
}

export interface QuizOption { id: string; name: string; sourceLanguage: string; targetLanguage: string; }
interface MaterialOption { id: string; title: string; }
export interface ContextOptions { quizzes?: QuizOption[]; books?: MaterialOption[]; transcripts?: MaterialOption[]; }
const isFreestyle = (language: string | null | undefined) =>
    String(language || '').trim().toLowerCase() === 'freestyle';

export const quizOptionText = (quiz: QuizOption) => isFreestyle(quiz.targetLanguage)
    ? `${quiz.name} (Freestyle)`
    : `${quiz.name} (${quiz.sourceLanguage} → ${quiz.targetLanguage})`;

export const currentBookPageContext = ({
    pageDocumentId,
    currentPage,
    selectedMaterialKind,
    selectedMaterialId,
}: { pageDocumentId?: string | null; currentPage?: string | number; selectedMaterialKind?: string | null; selectedMaterialId?: string | null }) => {
    if (!pageDocumentId
        || selectedMaterialKind !== 'book'
        || selectedMaterialId !== pageDocumentId) {
        return null;
    }

    const pageNumber = Number(currentPage);
    return {
        documentId: pageDocumentId,
        pageNumber: Number.isFinite(pageNumber) && pageNumber > 0 ? pageNumber : 1,
    };
};

export const matchesOwnedStatus = (currentMessage: string, ownedMessage: string | null) =>
    Boolean(ownedMessage) && currentMessage === ownedMessage;

const appendOption = (parent: HTMLSelectElement | HTMLOptGroupElement, value: string, label: string, contextLabel = label) => {
    const option = parent.ownerDocument.createElement('option');
    option.value = value;
    option.textContent = label;
    option.dataset.contextLabel = contextLabel;
    parent.appendChild(option);
};

const clearLoadedOptions = (selector: HTMLSelectElement) => {
    while (selector.children.length > 1) {
        selector.lastElementChild!.remove();
    }
};

export const selectedOptionContextLabel = (selector: HTMLSelectElement | null, value: string | null | undefined, fallback: string) => {
    const option = value
        ? Array.from(selector?.options ?? []).find(candidate => candidate.value === value)
        : null;
    return option?.dataset?.contextLabel
        || option?.textContent?.trim()
        || fallback;
};

const appendMaterialGroup = (selector: HTMLSelectElement, label: string, kind: string, materials?: MaterialOption[]) => {
    if (!materials?.length) {
        return;
    }

    const group = selector.ownerDocument.createElement('optgroup');
    group.label = label;
    for (const material of materials) {
        appendOption(group, `${kind}:${material.id}`, material.title);
    }
    selector.appendChild(group);
};

export const populateContextOptions = ({
    quizSelector,
    materialSelector,
    options,
    selectedQuizId,
    selectedMaterialKind,
    selectedMaterialId,
    fallbackContextLabel,
}: { quizSelector: HTMLSelectElement; materialSelector: HTMLSelectElement; options: ContextOptions; selectedQuizId?: string | null; selectedMaterialKind?: string | null; selectedMaterialId?: string | null; fallbackContextLabel?: string | null }) => {
    const selectedQuizLabel = selectedOptionContextLabel(
        quizSelector,
        selectedQuizId,
        fallbackContextLabel || 'Selected quiz');
    clearLoadedOptions(quizSelector);
    for (const quiz of options.quizzes ?? []) {
        appendOption(quizSelector, quiz.id, quizOptionText(quiz), quiz.name);
    }

    if (selectedQuizId
        && !Array.from(quizSelector.options).some(option => option.value === selectedQuizId)) {
        appendOption(
            quizSelector,
            selectedQuizId,
            selectedQuizLabel);
    }
    quizSelector.value = selectedQuizId || '';

    const selectedMaterial = selectedMaterialKind && selectedMaterialId
        ? `${selectedMaterialKind}:${selectedMaterialId}`
        : '';
    const selectedMaterialLabel = selectedOptionContextLabel(
        materialSelector,
        selectedMaterial,
        fallbackContextLabel || 'Selected material');
    clearLoadedOptions(materialSelector);
    appendMaterialGroup(
        materialSelector,
        materialSelector.dataset.booksLabel || 'Books',
        'book',
        options.books);
    appendMaterialGroup(
        materialSelector,
        materialSelector.dataset.transcriptsLabel || 'Transcripts',
        'transcript',
        options.transcripts);

    if (selectedMaterial
        && !Array.from(materialSelector.options).some(option => option.value === selectedMaterial)) {
        appendOption(
            materialSelector,
            selectedMaterial,
            selectedMaterialLabel);
    }
    materialSelector.value = selectedMaterial;
};

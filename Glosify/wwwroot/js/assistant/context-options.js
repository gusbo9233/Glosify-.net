const isFreestyle = (language) =>
    String(language || '').trim().toLowerCase() === 'freestyle';

export const quizOptionText = (quiz) => isFreestyle(quiz.targetLanguage)
    ? `${quiz.name} (Freestyle)`
    : `${quiz.name} (${quiz.sourceLanguage} → ${quiz.targetLanguage})`;

export const currentBookPageContext = ({
    pageDocumentId,
    currentPage,
    selectedMaterialKind,
    selectedMaterialId,
}) => {
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

export const matchesOwnedStatus = (currentMessage, ownedMessage) =>
    Boolean(ownedMessage) && currentMessage === ownedMessage;

const appendOption = (parent, value, label, contextLabel = label) => {
    const option = parent.ownerDocument.createElement('option');
    option.value = value;
    option.textContent = label;
    option.dataset.contextLabel = contextLabel;
    parent.appendChild(option);
};

const clearLoadedOptions = (selector) => {
    while (selector.children.length > 1) {
        selector.lastElementChild.remove();
    }
};

export const selectedOptionContextLabel = (selector, value, fallback) => {
    const option = value
        ? Array.from(selector?.options ?? []).find(candidate => candidate.value === value)
        : null;
    return option?.dataset?.contextLabel
        || option?.textContent?.trim()
        || fallback;
};

const appendMaterialGroup = (selector, label, kind, materials) => {
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
}) => {
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

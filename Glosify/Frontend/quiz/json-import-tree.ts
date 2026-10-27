export interface ImportQuiz { name: string; sourceLanguage: string; targetLanguage: string; wordCount: number; sentenceCount: number; }
export interface ImportCollection { name: string; quizzes: ImportQuiz[]; collections: ImportCollection[]; }
export interface ImportTotals { collectionCount: number; quizCount: number; wordCount: number; sentenceCount: number; }
export interface ImportPreview { canonicalJson: string; totals: ImportTotals; quizzes: ImportQuiz[]; collections: ImportCollection[]; warnings: string[]; wasAutoRepaired: boolean; }

export const addQuiz = (list: HTMLElement, quiz: ImportQuiz) => {
    const item = document.createElement('li');
    const name = document.createElement('strong');
    name.textContent = quiz.name;
    const detail = document.createElement('small');
    detail.textContent = `${quiz.sourceLanguage} → ${quiz.targetLanguage} · ${quiz.wordCount} words · ${quiz.sentenceCount} sentences`;
    item.append(name, detail);
    list.append(item);
};

export const addCollection = (list: HTMLElement, collection: ImportCollection) => {
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

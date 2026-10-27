import { requiredElement } from '../shared/dom.js';

(() => {
    const initialize = () => {
        const library = document.querySelector<HTMLElement>('[data-quiz-library]');
        if (!library) {
            return;
        }

        const moveUrl = library.dataset.moveQuizUrl;
        const token = document.querySelector<HTMLInputElement>('input[name="__RequestVerificationToken"]')?.value;
        const quizCards = library.querySelectorAll<HTMLElement>('[data-quiz-card]');
        const dropTargets = library.querySelectorAll<HTMLElement>('[data-collection-drop-target]');
        const pageMessage = library.querySelector<HTMLElement>('[data-quiz-library-message]');
        const movingQuizIds = new Set<string>();

        const showMoveError = (message?: string | null) => {
            if (!pageMessage) {
                return;
            }

            pageMessage.textContent = message || 'Could not move that quiz.';
            pageMessage.hidden = false;
        };

        const responseError = async (response: Response): Promise<string | null> => {
            try {
                const payload = await response.json();
                return typeof payload.error === 'string' ? payload.error : typeof payload.detail === 'string' ? payload.detail : null;
            } catch {
                return null;
            }
        };

        const createDragImage = (card: HTMLElement) => {
            const title = card.querySelector('.quiz-name')?.textContent?.trim() || 'Quiz';
            const meta = card.querySelector('.quiz-languages')?.textContent?.trim() || '';
            const dragImage = document.createElement('div');
            dragImage.className = 'quiz-drag-preview';
            dragImage.innerHTML = `
                <span class="material-symbols-outlined" aria-hidden="true">quiz</span>
                <span class="quiz-drag-preview-text">
                    <strong></strong>
                    <small></small>
                </span>
            `;
            requiredElement(dragImage, 'strong').textContent = title;
            requiredElement(dragImage, 'small').textContent = meta;
            document.body.appendChild(dragImage);
            return dragImage;
        };

        quizCards.forEach((card) => {
            card.addEventListener('dragstart', (event) => {
                if (!event.dataTransfer) return;
                event.dataTransfer.effectAllowed = 'move';
                event.dataTransfer.setData('text/plain', card.dataset.quizId || '');
                const dragImage = createDragImage(card);
                event.dataTransfer.setDragImage(dragImage, 24, 24);
                window.setTimeout(() => dragImage.remove(), 0);
                card.classList.add('is-dragging');
                library.classList.add('is-dragging-quiz');
            });

            card.addEventListener('dragend', () => {
                card.classList.remove('is-dragging');
                library.classList.remove('is-dragging-quiz');
                dropTargets.forEach((target) => target.classList.remove('is-drop-target'));
            });
        });

        dropTargets.forEach((target) => {
            target.addEventListener('dragover', (event) => {
                if (!event.dataTransfer) return;
                if (!Array.from(event.dataTransfer.types).includes('text/plain')) {
                    return;
                }

                event.preventDefault();
                event.dataTransfer.dropEffect = 'move';
                target.classList.add('is-drop-target');
            });

            target.addEventListener('dragleave', () => {
                target.classList.remove('is-drop-target');
            });

            target.addEventListener('drop', async (event) => {
                if (!event.dataTransfer) return;
                event.preventDefault();
                target.classList.remove('is-drop-target');

                const quizId = event.dataTransfer.getData('text/plain');
                const collectionId = target.dataset.collectionId;
                if (!quizId || !collectionId || !moveUrl) {
                    return;
                }

                if (target.classList.contains('is-drop-saving') || movingQuizIds.has(quizId)) {
                    return;
                }

                const formData = new FormData();
                formData.append('quizId', quizId);
                formData.append('collectionId', collectionId);
                if (token) {
                    formData.append('__RequestVerificationToken', token);
                }

                target.classList.add('is-drop-saving');
                movingQuizIds.add(quizId);
                library.setAttribute('aria-busy', 'true');

                try {
                    const response = await fetch(moveUrl, {
                        method: 'POST',
                        body: formData,
                        headers: {
                            'X-Requested-With': 'XMLHttpRequest'
                        }
                    });

                    if (!response.ok) {
                        showMoveError(await responseError(response));
                        return;
                    }

                    window.location.reload();
                } catch {
                    showMoveError();
                } finally {
                    target.classList.remove('is-drop-saving');
                    movingQuizIds.delete(quizId);
                    library.setAttribute('aria-busy', String(movingQuizIds.size > 0));
                }
            });
        });
    };
    initialize();
    window.addEventListener('learning:updated', initialize);
})();

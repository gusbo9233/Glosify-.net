(() => {
    const library = document.querySelector('[data-quiz-library]');
    if (!library) {
        return;
    }

    const moveUrl = library.dataset.moveQuizUrl;
    const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value;
    const quizCards = library.querySelectorAll('[data-quiz-card]');
    const dropTargets = library.querySelectorAll('[data-collection-drop-target]');

    const createDragImage = (card) => {
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
        dragImage.querySelector('strong').textContent = title;
        dragImage.querySelector('small').textContent = meta;
        document.body.appendChild(dragImage);
        return dragImage;
    };

    quizCards.forEach((card) => {
        card.addEventListener('dragstart', (event) => {
            event.dataTransfer.effectAllowed = 'move';
            event.dataTransfer.setData('text/plain', card.dataset.quizId);
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
            event.preventDefault();
            target.classList.remove('is-drop-target');

            const quizId = event.dataTransfer.getData('text/plain');
            const collectionId = target.dataset.collectionId;
            if (!quizId || !collectionId || !moveUrl) {
                return;
            }

            const formData = new FormData();
            formData.append('quizId', quizId);
            formData.append('collectionId', collectionId);
            if (token) {
                formData.append('__RequestVerificationToken', token);
            }

            target.classList.add('is-drop-saving');

            const response = await fetch(moveUrl, {
                method: 'POST',
                body: formData,
                headers: {
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });

            if (response.ok) {
                window.location.reload();
                return;
            }

            target.classList.remove('is-drop-saving');
            const pageMessage = document.querySelector('.page-message');
            if (pageMessage) {
                pageMessage.textContent = 'Could not move that quiz.';
            }
        });
    });
})();
    

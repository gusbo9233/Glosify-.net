(() => {
    const tabs = document.querySelectorAll('[data-tab]');
    const panels = {
        words: document.getElementById('wordsPanel'),
        sentences: document.getElementById('sentencesPanel')
    };
    const title = document.getElementById('collectionTitle');
    const subtitle = document.getElementById('collectionSubtitle');

    tabs.forEach(tab => {
        tab.addEventListener('click', () => {
            const activeTab = tab.dataset.tab;
            tabs.forEach(item => {
                const isActive = item === tab;
                item.classList.toggle('active', isActive);
                item.setAttribute('aria-selected', isActive.toString());
            });

            Object.entries(panels).forEach(([name, panel]) => {
                if (panel) {
                    panel.hidden = name !== activeTab;
                }
            });

            const activePanel = panels[activeTab];
            if (activePanel && title && subtitle) {
                title.textContent = activePanel.dataset.title || "";
                subtitle.textContent = activePanel.dataset.subtitle || "";
            }
        });
    });
})();

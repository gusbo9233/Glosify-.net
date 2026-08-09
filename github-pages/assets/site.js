const menuButton = document.querySelector('[data-menu-button]');
const nav = document.querySelector('[data-nav]');

if (menuButton && nav) {
  menuButton.addEventListener('click', () => {
    const isOpen = menuButton.getAttribute('aria-expanded') === 'true';
    menuButton.setAttribute('aria-expanded', String(!isOpen));
    nav.toggleAttribute('data-open', !isOpen);
  });
}

document.querySelectorAll('[data-copy]').forEach((button) => {
  button.addEventListener('click', async () => {
    const text = button.getAttribute('data-copy');
    if (!text) return;

    try {
      await navigator.clipboard.writeText(text);
      const original = button.textContent;
      button.textContent = 'Copied';
      window.setTimeout(() => { button.textContent = original; }, 1400);
    } catch {
      button.textContent = 'Select & copy';
    }
  });
});

(() => {
  const status = document.querySelector(".saved-translation-copy-status");
  for (const button of document.querySelectorAll("[data-copy-target]")) {
    button.addEventListener("click", async () => {
      const target = document.getElementById(button.dataset.copyTarget);
      if (!target) return;
      try {
        await navigator.clipboard.writeText(target.textContent ?? "");
        if (status) status.textContent = status.dataset.copySuccess ?? "Copied.";
      } catch {
        if (status) status.textContent = status.dataset.copyFailed
          ?? "Could not copy. Select the text and copy it manually.";
      }
    });
  }
})();

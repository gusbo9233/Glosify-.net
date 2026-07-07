(function () {
    "use strict";

    const copyButton = document.querySelector("[data-copy-join-code]");
    if (copyButton) {
        copyButton.addEventListener("click", async () => {
            const host = copyButton.closest("[data-join-code]");
            const code = host ? host.getAttribute("data-join-code") : "";
            if (!code) {
                return;
            }

            try {
                await navigator.clipboard.writeText(code);
                const original = copyButton.textContent;
                copyButton.textContent = "Copied!";
                setTimeout(() => { copyButton.textContent = original; }, 1500);
            } catch {
                // Clipboard unavailable (e.g. insecure context); the code is visible anyway.
            }
        });
    }

    document.querySelectorAll("form[data-confirm]").forEach((form) => {
        form.addEventListener("submit", (event) => {
            const message = form.getAttribute("data-confirm") || "Are you sure?";
            if (!window.confirm(message)) {
                event.preventDefault();
            }
        });
    });
})();

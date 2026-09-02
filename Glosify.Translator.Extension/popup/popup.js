const elements = {
  loading: document.querySelector("#loading"),
  signedOut: document.querySelector("#signed-out"),
  signedIn: document.querySelector("#signed-in"),
  connect: document.querySelector("#connect"),
  signOut: document.querySelector("#sign-out"),
  start: document.querySelector("#start"),
  saved: document.querySelector("#saved"),
  email: document.querySelector("#email"),
  credits: document.querySelector("#credits"),
  error: document.querySelector("#error"),
};
let currentState = null;
let busy = false;

elements.connect.addEventListener("click", () => run("popup:sign-in"));
elements.signOut.addEventListener("click", () => run("popup:sign-out"));
elements.start.addEventListener("click", () => run("popup:start"));
elements.saved.addEventListener("click", () => run("popup:open-saved", false));
chrome.runtime.onMessage.addListener(message => {
  if (message?.target === "popup" && message.type === "state:update") {
    currentState = message.state;
    render();
  }
});
void run("popup:get-state", false);

async function run(type, showBusy = true) {
  busy = showBusy;
  render();
  try {
    const response = await chrome.runtime.sendMessage({ type });
    if (!response?.ok) throw new Error(response?.error || "The extension did not respond.");
    currentState = response.result ?? currentState;
    if (type === "popup:start") window.close();
  } catch (error) {
    currentState ??= { signedIn: false };
    currentState.error = error?.message || "Unexpected extension error.";
  } finally {
    busy = false;
    render();
  }
}

function render() {
  elements.loading.classList.toggle("hidden", Boolean(currentState));
  if (!currentState) return;
  elements.signedOut.classList.toggle("hidden", currentState.signedIn);
  elements.signedIn.classList.toggle("hidden", !currentState.signedIn);
  elements.connect.disabled = busy;
  elements.error.textContent = currentState.error ?? "";
  elements.error.classList.toggle("hidden", !currentState.error);
  if (!currentState.signedIn) return;
  elements.email.textContent = currentState.email ?? "Glosify account";
  elements.credits.textContent = String(currentState.availableCredits ?? 0);
  elements.start.disabled = busy || !currentState.catalog;
  elements.signOut.disabled = busy;
}

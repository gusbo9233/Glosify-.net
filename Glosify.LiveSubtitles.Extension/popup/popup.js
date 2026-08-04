const elements = {
  loading: document.querySelector("#loading"),
  signedOut: document.querySelector("#signed-out"),
  signedIn: document.querySelector("#signed-in"),
  connect: document.querySelector("#connect"),
  signOut: document.querySelector("#sign-out"),
  email: document.querySelector("#email"),
  credits: document.querySelector("#credits"),
  quizLanguage: document.querySelector("#quiz-language"),
  language: document.querySelector("#language"),
  saveTranscript: document.querySelector("#save-transcript"),
  saveTranscriptHelp: document.querySelector("#save-transcript-help"),
  price: document.querySelector("#price"),
  start: document.querySelector("#start"),
  stop: document.querySelector("#stop"),
  viewTranscripts: document.querySelector("#view-transcripts"),
  sessionStatus: document.querySelector("#session-status"),
  notice: document.querySelector("#notice"),
  error: document.querySelector("#error"),
};

let currentState = null;
let busy = false;

elements.connect.addEventListener("click", () => run("popup:sign-in"));
elements.signOut.addEventListener("click", () => run("popup:sign-out"));
elements.start.addEventListener("click", () => run("popup:start"));
elements.stop.addEventListener("click", () => run("popup:stop"));
elements.language.addEventListener("change", () => run("popup:set-target", {
  targetLanguage: elements.language.value,
}));
elements.quizLanguage.addEventListener("change", () => run("popup:set-quiz-language", {
  code: elements.quizLanguage.value,
}));
elements.saveTranscript.addEventListener("change", () => run("popup:set-save-transcript", {
  enabled: elements.saveTranscript.checked,
}));
elements.viewTranscripts.addEventListener("click", () => run("popup:open-transcripts", {}, false));

chrome.runtime.onMessage.addListener(message => {
  if (message?.target === "popup" && message.type === "state:update") {
    currentState = message.state;
    render();
  }
});

void run("popup:get-state", {}, false);

async function run(type, extra = {}, showBusy = true) {
  if (showBusy) {
    busy = true;
    render();
  }
  try {
    const response = await chrome.runtime.sendMessage({ type, ...extra });
    if (!response?.ok) {
      throw new Error(response?.error || "The extension background process did not respond.");
    }
    if (response.result) {
      currentState = response.result;
    }
  } catch (error) {
    currentState ??= { signedIn: false, status: "disconnected" };
    currentState.error = error?.message || "Unexpected extension error.";
  } finally {
    busy = false;
    render();
  }
}

function render() {
  if (!currentState) {
    elements.loading.classList.remove("hidden");
    return;
  }
  elements.loading.classList.add("hidden");
  elements.signedOut.classList.toggle("hidden", currentState.signedIn);
  elements.signedIn.classList.toggle("hidden", !currentState.signedIn);
  elements.connect.disabled = busy;
  if (!currentState.signedIn) {
    return;
  }

  elements.email.textContent = currentState.email ?? "Glosify account";
  elements.credits.textContent = String(currentState.availableCredits ?? 0);
  const price = currentState.effectiveCreditsPerMinute ?? currentState.catalog?.creditsPerMinute ?? 8;
  elements.price.textContent = `${price} credits/min`;

  const quizLanguages = currentState.catalog?.quizLanguages ?? [];
  const quizSignature = quizLanguages.map(language => language.code).join(",");
  if (elements.quizLanguage.dataset.signature !== quizSignature) {
    const placeholder = document.createElement("option");
    placeholder.value = "";
    placeholder.textContent = "Choose a quiz language…";
    placeholder.disabled = true;
    elements.quizLanguage.replaceChildren(placeholder, ...quizLanguages.map(language => {
      const option = document.createElement("option");
      option.value = language.code;
      option.textContent = language.name;
      return option;
    }));
    elements.quizLanguage.dataset.signature = quizSignature;
  }
  elements.quizLanguage.value = currentState.catalog?.selectedQuizLanguage?.code ?? "";
  elements.quizLanguage.disabled = busy || currentState.active || quizLanguages.length === 0;

  const languages = currentState.catalog?.languages ?? [];
  const signature = languages.map(language => language.code).join(",");
  if (elements.language.dataset.signature !== signature) {
    elements.language.replaceChildren(...languages.map(language => {
      const option = document.createElement("option");
      option.value = language.code;
      option.textContent = language.name;
      return option;
    }));
    elements.language.dataset.signature = signature;
  }
  elements.language.value = currentState.targetLanguage ?? languages[0]?.code ?? "";
  elements.language.disabled = busy || currentState.active || languages.length === 0;

  elements.saveTranscript.checked = Boolean(currentState.saveTranscript);
  elements.saveTranscript.disabled = busy || currentState.active || !currentState.canSaveTranscript;
  elements.saveTranscriptHelp.textContent = currentState.saveTranscriptHelp
    ?? "Stores finalized original-language speech in your private Glosify account for this session only.";

  const canStart = !busy
    && !currentState.active
    && currentState.catalog
    && languages.length > 0
    && currentState.availableCredits >= price;
  elements.start.classList.toggle("hidden", currentState.active);
  elements.stop.classList.toggle("hidden", !currentState.active);
  elements.start.disabled = !canStart;
  elements.stop.disabled = busy;

  const statusText = describeStatus(currentState);
  setMessage(elements.sessionStatus, statusText);
  setMessage(elements.notice, currentState.notice);
  setMessage(elements.error, currentState.error);
}

function describeStatus(state) {
  switch (state.status) {
    case "connecting": return "Connecting tab audio to the Glosify relay…";
    case "subtitling": return `Subtitling · paid minute ${state.currentMinute}`;
    case "reconnecting": return "Reconnecting after the 30-minute session limit…";
    case "insufficient_credits": return "Stopped: insufficient Glosify credits.";
    default: return null;
  }
}

function setMessage(element, text) {
  element.textContent = text ?? "";
  element.classList.toggle("hidden", !text);
}

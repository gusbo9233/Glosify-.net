import { getEffectiveCreditsPerMinute, isTranscriptToggleDisabled } from "../lib/transcript-storage.js";
import { setQuizLanguageVisibility } from "../lib/popup-visibility.js";

const elements = {
  loading: document.querySelector("#loading"),
  signedOut: document.querySelector("#signed-out"),
  signedIn: document.querySelector("#signed-in"),
  connect: document.querySelector("#connect"),
  signOut: document.querySelector("#sign-out"),
  email: document.querySelector("#email"),
  credits: document.querySelector("#credits"),
  quizLanguage: document.querySelector("#quiz-language"),
  quizLanguageGroup: document.querySelector("#quiz-language-group"),
  language: document.querySelector("#language"),
  translationMode: document.querySelector("#translation-mode"),
  sourceLanguage: document.querySelector("#source-language"),
  sourceLanguageGroup: document.querySelector("#source-language-group"),
  transparentSubtitles: document.querySelector("#transparent-subtitles"),
  saveTranscript: document.querySelector("#save-transcript"),
  saveTranscriptHelp: document.querySelector("#save-transcript-help"),
  price: document.querySelector("#price"),
  serviceDisclosure: document.querySelector("#service-disclosure"),
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
elements.translationMode.addEventListener("change", () => run("popup:set-mode", {
  translationMode: elements.translationMode.value,
}));
elements.sourceLanguage.addEventListener("change", () => run("popup:set-source", {
  sourceLanguage: elements.sourceLanguage.value,
}));
elements.quizLanguage.addEventListener("change", () => run("popup:set-quiz-language", {
  code: elements.quizLanguage.value,
}));
elements.transparentSubtitles.addEventListener("change", () => {
  const enabled = elements.transparentSubtitles.checked;
  void run("popup:set-transparent-subtitles", { enabled }, true, {
    transparentSubtitles: enabled,
  });
});
elements.saveTranscript.addEventListener("change", () => {
  const enabled = elements.saveTranscript.checked;
  const catalog = currentState?.catalog;
  void run("popup:set-save-transcript", {
    enabled,
    quizLanguageCode: elements.quizLanguage.value,
  }, true, {
    saveTranscript: enabled,
    effectiveCreditsPerMinute: getEffectiveCreditsPerMinute(
      catalog,
      enabled,
      currentState?.translationMode),
  });
});
elements.viewTranscripts.addEventListener("click", () => run("popup:open-transcripts", {}, false));

chrome.runtime.onMessage.addListener(message => {
  if (message?.target === "popup" && message.type === "state:update") {
    currentState = message.state;
    render();
  }
});

void run("popup:get-state", {}, false);

async function run(type, extra = {}, showBusy = true, optimisticState = null) {
  const previousState = currentState ? { ...currentState } : null;
  if (showBusy) {
    busy = true;
    if (optimisticState && currentState) {
      currentState = { ...currentState, ...optimisticState };
    }
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
    currentState = previousState ?? { signedIn: false, status: "disconnected" };
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
  const price = currentState.effectiveCreditsPerMinute;
  elements.price.textContent = Number.isFinite(price)
    ? `${price} credits/min`
    : "Price unavailable";

  const modes = currentState.catalog?.modes ?? [];
  const modeSignature = modes.map(mode => `${mode.code}:${mode.creditsPerMinute}`).join(",");
  if (elements.translationMode.dataset.signature !== modeSignature) {
    elements.translationMode.replaceChildren(...modes.map(mode => {
      const option = document.createElement("option");
      option.value = mode.code;
      option.textContent = `${mode.name} — ${mode.description}`;
      return option;
    }));
    elements.translationMode.dataset.signature = modeSignature;
  }
  elements.translationMode.value = currentState.translationMode ?? "enhanced";
  elements.translationMode.disabled = busy || currentState.active;

  const usesScribe = currentState.translationMode === "scribe" || currentState.saveTranscript;
  const sourceLanguages = currentState.catalog?.sourceLanguages ?? [];
  const sourceSignature = sourceLanguages.map(language => `${language.code}:${language.name}`).join(",");
  if (elements.sourceLanguage.dataset.signature !== sourceSignature) {
    elements.sourceLanguage.replaceChildren(...sourceLanguages.map(language => {
      const option = document.createElement("option");
      option.value = language.code;
      option.textContent = language.name;
      return option;
    }));
    elements.sourceLanguage.dataset.signature = sourceSignature;
  }
  elements.sourceLanguage.value = currentState.sourceLanguage ?? "auto";
  elements.sourceLanguage.disabled = busy || currentState.active;
  elements.sourceLanguageGroup.classList.toggle("hidden", !usesScribe);

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
  elements.quizLanguage.value = currentState.catalog?.selectedQuizLanguage?.code
    ?? quizLanguages.find(language => language.code === currentState.targetLanguage)?.code
    ?? quizLanguages[0]?.code
    ?? "";
  elements.quizLanguage.disabled = busy || currentState.active || quizLanguages.length === 0;
  setQuizLanguageVisibility(elements.quizLanguageGroup, currentState.saveTranscript);

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

  elements.transparentSubtitles.checked = Boolean(currentState.transparentSubtitles);
  elements.transparentSubtitles.disabled = busy;

  elements.saveTranscript.checked = Boolean(currentState.saveTranscript);
  elements.saveTranscript.disabled = isTranscriptToggleDisabled({
    busy,
    catalog: currentState.catalog,
  });
  elements.saveTranscriptHelp.textContent = currentState.saveTranscriptHelp
    ?? "Optional and off by default. Stores finalized original-language speech in your private Glosify account until you delete the transcript or account.";
  elements.serviceDisclosure.textContent = currentState.translationMode === "scribe"
    ? "When you start, this tab’s audio is streamed through Glosify to ElevenLabs Scribe v2, and finalized phrases are sent to Azure Translator. ElevenLabs may retain standard API logs under its service policy. Glosify does not store tab audio. Each started minute consumes credits."
    : currentState.saveTranscript
      ? "When you start, this tab’s audio is streamed through Glosify to Microsoft Foundry for enhanced live translation and to ElevenLabs Scribe v2 for the saved source transcript. ElevenLabs may retain standard API logs under its service policy. Glosify does not store tab audio. Each started minute consumes credits."
      : "When you start, this tab’s audio is streamed through Glosify to Microsoft Foundry for enhanced live translation. Audio is not stored. Each started minute consumes credits.";

  const canStart = !busy
    && !currentState.active
    && currentState.catalog
    && Number.isFinite(price)
    && price > 0
    && currentState.paidServicesAvailable !== false
    && languages.length > 0
    && (!usesScribe || sourceLanguages.some(language => language.code === currentState.sourceLanguage))
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
    case "budget_exhausted": return `Paid features reopen ${formatReset(state.paidServicesResetAtUtc)}.`;
    default: return null;
  }
}

function formatReset(value) {
  const reset = value ? new Date(value) : null;
  return reset && !Number.isNaN(reset.valueOf())
    ? reset.toLocaleString([], { dateStyle: "medium", timeStyle: "short" })
    : "at the start of next month";
}

function setMessage(element, text) {
  element.textContent = text ?? "";
  element.classList.toggle("hidden", !text);
}

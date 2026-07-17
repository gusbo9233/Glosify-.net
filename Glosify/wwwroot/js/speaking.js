(() => {
    "use strict";

    const root = document.getElementById("speaking-app");
    if (!root) {
        return;
    }

    const pageData = JSON.parse(root.dataset.speakingPage || "{}");
    const avatars = new Map((pageData.avatars || []).map(avatar => [avatar.id, avatar]));
    const elements = {
        avatarChoices: [...root.querySelectorAll("[data-avatar-choice]")],
        scenes: [...root.querySelectorAll("[data-avatar-scene]")],
        stageCard: root.querySelector(".speaking-stage-card"),
        avatarName: document.getElementById("speaking-avatar-name"),
        sceneName: document.getElementById("speaking-scene-name"),
        connectionLabel: document.getElementById("speaking-connection-label"),
        level: document.getElementById("speaking-level"),
        translationToggle: document.getElementById("speaking-translation-toggle"),
        muteToggle: document.getElementById("speaking-mute-toggle"),
        messages: document.getElementById("speaking-messages"),
        textarea: document.getElementById("speaking-message"),
        characterCount: document.getElementById("speaking-character-count"),
        mic: document.getElementById("speaking-mic"),
        micLabel: root.querySelector(".speaking-mic-label"),
        send: document.getElementById("speaking-send"),
        status: document.getElementById("speaking-status"),
        recordingNote: document.getElementById("speaking-recording-note"),
        replayLatest: document.getElementById("speaking-replay-latest"),
        newSession: document.getElementById("speaking-new-session"),
        liveBubble: document.getElementById("speaking-live-bubble"),
        livePolish: document.getElementById("speaking-live-polish"),
        liveEnglish: document.getElementById("speaking-live-english"),
        userTemplate: document.getElementById("speaking-user-message-template"),
        avatarTemplate: document.getElementById("speaking-avatar-message-template")
    };

    const state = {
        avatarId: pageData.defaultAvatarId || "bartender",
        cefrLevel: pageData.defaultCefrLevel || "A2",
        sessionId: null,
        ready: false,
        busy: false,
        listening: false,
        userTurns: 0,
        recordedTranscript: "",
        pronunciation: null,
        token: null,
        latestReply: null,
        activeSynthesizer: null,
        activeAudio: null,
        mouthTimers: [],
        genericMouthTimer: null
    };

    const antiforgeryToken =
        root.querySelector("input[name='__RequestVerificationToken']")?.value || "";
    const visemePoses = [
        "closed", "open", "open", "open", "open", "round", "round", "open",
        "round", "open", "narrow", "narrow", "narrow", "round", "narrow",
        "narrow", "narrow", "closed", "narrow", "narrow", "narrow", "open"
    ];

    function currentAvatar() {
        return avatars.get(state.avatarId) || [...avatars.values()][0];
    }

    function currentScene() {
        return elements.scenes.find(scene => scene.dataset.avatarScene === state.avatarId);
    }

    function setStatus(message, isError = false) {
        elements.status.textContent = message || "";
        elements.status.classList.toggle("is-error", isError);
    }

    function setConnection(message, ready = false) {
        elements.connectionLabel.textContent = message;
        elements.stageCard.classList.toggle("is-ready", ready);
    }

    function setBusy(busy) {
        state.busy = busy;
        elements.send.disabled = busy || !state.ready;
        elements.mic.disabled = busy || !state.ready || state.listening;
        elements.level.disabled = busy;
        elements.avatarChoices.forEach(choice => {
            choice.disabled = busy;
        });
    }

    function updateCharacterCount() {
        elements.characterCount.textContent = `${elements.textarea.value.length} / 800`;
    }

    function updateSelectionUi() {
        const avatar = currentAvatar();
        elements.avatarChoices.forEach(choice => {
            choice.setAttribute(
                "aria-checked",
                choice.dataset.avatarChoice === state.avatarId ? "true" : "false");
        });
        elements.scenes.forEach(scene => {
            scene.hidden = scene.dataset.avatarScene !== state.avatarId;
            scene.dataset.mouthPose = "closed";
            scene.classList.remove("is-talking");
        });
        elements.level.value = state.cefrLevel;
        elements.avatarName.textContent = avatar?.name || "";
        elements.sceneName.textContent = avatar?.scenario || "";
    }

    function setTranslationVisibility(show) {
        elements.translationToggle.checked = show;
        root.classList.toggle("show-translations", show);
        try {
            localStorage.setItem("glosify-speaking-translation", show ? "1" : "0");
        } catch {
            // A blocked storage API should not affect the practice experience.
        }
    }

    function loadTranslationPreference() {
        try {
            setTranslationVisibility(
                localStorage.getItem("glosify-speaking-translation") === "1");
        } catch {
            setTranslationVisibility(false);
        }
    }

    async function apiFetch(url, options = {}) {
        const headers = new Headers(options.headers || {});
        if (options.body && !headers.has("Content-Type")) {
            headers.set("Content-Type", "application/json");
        }
        if (antiforgeryToken) {
            headers.set("RequestVerificationToken", antiforgeryToken);
        }

        const response = await fetch(url, {
            credentials: "same-origin",
            ...options,
            headers
        });
        if (response.ok) {
            return response;
        }

        let message = `Request failed (${response.status}).`;
        try {
            const body = await response.json();
            message = body.error || body.title || message;
        } catch {
            const text = await response.text();
            if (text) {
                message = text;
            }
        }

        const error = new Error(message);
        error.status = response.status;
        throw error;
    }

    async function createSession({ announce = true, speakOpening = false } = {}) {
        state.ready = false;
        state.sessionId = null;
        setBusy(true);
        setStatus("");
        setConnection("Starting the scene…", false);
        elements.messages.replaceChildren();
        elements.liveBubble.hidden = true;
        elements.replayLatest.disabled = true;
        state.latestReply = null;
        state.userTurns = 0;
        clearRecordedSpeech();

        try {
            const response = await apiFetch(root.dataset.createUrl, {
                method: "POST",
                body: JSON.stringify({
                    avatarId: state.avatarId,
                    cefrLevel: state.cefrLevel
                })
            });
            const created = await response.json();
            state.sessionId = created.sessionId;
            state.ready = true;

            const opening = {
                replyPolish: created.openingTurn.replyPolish,
                replyEnglish: created.openingTurn.replyEnglish,
                coach: null
            };
            appendAvatarMessage(opening, false);
            showLiveReply(opening.replyPolish, opening.replyEnglish);
            state.latestReply = opening.replyPolish;
            elements.replayLatest.disabled = false;
            setConnection(`${created.avatarName} is ready · ${state.cefrLevel}`, true);
            if (announce) {
                setStatus("Your practice session is ready.");
            }
            if (speakOpening && !elements.muteToggle.checked) {
                void speakReply(opening.replyPolish);
            }
        } catch (error) {
            state.ready = false;
            setConnection("Speaking practice is unavailable", false);
            setStatus(error.message, true);
        } finally {
            setBusy(false);
        }
    }

    async function deleteCurrentSession() {
        const sessionId = state.sessionId;
        state.sessionId = null;
        state.ready = false;
        if (!sessionId) {
            return;
        }

        try {
            await apiFetch(`${root.dataset.createUrl}/${sessionId}`, {
                method: "DELETE"
            });
        } catch {
            // Deletion is best effort; sessions also expire in the server store.
        }
    }

    async function changePractice(avatarId, cefrLevel) {
        const changesConversation =
            avatarId !== state.avatarId || cefrLevel !== state.cefrLevel;
        if (!changesConversation) {
            return;
        }

        if (state.userTurns > 0
            && !window.confirm("Start a new session? The current conversation will be cleared.")) {
            updateSelectionUi();
            return;
        }

        stopSpeaking();
        await deleteCurrentSession();
        state.avatarId = avatarId;
        state.cefrLevel = cefrLevel;
        updateSelectionUi();
        await createSession({ speakOpening: true });
    }

    function appendUserMessage(text, mode, pronunciation) {
        const fragment = elements.userTemplate.content.cloneNode(true);
        const article = fragment.querySelector(".speaking-message");
        fragment.querySelector(".speaking-message-polish").textContent = text;
        fragment.querySelector(".speaking-input-badge").textContent =
            mode === "voice" ? "Voice" : "Typed";

        const scores = fragment.querySelector(".speaking-pronunciation");
        if (mode === "voice" && pronunciation) {
            scores.hidden = false;
            scores.querySelector("[data-score='accuracy']").textContent =
                formatScore(pronunciation.accuracy);
            scores.querySelector("[data-score='fluency']").textContent =
                formatScore(pronunciation.fluency);
        }

        elements.messages.append(article);
        scrollMessages();
    }

    function appendAvatarMessage(turn, allowReplay = true) {
        const fragment = elements.avatarTemplate.content.cloneNode(true);
        const article = fragment.querySelector(".speaking-message");
        fragment.querySelector(".speaking-message-speaker").textContent =
            currentAvatar()?.name || "Avatar";
        fragment.querySelector(".speaking-message-polish").textContent = turn.replyPolish;
        fragment.querySelector(".speaking-message-english").textContent = turn.replyEnglish;

        const replay = fragment.querySelector(".speaking-message-replay");
        replay.hidden = !allowReplay;
        replay.addEventListener("click", () => {
            void speakReply(turn.replyPolish);
        });

        const coach = turn.coach;
        const coachPanel = fragment.querySelector(".speaking-coach");
        let visibleCoachRows = 0;
        if (coach) {
            for (const [name, value] of Object.entries(coach)) {
                const row = fragment.querySelector(`[data-coach-row='${name}']`);
                if (!row) {
                    continue;
                }
                const normalized = String(value || "").trim();
                row.hidden = !normalized;
                row.querySelector("dd").textContent = normalized;
                if (normalized) {
                    visibleCoachRows += 1;
                }
            }
        }
        coachPanel.hidden = visibleCoachRows === 0;

        elements.messages.append(article);
        scrollMessages();
    }

    function scrollMessages() {
        elements.messages.scrollTop = elements.messages.scrollHeight;
    }

    function formatScore(value) {
        const number = Number(value);
        return Number.isFinite(number) ? `${Math.round(number)} / 100` : "—";
    }

    function showLiveReply(polish, english) {
        elements.livePolish.textContent = polish;
        elements.liveEnglish.textContent = english;
        elements.liveBubble.hidden = false;
    }

    async function sendCurrentMessage() {
        const text = elements.textarea.value.trim();
        if (!text || state.busy || !state.ready || !state.sessionId) {
            if (!state.ready) {
                setStatus("Start a speaking session before sending a message.", true);
            }
            return;
        }

        const mode = state.recordedTranscript ? "voice" : "text";
        const pronunciation = mode === "voice" ? state.pronunciation : null;
        const recordedTranscript = state.recordedTranscript;
        appendUserMessage(text, mode, pronunciation);
        state.userTurns += 1;
        elements.textarea.value = "";
        updateCharacterCount();
        clearRecordedSpeech();
        setBusy(true);
        setStatus(`${currentAvatar()?.name || "The avatar"} is thinking…`);

        try {
            const response = await apiFetch(
                `${root.dataset.createUrl}/${state.sessionId}/turns`,
                {
                    method: "POST",
                    body: JSON.stringify({ text, inputMode: mode })
                });
            const turn = await response.json();
            appendAvatarMessage(turn);
            showLiveReply(turn.replyPolish, turn.replyEnglish);
            state.latestReply = turn.replyPolish;
            elements.replayLatest.disabled = false;
            setStatus("");
            if (!elements.muteToggle.checked) {
                await speakReply(turn.replyPolish);
            }
        } catch (error) {
            elements.textarea.value = text;
            updateCharacterCount();
            if (mode === "voice") {
                state.recordedTranscript = recordedTranscript;
                state.pronunciation = pronunciation;
                elements.recordingNote.hidden = false;
            }
            setStatus(error.message, true);
            if (error.status === 404 || error.status === 410) {
                state.ready = false;
                setConnection("Session ended · start a new session", false);
            }
        } finally {
            setBusy(false);
            elements.textarea.focus();
        }
    }

    function clearRecordedSpeech() {
        state.recordedTranscript = "";
        state.pronunciation = null;
        elements.recordingNote.hidden = true;
    }

    async function getSpeechToken(forceRefresh = false) {
        if (!forceRefresh
            && state.token
            && state.token.expiresAt - Date.now() > 120_000) {
            return state.token;
        }

        const response = await apiFetch(root.dataset.tokenUrl, { method: "POST" });
        const data = await response.json();
        state.token = {
            authorizationToken: data.authorizationToken,
            region: data.region,
            expiresAt: Date.parse(data.expiresAtUtc)
        };
        return state.token;
    }

    async function startRecognition() {
        if (state.listening || state.busy) {
            return;
        }

        const sdk = window.SpeechSDK;
        if (!sdk || !navigator.mediaDevices?.getUserMedia) {
            setStatus("This browser cannot record speech here. Typed chat is still available.", true);
            return;
        }

        state.listening = true;
        elements.mic.classList.add("is-listening");
        elements.micLabel.textContent = "Listening…";
        elements.mic.disabled = true;
        setStatus("Speak one Polish sentence. Recording stops automatically after the phrase.");

        let recognizer;
        try {
            const token = await getSpeechToken();
            const speechConfig = sdk.SpeechConfig.fromAuthorizationToken(
                token.authorizationToken,
                token.region);
            speechConfig.speechRecognitionLanguage = "pl-PL";
            speechConfig.outputFormat = sdk.OutputFormat.Detailed;
            const audioConfig = sdk.AudioConfig.fromDefaultMicrophoneInput();
            recognizer = new sdk.SpeechRecognizer(speechConfig, audioConfig);
            const pronunciationConfig = new sdk.PronunciationAssessmentConfig(
                "",
                sdk.PronunciationAssessmentGradingSystem.HundredMark,
                sdk.PronunciationAssessmentGranularity.Phoneme,
                false);
            pronunciationConfig.applyTo(recognizer);

            const result = await new Promise((resolve, reject) => {
                recognizer.recognizeOnceAsync(resolve, reject);
            });
            if (result.reason !== sdk.ResultReason.RecognizedSpeech || !result.text?.trim()) {
                throw new Error("Azure Speech did not catch that. Try again or type your message.");
            }

            let pronunciation = null;
            try {
                const assessment = sdk.PronunciationAssessmentResult.fromResult(result);
                pronunciation = {
                    accuracy: assessment.accuracyScore,
                    fluency: assessment.fluencyScore
                };
            } catch {
                // Recognition remains useful when assessment is unavailable.
            }

            const transcript = result.text.trim();
            elements.textarea.value = transcript;
            state.recordedTranscript = transcript;
            state.pronunciation = pronunciation;
            elements.recordingNote.hidden = false;
            updateCharacterCount();
            setStatus(
                pronunciation
                    ? "Transcript ready. Edit it if needed, then send."
                    : "Transcript ready. Pronunciation scoring was unavailable for this recording.");
            elements.textarea.focus();
        } catch (error) {
            setStatus(
                error?.message || "Microphone access failed. Typed chat is still available.",
                true);
        } finally {
            recognizer?.close();
            state.listening = false;
            elements.mic.classList.remove("is-listening");
            elements.micLabel.textContent = "Speak";
            elements.mic.disabled = state.busy || !state.ready;
        }
    }

    function setMouthPose(pose) {
        const scene = currentScene();
        if (scene) {
            scene.dataset.mouthPose = pose;
        }
    }

    function startGenericMouth() {
        stopMouthAnimation();
        const poses = ["narrow", "open", "round", "open"];
        let index = 0;
        const scene = currentScene();
        scene?.classList.add("is-talking");
        state.genericMouthTimer = window.setInterval(() => {
            setMouthPose(poses[index++ % poses.length]);
        }, 150);
    }

    function stopMouthAnimation() {
        if (state.genericMouthTimer) {
            window.clearInterval(state.genericMouthTimer);
            state.genericMouthTimer = null;
        }
        state.mouthTimers.forEach(timer => window.clearTimeout(timer));
        state.mouthTimers = [];
        elements.scenes.forEach(scene => {
            scene.classList.remove("is-talking");
            scene.dataset.mouthPose = "closed";
        });
    }

    async function speakReply(text) {
        if (!text || elements.muteToggle.checked) {
            return;
        }

        stopSpeaking();
        const sdk = window.SpeechSDK;
        if (!sdk) {
            await playFallbackAudio(text);
            return;
        }

        try {
            const token = await getSpeechToken();
            const avatar = currentAvatar();
            const speechConfig = sdk.SpeechConfig.fromAuthorizationToken(
                token.authorizationToken,
                token.region);
            speechConfig.speechSynthesisVoiceName = avatar.voice;
            const synthesizer = new sdk.SpeechSynthesizer(speechConfig);
            state.activeSynthesizer = synthesizer;
            let synthesisStartedAt = performance.now();
            let receivedVisemes = false;

            startGenericMouth();
            synthesizer.synthesisStarted = () => {
                synthesisStartedAt = performance.now();
            };
            synthesizer.visemeReceived = (_sender, event) => {
                if (!receivedVisemes) {
                    receivedVisemes = true;
                    if (state.genericMouthTimer) {
                        window.clearInterval(state.genericMouthTimer);
                        state.genericMouthTimer = null;
                    }
                }
                const offsetMilliseconds = event.audioOffset / 10_000;
                const elapsed = performance.now() - synthesisStartedAt;
                const timer = window.setTimeout(
                    () => setMouthPose(visemePoses[event.visemeId] || "open"),
                    Math.max(0, offsetMilliseconds - elapsed));
                state.mouthTimers.push(timer);
            };

            const ssml = buildSsml(text, avatar);
            await new Promise((resolve, reject) => {
                synthesizer.speakSsmlAsync(
                    ssml,
                    result => {
                        if (result.reason === sdk.ResultReason.SynthesizingAudioCompleted) {
                            resolve(result);
                        } else {
                            reject(new Error(result.errorDetails || "Speech synthesis was canceled."));
                        }
                    },
                    reject);
            });
        } catch {
            await playFallbackAudio(text);
        } finally {
            state.activeSynthesizer?.close();
            state.activeSynthesizer = null;
            stopMouthAnimation();
        }
    }

    function buildSsml(text, avatar) {
        return `<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="pl-PL">`
            + `<voice name="${escapeXml(avatar.voice)}">`
            + `<prosody rate="${escapeXml(avatar.ssmlRate || "0%")}" pitch="${escapeXml(avatar.ssmlPitch || "0%")}">`
            + `${escapeXml(text)}</prosody></voice></speak>`;
    }

    function escapeXml(value) {
        return String(value)
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll("\"", "&quot;")
            .replaceAll("'", "&apos;");
    }

    async function playFallbackAudio(text) {
        startGenericMouth();
        try {
            const url = `${root.dataset.ttsFallbackUrl}?text=${encodeURIComponent(text.slice(0, 200))}&lang=pl`;
            const audio = new Audio(url);
            state.activeAudio = audio;
            await audio.play();
            await new Promise((resolve, reject) => {
                audio.addEventListener("ended", resolve, { once: true });
                audio.addEventListener("error", reject, { once: true });
            });
        } catch {
            setStatus("The reply is visible, but audio playback is unavailable.", true);
        } finally {
            state.activeAudio = null;
            stopMouthAnimation();
        }
    }

    function stopSpeaking() {
        state.activeSynthesizer?.close();
        state.activeSynthesizer = null;
        if (state.activeAudio) {
            state.activeAudio.pause();
            state.activeAudio.src = "";
            state.activeAudio = null;
        }
        stopMouthAnimation();
    }

    elements.avatarChoices.forEach(choice => {
        choice.addEventListener("click", () => {
            void changePractice(choice.dataset.avatarChoice, state.cefrLevel);
        });
    });

    elements.level.addEventListener("change", () => {
        void changePractice(state.avatarId, elements.level.value);
    });

    elements.translationToggle.addEventListener("change", () => {
        setTranslationVisibility(elements.translationToggle.checked);
    });

    elements.muteToggle.addEventListener("change", () => {
        if (elements.muteToggle.checked) {
            stopSpeaking();
        }
    });

    elements.newSession.addEventListener("click", async () => {
        if (state.userTurns > 0
            && !window.confirm("Clear this conversation and start a new session?")) {
            return;
        }
        stopSpeaking();
        await deleteCurrentSession();
        await createSession({ speakOpening: true });
    });

    elements.replayLatest.addEventListener("click", () => {
        void speakReply(state.latestReply);
    });

    elements.mic.addEventListener("click", () => {
        void startRecognition();
    });

    elements.send.addEventListener("click", () => {
        void sendCurrentMessage();
    });

    elements.textarea.addEventListener("input", () => {
        updateCharacterCount();
        if (state.recordedTranscript
            && elements.textarea.value.trim() !== state.recordedTranscript) {
            elements.recordingNote.hidden = false;
        }
    });

    elements.textarea.addEventListener("keydown", event => {
        if (event.key === "Enter" && !event.shiftKey) {
            event.preventDefault();
            void sendCurrentMessage();
        }
    });

    window.addEventListener("pagehide", () => {
        stopSpeaking();
        if (!state.sessionId || !antiforgeryToken) {
            return;
        }
        fetch(`${root.dataset.createUrl}/${state.sessionId}`, {
            method: "DELETE",
            credentials: "same-origin",
            keepalive: true,
            headers: { RequestVerificationToken: antiforgeryToken }
        }).catch(() => {});
    });

    loadTranslationPreference();
    updateSelectionUi();
    updateCharacterCount();
    setBusy(true);
    void createSession({ announce: false });
})();

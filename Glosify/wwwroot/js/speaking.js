(() => {
    "use strict";

    const root = document.getElementById("speaking-app");
    if (!root) {
        return;
    }

    const pageData = JSON.parse(root.dataset.speakingPage || "{}");
    const practiceLanguage = pageData.language || "the selected language";
    const avatars = new Map((pageData.avatars || []).map(avatar => [avatar.id, avatar]));
    const elements = {
        avatarChoices: [...root.querySelectorAll("[data-avatar-choice]")],
        scenes: [...root.querySelectorAll("[data-avatar-scene]")]
            .filter(scene => avatars.has(scene.dataset.avatarScene)),
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
        avatarRecord: document.getElementById("speaking-avatar-record"),
        avatarRecordLabel: root.querySelector(".speaking-avatar-record-label"),
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
        avatarId: pageData.defaultAvatarId || avatars.keys().next().value || "",
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
        activeRecognition: null,
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
        const interactionLocked = busy || state.listening;
        const recognitionSource = state.activeRecognition?.source;
        elements.send.disabled = interactionLocked || !state.ready;
        elements.mic.disabled =
            busy
            || !state.ready
            || (state.listening && recognitionSource !== "composer");
        elements.avatarRecord.disabled =
            busy
            || !state.ready
            || (state.listening && recognitionSource !== "avatar");
        elements.level.disabled = interactionLocked;
        elements.newSession.disabled = interactionLocked;
        elements.textarea.readOnly = state.listening;
        elements.avatarChoices.forEach(choice => {
            choice.disabled = interactionLocked;
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

    function setMessageTranslation(message, showTranslation) {
        message.classList.toggle("is-flipped", showTranslation);
        message.setAttribute("aria-pressed", showTranslation ? "true" : "false");
        message.setAttribute(
            "aria-label",
            showTranslation
                ? `Show ${practiceLanguage} message`
                : "Show English translation");
        message.querySelector(".speaking-message-front")
            ?.setAttribute("aria-hidden", showTranslation ? "true" : "false");
        message.querySelector(".speaking-message-back")
            ?.setAttribute("aria-hidden", showTranslation ? "false" : "true");
    }

    function setTranslationVisibility(show) {
        elements.translationToggle.checked = show;
        root.classList.toggle("show-translations", show);
        root.querySelectorAll("[data-message-flip]").forEach(message => {
            setMessageTranslation(message, show);
        });
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

        const messageFlip = fragment.querySelector("[data-message-flip]");
        setMessageTranslation(messageFlip, elements.translationToggle.checked);
        messageFlip.addEventListener("click", () => {
            setMessageTranslation(
                messageFlip,
                !messageFlip.classList.contains("is-flipped"));
        });

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

    function beginRecognition({
        source,
        autoSend,
        autoStop,
        control,
        label,
        idleLabel
    }) {
        if (state.listening || state.busy) {
            return;
        }

        const sdk = window.SpeechSDK;
        if (!sdk || !navigator.mediaDevices?.getUserMedia) {
            setStatus("This browser cannot record speech here. Typed chat is still available.", true);
            return;
        }

        stopSpeaking();
        clearRecordedSpeech();
        state.listening = true;
        const recognition = {
            source,
            autoSend,
            autoStop,
            control,
            label,
            idleLabel,
            recognizer: null,
            started: false,
            stopRequested: false,
            finishing: false,
            silenceTimer: null,
            segments: [],
            partialTranscript: "",
            pronunciationSamples: [],
            error: null,
            startPromise: null
        };
        state.activeRecognition = recognition;
        control.classList.add("is-listening");
        control.setAttribute("aria-pressed", "true");
        label.textContent = "Starting…";
        setBusy(state.busy);
        setStatus(
            autoStop
                ? "Connecting to the microphone. Start speaking when it is ready."
                : "Connecting to the microphone. Keep holding the button.");

        recognition.startPromise = initializeRecognition(recognition, sdk)
            .catch(error => {
                recognition.error =
                    error instanceof Error
                        ? error
                        : new Error("Microphone access failed. Typed chat is still available.");
                return finishRecognition(recognition, false);
            });
    }

    function beginAvatarRecognition() {
        beginRecognition({
            source: "avatar",
            autoSend: true,
            autoStop: false,
            control: elements.avatarRecord,
            label: elements.avatarRecordLabel,
            idleLabel: "Hold to speak & send"
        });
    }

    function beginComposerRecognition() {
        beginRecognition({
            source: "composer",
            autoSend: false,
            autoStop: true,
            control: elements.mic,
            label: elements.micLabel,
            idleLabel: "Speak to type"
        });
    }

    function clearSilenceTimer(recognition) {
        if (!recognition.silenceTimer) {
            return;
        }

        window.clearTimeout(recognition.silenceTimer);
        recognition.silenceTimer = null;
    }

    function scheduleSilenceStop(recognition, delay = 1_800) {
        if (!recognition.autoStop
            || recognition.stopRequested
            || state.activeRecognition !== recognition) {
            return;
        }

        clearSilenceTimer(recognition);
        recognition.silenceTimer = window.setTimeout(() => {
            recognition.silenceTimer = null;
            void stopRecognition(recognition);
        }, delay);
    }

    async function initializeRecognition(recognition, sdk) {
        const token = await getSpeechToken();
        if (state.activeRecognition !== recognition) {
            return;
        }

        const speechConfig = sdk.SpeechConfig.fromAuthorizationToken(
            token.authorizationToken,
            token.region);
        const avatar = currentAvatar();
        speechConfig.speechRecognitionLanguage = avatar?.locale || pageData.locale;
        speechConfig.outputFormat = sdk.OutputFormat.Detailed;
        const audioConfig = sdk.AudioConfig.fromDefaultMicrophoneInput();
        const recognizer = new sdk.SpeechRecognizer(speechConfig, audioConfig);
        recognition.recognizer = recognizer;

        const pronunciationConfig = new sdk.PronunciationAssessmentConfig(
            "",
            sdk.PronunciationAssessmentGradingSystem.HundredMark,
            sdk.PronunciationAssessmentGranularity.Phoneme,
            false);
        pronunciationConfig.applyTo(recognizer);

        recognizer.recognizing = (_sender, event) => {
            if (event.result.reason === sdk.ResultReason.RecognizingSpeech) {
                recognition.partialTranscript = event.result.text?.trim() || "";
                if (recognition.partialTranscript) {
                    scheduleSilenceStop(recognition);
                }
            }
        };
        recognizer.recognized = (_sender, event) => {
            if (event.result.reason !== sdk.ResultReason.RecognizedSpeech
                || !event.result.text?.trim()) {
                return;
            }

            const text = event.result.text.trim();
            recognition.segments.push(text);
            recognition.partialTranscript = "";
            scheduleSilenceStop(recognition);
            try {
                const assessment = sdk.PronunciationAssessmentResult.fromResult(event.result);
                recognition.pronunciationSamples.push({
                    accuracy: assessment.accuracyScore,
                    fluency: assessment.fluencyScore,
                    weight: Math.max(1, text.length)
                });
            } catch {
                // Recognition remains useful when assessment is unavailable.
            }
        };
        recognizer.canceled = (_sender, event) => {
            if (event.reason === sdk.CancellationReason.Error) {
                recognition.error = new Error(
                    "Azure Speech could not finish the recording. Try again or type your message.");
                void finishRecognition(recognition, false);
            }
        };

        await new Promise((resolve, reject) => {
            recognizer.startContinuousRecognitionAsync(resolve, reject);
        });
        recognition.started = true;
        if (!recognition.stopRequested && state.activeRecognition === recognition) {
            if (recognition.autoStop) {
                recognition.label.textContent = "Listening…";
                setStatus("Speak naturally. The recording will stop when you finish.");
                scheduleSilenceStop(recognition, 10_000);
            } else {
                recognition.label.textContent = "Release to send";
                setStatus("Keep holding while you speak. Release the button to send.");
            }
        }
    }

    async function stopRecognition(recognition = state.activeRecognition) {
        if (!recognition || recognition.stopRequested) {
            return;
        }

        clearSilenceTimer(recognition);
        recognition.stopRequested = true;
        recognition.label.textContent = "Transcribing…";
        recognition.control.disabled = true;
        setStatus("Finishing your transcription…");
        await recognition.startPromise;
        if (state.activeRecognition !== recognition) {
            return;
        }

        if (recognition.started && recognition.recognizer) {
            try {
                await new Promise((resolve, reject) => {
                    recognition.recognizer.stopContinuousRecognitionAsync(resolve, reject);
                });
            } catch (error) {
                recognition.error =
                    error instanceof Error
                        ? error
                        : new Error("Azure Speech could not finish the recording.");
            }
        }

        await finishRecognition(recognition, true);
    }

    async function endAvatarRecognition() {
        const recognition = state.activeRecognition;
        if (recognition?.source !== "avatar") {
            return;
        }

        await stopRecognition(recognition);
    }

    async function finishRecognition(recognition, processTranscript) {
        if (recognition.finishing || state.activeRecognition !== recognition) {
            return;
        }

        recognition.finishing = true;
        clearSilenceTimer(recognition);
        recognition.recognizer?.close();
        state.activeRecognition = null;
        state.listening = false;
        recognition.control.classList.remove("is-listening");
        recognition.control.setAttribute("aria-pressed", "false");
        recognition.label.textContent = recognition.idleLabel;
        setBusy(state.busy);

        if (recognition.error) {
            setStatus(recognition.error.message, true);
            return;
        }
        if (!processTranscript) {
            return;
        }

        const transcript = [...recognition.segments, recognition.partialTranscript]
            .filter(Boolean)
            .join(" ")
            .replace(/\s+/g, " ")
            .trim();
        if (!transcript) {
            setStatus(
                recognition.source === "composer"
                    ? "Azure Speech did not catch that. Press Speak to type and try again."
                    : "Azure Speech did not catch that. Hold the red button and try again.",
                true);
            return;
        }
        if (transcript.length > 800) {
            const shortenedTranscript = transcript.slice(0, 800);
            elements.textarea.value = shortenedTranscript;
            state.recordedTranscript = shortenedTranscript;
            state.pronunciation = averagePronunciation(recognition.pronunciationSamples);
            elements.recordingNote.hidden = false;
            updateCharacterCount();
            setStatus("That recording is too long. Shorten the transcript and press Send.", true);
            elements.textarea.focus();
            return;
        }

        elements.textarea.value = transcript;
        state.recordedTranscript = transcript;
        state.pronunciation = averagePronunciation(recognition.pronunciationSamples);
        elements.recordingNote.hidden = false;
        updateCharacterCount();
        if (recognition.autoSend) {
            setStatus("Transcript ready. Sending…");
            await sendCurrentMessage();
            return;
        }

        setStatus("Transcript ready. Review or edit it, then press Send.");
        elements.textarea.focus();
    }

    function averagePronunciation(samples) {
        if (!samples.length) {
            return null;
        }

        const totalWeight = samples.reduce((sum, sample) => sum + sample.weight, 0);
        return {
            accuracy: samples.reduce(
                (sum, sample) => sum + sample.accuracy * sample.weight,
                0) / totalWeight,
            fluency: samples.reduce(
                (sum, sample) => sum + sample.fluency * sample.weight,
                0) / totalWeight
        };
    }

    function cancelRecognition() {
        const recognition = state.activeRecognition;
        if (!recognition) {
            return;
        }

        recognition.finishing = true;
        clearSilenceTimer(recognition);
        recognition.recognizer?.close();
        state.activeRecognition = null;
        state.listening = false;
        recognition.control.classList.remove("is-listening");
        recognition.control.setAttribute("aria-pressed", "false");
        recognition.label.textContent = recognition.idleLabel;
        setBusy(state.busy);
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
            const audioConfig = sdk.AudioConfig.fromDefaultSpeakerOutput();
            const synthesizer = new sdk.SpeechSynthesizer(speechConfig, audioConfig);
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
        return `<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="${escapeXml(avatar.locale)}">`
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
            const avatar = currentAvatar();
            const languageCode = avatar?.languageCode || pageData.languageCode;
            const url = `${root.dataset.ttsFallbackUrl}?text=${encodeURIComponent(text.slice(0, 200))}&lang=${encodeURIComponent(languageCode)}`;
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

    elements.avatarRecord.addEventListener("pointerdown", event => {
        if (!event.isPrimary || event.button !== 0) {
            return;
        }
        event.preventDefault();
        elements.avatarRecord.setPointerCapture?.(event.pointerId);
        beginAvatarRecognition();
    });

    const releasePointer = event => {
        if (!event.isPrimary) {
            return;
        }
        event.preventDefault();
        if (elements.avatarRecord.hasPointerCapture?.(event.pointerId)) {
            elements.avatarRecord.releasePointerCapture(event.pointerId);
        }
        void endAvatarRecognition();
    };
    elements.avatarRecord.addEventListener("pointerup", releasePointer);
    elements.avatarRecord.addEventListener("pointercancel", releasePointer);
    elements.avatarRecord.addEventListener("lostpointercapture", () => {
        void endAvatarRecognition();
    });
    elements.avatarRecord.addEventListener("keydown", event => {
        if ((event.key === " " || event.key === "Enter") && !event.repeat) {
            event.preventDefault();
            beginAvatarRecognition();
        }
    });
    elements.avatarRecord.addEventListener("keyup", event => {
        if (event.key === " " || event.key === "Enter") {
            event.preventDefault();
            void endAvatarRecognition();
        }
    });

    elements.mic.addEventListener("click", () => {
        const recognition = state.activeRecognition;
        if (recognition?.source === "composer") {
            void stopRecognition(recognition);
            return;
        }

        beginComposerRecognition();
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

    window.addEventListener("blur", () => {
        void stopRecognition();
    });

    window.addEventListener("pagehide", () => {
        cancelRecognition();
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

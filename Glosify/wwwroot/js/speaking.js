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
        interactiveLayer: document.getElementById("speaking-interactive-layer"),
        menuToggle: document.getElementById("speaking-menu-toggle"),
        barMenu: document.getElementById("speaking-bar-menu"),
        walletToggle: document.getElementById("speaking-wallet-toggle"),
        wallet: document.getElementById("speaking-wallet"),
        walletClose: document.getElementById("speaking-wallet-close"),
        walletBalance: document.getElementById("speaking-wallet-balance"),
        walletMoney: document.getElementById("speaking-wallet-money"),
        paymentDue: document.getElementById("speaking-payment-due"),
        paymentSelected: document.getElementById("speaking-payment-selected"),
        paymentSubmit: document.getElementById("speaking-payment-submit"),
        bill: document.getElementById("speaking-bill"),
        billTotal: document.getElementById("speaking-bill-total"),
        drinkAction: document.getElementById("speaking-drink-action"),
        drinkLabel: document.getElementById("speaking-drink-label"),
        snackAction: document.getElementById("speaking-snack-action"),
        sceneEvent: document.getElementById("speaking-scene-event"),
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
        genericMouthTimer: null,
        interactiveMode: false,
        interaction: null,
        selectedTender: new Map(),
        sceneEventTimer: null,
        sceneGeneration: 0,
        sceneQueue: Promise.resolve(),
        sceneQueueVersion: 0,
        sceneActionsPending: false,
        speechGeneration: 0
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

    function supportsInteractiveMode() {
        return Boolean(pageData.interactiveBartenderEnabled)
            && state.avatarId === "bartender";
    }

    function updateInteractiveMode() {
        state.interactiveMode = supportsInteractiveMode();
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
        [elements.drinkAction, elements.snackAction]
            .filter(Boolean)
            .forEach(control => {
                control.disabled =
                    interactionLocked
                    || state.sceneActionsPending
                    || !state.ready;
            });
        [elements.menuToggle, elements.walletToggle]
            .filter(Boolean)
            .forEach(control => {
                control.disabled = !state.ready;
            });
        updatePaymentSelection();
    }

    function updateCharacterCount() {
        elements.characterCount.textContent = `${elements.textarea.value.length} / 800`;
    }

    function updateSelectionUi() {
        const avatar = currentAvatar();
        updateInteractiveMode();
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
        if (elements.interactiveLayer) {
            elements.interactiveLayer.hidden = !state.interactiveMode;
        }
        const scene = currentScene();
        scene?.classList.toggle("is-interactive", state.interactiveMode);
        if (!state.interactiveMode) {
            resetInteractionUi();
        }
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

    function resetInteractionUi() {
        cancelSceneActions();
        state.interaction = null;
        state.selectedTender.clear();
        if (elements.barMenu) {
            elements.barMenu.hidden = true;
        }
        if (elements.wallet) {
            elements.wallet.hidden = true;
        }
        if (elements.menuToggle) {
            elements.menuToggle.setAttribute("aria-expanded", "false");
        }
        if (elements.walletToggle) {
            elements.walletToggle.setAttribute("aria-expanded", "false");
        }
        if (elements.bill) {
            elements.bill.hidden = true;
        }
        if (elements.drinkAction) {
            elements.drinkAction.hidden = true;
        }
        if (elements.snackAction) {
            elements.snackAction.hidden = true;
        }
        if (elements.walletMoney) {
            elements.walletMoney.replaceChildren();
        }
        hideSceneEvent();
    }

    function applySceneSnapshot(snapshot) {
        const activeDrink = snapshot?.activeDrink || null;
        const scene = currentScene();
        scene?.classList.toggle("has-active-drink", Boolean(activeDrink));
        const glass = scene?.querySelector("[data-bartender-active-drink]");
        if (glass && activeDrink) {
            glass.dataset.drinkId = activeDrink.id;
            glass.dataset.fillLevel = String(activeDrink.fillLevel);
        }
    }

    function hideInteractionActions() {
        if (elements.drinkAction) {
            elements.drinkAction.hidden = true;
        }
        if (elements.snackAction) {
            elements.snackAction.hidden = true;
        }
    }

    function applyInteractionActions(snapshot) {
        const activeDrink = snapshot?.activeDrink || null;
        const actions = new Set(snapshot?.availableActions || []);
        elements.drinkAction.hidden = !actions.has("drink");
        if (activeDrink) {
            elements.drinkLabel.textContent =
                activeDrink.fillLevel <= 1 ? "Finish drink" : "Take a sip";
            elements.drinkAction.setAttribute(
                "aria-label",
                `${elements.drinkLabel.textContent}: ${activeDrink.nameEnglish}`);
        }
        elements.snackAction.hidden = !actions.has("takeSnack");
    }

    function applyInteractionSnapshot(
        snapshot,
        { updateScene = true, updateActions = true } = {}) {
        state.interaction = snapshot || null;
        state.selectedTender.clear();
        if (!snapshot || !state.interactiveMode) {
            resetInteractionUi();
            return;
        }

        elements.walletBalance.textContent = `${snapshot.walletBalance} zł`;
        elements.paymentDue.textContent = snapshot.billPresented
            ? `Amount due: ${snapshot.tabTotal} zł`
            : snapshot.tabTotal > 0
                ? `Open tab: ${snapshot.tabTotal} zł`
                : "No bill yet";
        elements.bill.hidden = !snapshot.billPresented;
        elements.billTotal.textContent = `${snapshot.tabTotal} zł`;
        if (!snapshot.billPresented && snapshot.tabTotal === 0) {
            setPanel(elements.wallet, elements.walletToggle, false);
        }

        if (updateScene) {
            applySceneSnapshot(snapshot);
        }

        if (updateActions) {
            applyInteractionActions(snapshot);
        } else {
            hideInteractionActions();
        }

        root.querySelectorAll("[data-menu-drink]").forEach(row => {
            const unavailable =
                (snapshot.unavailableDrinkIds || []).includes(row.dataset.menuDrink);
            row.classList.toggle("is-unavailable", unavailable);
            row.dataset.unavailable = unavailable ? "true" : "false";
            if (unavailable) {
                row.setAttribute("aria-label", `${row.textContent.trim()} unavailable`);
            } else {
                row.removeAttribute("aria-label");
            }
        });

        renderWallet(snapshot.wallet || []);
        updatePaymentSelection();
    }

    function renderWallet(wallet) {
        if (!elements.walletMoney) {
            return;
        }

        elements.walletMoney.replaceChildren();
        for (const denomination of wallet) {
            const button = document.createElement("button");
            button.type = "button";
            button.className = "speaking-wallet-denomination";
            button.dataset.denomination = String(denomination.value);
            button.disabled = denomination.count <= 0;
            button.innerHTML =
                `<span>${denomination.value} zł</span><small>owned ×${denomination.count}</small>`;
            button.addEventListener("click", () => {
                const selected = state.selectedTender.get(denomination.value) || 0;
                const next = selected >= denomination.count ? 0 : selected + 1;
                if (next === 0) {
                    state.selectedTender.delete(denomination.value);
                } else {
                    state.selectedTender.set(denomination.value, next);
                }
                updatePaymentSelection();
            });
            elements.walletMoney.append(button);
        }
    }

    function updatePaymentSelection() {
        if (!elements.paymentSubmit) {
            return;
        }

        const total = [...state.selectedTender.entries()]
            .reduce((sum, [value, count]) => sum + value * count, 0);
        elements.paymentSelected.textContent = `${total} zł`;
        elements.walletMoney?.querySelectorAll("[data-denomination]").forEach(button => {
            const value = Number(button.dataset.denomination);
            const count = state.selectedTender.get(value) || 0;
            button.classList.toggle("is-selected", count > 0);
            button.setAttribute("aria-pressed", count > 0 ? "true" : "false");
            const detail = button.querySelector("small");
            const owned = state.interaction?.wallet
                ?.find(item => item.value === value)?.count || 0;
            if (detail) {
                detail.textContent = count > 0
                    ? `selected ×${count} · owned ×${owned}`
                    : `owned ×${owned}`;
            }
            button.disabled = owned <= 0;
        });
        elements.paymentSubmit.disabled =
            state.busy
            || state.sceneActionsPending
            || !state.ready
            || !state.interaction?.billPresented
            || total <= 0;
    }

    function setPanel(panel, toggle, open) {
        if (!panel || !toggle) {
            return;
        }
        panel.hidden = !open;
        toggle.setAttribute("aria-expanded", open ? "true" : "false");
    }

    function openWallet() {
        setPanel(elements.barMenu, elements.menuToggle, false);
        setPanel(elements.wallet, elements.walletToggle, true);
    }

    function hideSceneEvent() {
        if (!elements.sceneEvent) {
            return;
        }
        if (state.sceneEventTimer) {
            window.clearTimeout(state.sceneEventTimer);
            state.sceneEventTimer = null;
        }
        elements.sceneEvent.hidden = true;
    }

    function showSceneEvent(message) {
        if (!elements.sceneEvent) {
            return;
        }
        hideSceneEvent();
        elements.sceneEvent.textContent = message;
        elements.sceneEvent.hidden = false;
        state.sceneEventTimer = window.setTimeout(hideSceneEvent, 3_500);
    }

    function animationDelay(milliseconds) {
        if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
            return Promise.resolve();
        }
        return new Promise(resolve => window.setTimeout(resolve, milliseconds));
    }

    function cancelSceneActions() {
        state.sceneGeneration += 1;
        state.sceneQueueVersion += 1;
        state.sceneQueue = Promise.resolve();
        state.sceneActionsPending = false;
        elements.interactiveLayer?.setAttribute("aria-busy", "false");
        elements.scenes.forEach(scene => {
            scene.classList.remove(
                "has-active-drink",
                "is-pouring",
                "is-serving",
                "is-polishing",
                "is-wiping",
                "is-clearing",
                "is-snacking",
                "is-last-call");
            delete scene.dataset.pourSource;
        });
        hideSceneEvent();
    }

    async function waitForScene(milliseconds, generation) {
        await animationDelay(milliseconds);
        return generation === state.sceneGeneration;
    }

    function waitForSceneAnimation(
        element,
        expectedAnimationName,
        fallbackMilliseconds,
        generation) {
        if (!element
            || window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
            return Promise.resolve(generation === state.sceneGeneration);
        }

        return new Promise(resolve => {
            let settled = false;
            const finish = () => {
                if (settled) {
                    return;
                }
                settled = true;
                window.clearTimeout(fallbackTimer);
                element.removeEventListener("animationend", handleAnimationEnd);
                element.removeEventListener("animationcancel", handleAnimationCancel);
                resolve(generation === state.sceneGeneration);
            };
            const handleAnimationEnd = event => {
                if (event.target === element
                    && event.animationName === expectedAnimationName) {
                    finish();
                }
            };
            const handleAnimationCancel = event => {
                if (event.target === element
                    && event.animationName === expectedAnimationName
                    && generation !== state.sceneGeneration) {
                    finish();
                }
            };
            const fallbackTimer = window.setTimeout(finish, fallbackMilliseconds);
            element.addEventListener("animationend", handleAnimationEnd);
            element.addEventListener("animationcancel", handleAnimationCancel);
        });
    }

    async function playSceneCommand(command, generation) {
        if (!state.interactiveMode
            || !command
            || generation !== state.sceneGeneration) {
            return;
        }

        const scene = currentScene();
        const glass = scene?.querySelector("[data-bartender-active-drink]");
        const glassMotion = glass?.querySelector("[data-bartender-drink-motion]");
        switch (command.type) {
            case "pourAndServe":
                if (scene) {
                    scene.dataset.pourSource = command.drinkId || "";
                }
                scene?.classList.add("is-pouring");
                if (glass) {
                    glass.dataset.drinkId = command.drinkId || "";
                    glass.dataset.fillLevel = String(command.fillLevel ?? 3);
                }
                showSceneEvent("Marek pours and slides over your drink.");
                const beerTap =
                    command.drinkId === "lightBeer" || command.drinkId === "darkBeer"
                        ? scene?.querySelector(
                            `[data-bartender-tap="${command.drinkId}"] [data-bartender-pour-motion]`)
                        : null;
                const pourMotion = beerTap
                    || scene?.querySelector(
                        "[data-bartender-bottle] [data-bartender-pour-motion]");
                if (!await waitForSceneAnimation(
                    pourMotion,
                    "speaking-bartender-pour",
                    1_000,
                    generation)) {
                    return;
                }
                scene?.classList.remove("is-pouring");
                if (scene) {
                    delete scene.dataset.pourSource;
                }
                scene?.classList.add("is-serving");
                if (!await waitForSceneAnimation(
                    glassMotion,
                    "speaking-bartender-serve",
                    850,
                    generation)) {
                    return;
                }
                scene?.classList.add("has-active-drink");
                scene?.classList.remove("is-serving");
                break;
            case "drink":
                if (glass) {
                    glass.dataset.fillLevel = String(command.fillLevel ?? 0);
                }
                showSceneEvent(
                    command.fillLevel === 0 ? "You finish the drink." : "You take a sip.");
                await waitForScene(350, generation);
                break;
            case "takeSnack":
                scene?.classList.add("is-snacking");
                showSceneEvent("You take some paluszki.");
                if (!await waitForSceneAnimation(
                    scene?.querySelector(
                        "[data-bartender-snack] [data-bartender-snack-motion]"),
                    "speaking-bartender-snack",
                    650,
                    generation)) {
                    return;
                }
                scene?.classList.remove("is-snacking");
                break;
            case "showBill":
                openWallet();
                showSceneEvent(`Marek presents the ${command.amount} zł bill.`);
                await waitForScene(350, generation);
                break;
            case "offerSnack":
                showSceneEvent("Marek offers you paluszki.");
                await waitForScene(350, generation);
                break;
            case "clearGlass":
                scene?.classList.add("is-clearing");
                showSceneEvent("Marek clears the empty glass.");
                if (!await waitForSceneAnimation(
                    glassMotion,
                    "speaking-bartender-clear",
                    750,
                    generation)) {
                    return;
                }
                scene?.classList.remove("is-clearing", "has-active-drink");
                break;
            case "polishGlass":
                scene?.classList.add("is-polishing");
                showSceneEvent("Marek polishes a glass.");
                if (!await waitForSceneAnimation(
                    scene?.querySelector("[data-bartender-polish-gesture]"),
                    "speaking-bartender-polish",
                    1_450,
                    generation)) {
                    return;
                }
                scene?.classList.remove("is-polishing");
                break;
            case "wipeCounter":
                scene?.classList.add("is-wiping");
                showSceneEvent("Marek wipes the counter.");
                if (!await waitForSceneAnimation(
                    scene?.querySelector("[data-bartender-counter]"),
                    "speaking-bartender-wipe",
                    900,
                    generation)) {
                    return;
                }
                scene?.classList.remove("is-wiping");
                break;
            case "lastCall":
                scene?.classList.add("is-last-call");
                showSceneEvent("Last call.");
                if (!await waitForScene(650, generation)) {
                    return;
                }
                scene?.classList.remove("is-last-call");
                break;
            case "markUnavailable":
                showSceneEvent("That item is unavailable.");
                await waitForScene(350, generation);
                break;
            case "paymentRejected":
                openWallet();
                showSceneEvent(`${command.amount} zł is not enough. Nothing was removed.`);
                await waitForScene(450, generation);
                break;
            case "paymentAccepted":
                showSceneEvent(`Marek accepts ${command.amount} zł.`);
                await waitForScene(450, generation);
                break;
            case "returnChange":
                showSceneEvent(`Marek returns ${command.amount} zł change.`);
                await waitForScene(500, generation);
                break;
        }
    }

    async function playSceneActions(commands, snapshot, generation) {
        try {
            for (const command of commands || []) {
                if (generation !== state.sceneGeneration) {
                    return;
                }
                await playSceneCommand(command, generation);
            }
        } finally {
            if (generation === state.sceneGeneration && state.interactiveMode) {
                applySceneSnapshot(snapshot);
            }
        }
    }

    function enqueueSceneActions(commands, snapshot) {
        const generation = state.sceneGeneration;
        const queueVersion = ++state.sceneQueueVersion;
        state.sceneActionsPending = true;
        elements.interactiveLayer?.setAttribute("aria-busy", "true");
        hideInteractionActions();
        setBusy(state.busy);
        state.sceneQueue = state.sceneQueue
            .catch(() => {})
            .then(() => playSceneActions(commands, snapshot, generation))
            .catch(() => {})
            .then(() => {
                if (generation !== state.sceneGeneration
                    || queueVersion !== state.sceneQueueVersion
                    || !state.interactiveMode) {
                    return;
                }
                applyInteractionActions(snapshot);
                state.sceneActionsPending = false;
                elements.interactiveLayer?.setAttribute("aria-busy", "false");
                setBusy(state.busy);
            });
    }

    function presentTurn(turn) {
        appendAvatarMessage(turn);
        showLiveReply(turn.replyPolish, turn.replyEnglish);
        state.latestReply = turn.replyPolish;
        elements.replayLatest.disabled = false;
        if (state.interactiveMode && turn.interaction) {
            applyInteractionSnapshot(
                turn.interaction,
                { updateScene: false, updateActions: false });
            enqueueSceneActions(turn.sceneActions, turn.interaction);
        } else {
            applyInteractionSnapshot(turn.interaction);
        }
        if (!elements.muteToggle.checked) {
            void speakReply(turn.replyPolish);
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
        resetInteractionUi();
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
            applyInteractionSnapshot(created.interaction);
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

    function refocusComposerForKeyboardUsers() {
        if (window.matchMedia("(hover: hover) and (pointer: fine)").matches) {
            elements.textarea.focus({ preventScroll: true });
        }
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
        stopSpeaking();
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
            setStatus("");
            presentTurn(turn);
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
            refocusComposerForKeyboardUsers();
        }
    }

    async function sendInteractiveAction(action, denominations = null) {
        if (state.busy
            || state.sceneActionsPending
            || !state.ready
            || !state.sessionId
            || !state.interactiveMode) {
            return;
        }

        stopSpeaking();
        setBusy(true);
        setStatus(`${currentAvatar()?.name || "The avatar"} is reacting…`);
        try {
            const response = await apiFetch(
                `${root.dataset.createUrl}/${state.sessionId}/actions`,
                {
                    method: "POST",
                    body: JSON.stringify({ action, denominations })
                });
            const turn = await response.json();
            state.userTurns += 1;
            setStatus("");
            presentTurn(turn);
        } catch (error) {
            if (error.status === 404 || error.status === 410) {
                setStatus(error.message, true);
                state.ready = false;
                setConnection("Session ended · start a new session", false);
            } else if (error.status === 400 || error.status === 409) {
                setStatus("");
                showSceneEvent("That moment passed. Keep chatting or try another action.");
            } else {
                setStatus(error.message, true);
            }
        } finally {
            setBusy(false);
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

    function closeSynthesizer(synthesizer) {
        try {
            synthesizer?.close();
        } catch {
            // Superseded speech must never interrupt the conversation controls.
        }
    }

    async function speakReply(text) {
        if (!text || elements.muteToggle.checked) {
            return;
        }

        stopSpeaking();
        const generation = state.speechGeneration;
        const sdk = window.SpeechSDK;
        if (!sdk) {
            await playFallbackAudio(text, generation);
            return;
        }

        let synthesizer = null;
        try {
            const token = await getSpeechToken();
            if (generation !== state.speechGeneration) {
                return;
            }
            const avatar = currentAvatar();
            const speechConfig = sdk.SpeechConfig.fromAuthorizationToken(
                token.authorizationToken,
                token.region);
            speechConfig.speechSynthesisVoiceName = avatar.voice;
            const audioConfig = sdk.AudioConfig.fromDefaultSpeakerOutput();
            synthesizer = new sdk.SpeechSynthesizer(speechConfig, audioConfig);
            state.activeSynthesizer = synthesizer;
            let synthesisStartedAt = performance.now();
            let receivedVisemes = false;

            startGenericMouth();
            synthesizer.synthesisStarted = () => {
                if (generation !== state.speechGeneration) {
                    return;
                }
                synthesisStartedAt = performance.now();
            };
            synthesizer.visemeReceived = (_sender, event) => {
                if (generation !== state.speechGeneration) {
                    return;
                }
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
            closeSynthesizer(synthesizer);
            if (state.activeSynthesizer === synthesizer) {
                state.activeSynthesizer = null;
            }
            synthesizer = null;
            if (generation === state.speechGeneration) {
                await playFallbackAudio(text, generation);
            }
        } finally {
            closeSynthesizer(synthesizer);
            if (state.activeSynthesizer === synthesizer) {
                state.activeSynthesizer = null;
            }
            if (generation === state.speechGeneration) {
                stopMouthAnimation();
            }
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

    async function playFallbackAudio(text, generation) {
        if (generation !== state.speechGeneration) {
            return;
        }

        startGenericMouth();
        let audio = null;
        try {
            const avatar = currentAvatar();
            const languageCode = avatar?.languageCode || pageData.languageCode;
            const url = `${root.dataset.ttsFallbackUrl}?text=${encodeURIComponent(text.slice(0, 200))}&lang=${encodeURIComponent(languageCode)}`;
            audio = new Audio(url);
            state.activeAudio = audio;
            await audio.play();
            await new Promise(resolve => {
                audio.addEventListener("ended", resolve, { once: true });
                audio.addEventListener("error", resolve, { once: true });
                audio.addEventListener("abort", resolve, { once: true });
                audio.addEventListener("emptied", resolve, { once: true });
            });
        } catch {
            // The visible reply remains usable when audio is unavailable.
        } finally {
            if (state.activeAudio === audio) {
                state.activeAudio = null;
            }
            if (generation === state.speechGeneration) {
                stopMouthAnimation();
            }
        }
    }

    function stopSpeaking() {
        state.speechGeneration += 1;
        closeSynthesizer(state.activeSynthesizer);
        state.activeSynthesizer = null;
        if (state.activeAudio) {
            try {
                state.activeAudio.pause();
                state.activeAudio.src = "";
            } catch {
                // A detached audio element is already effectively stopped.
            }
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

    elements.menuToggle?.addEventListener("click", () => {
        const open = elements.barMenu.hidden;
        setPanel(elements.wallet, elements.walletToggle, false);
        setPanel(elements.barMenu, elements.menuToggle, open);
    });

    elements.walletToggle?.addEventListener("click", () => {
        const open = elements.wallet.hidden;
        setPanel(elements.barMenu, elements.menuToggle, false);
        setPanel(elements.wallet, elements.walletToggle, open);
    });

    elements.walletClose?.addEventListener("click", () => {
        setPanel(elements.wallet, elements.walletToggle, false);
    });

    elements.drinkAction?.addEventListener("click", () => {
        void sendInteractiveAction("drink");
    });

    elements.snackAction?.addEventListener("click", () => {
        void sendInteractiveAction("takeSnack");
    });

    elements.paymentSubmit?.addEventListener("click", () => {
        const denominations = Object.fromEntries(state.selectedTender.entries());
        void sendInteractiveAction("submitPayment", denominations);
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

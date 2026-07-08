(function () {
    "use strict";

    const root = document.querySelector("[data-classroom-call]");
    if (!root || typeof acs === "undefined") {
        return;
    }

    const classroomId = root.getAttribute("data-classroom-id");
    const displayName = root.getAttribute("data-display-name") || "Member";
    const isTeacher = root.getAttribute("data-is-teacher") === "true";
    let callParticipants = parseInt(root.getAttribute("data-call-participants") || "0", 10) || 0;
    const lobby = root.querySelector("[data-call-lobby]");
    const lobbyTitle = root.querySelector("[data-call-lobby-title]");
    const presenceLine = root.querySelector("[data-call-presence]");
    const joinLabel = root.querySelector("[data-call-join-label]");
    const stage = root.querySelector("[data-call-stage]");
    const status = root.querySelector("[data-call-status]");
    const liveStatus = root.querySelector("[data-call-status-live]");
    const timer = root.querySelector("[data-call-timer]");
    const countBadge = root.querySelector("[data-call-count]");
    const joinButton = root.querySelector("[data-call-join]");
    const muteButton = root.querySelector("[data-call-mute]");
    const cameraButton = root.querySelector("[data-call-camera]");
    const leaveButton = root.querySelector("[data-call-leave]");
    const grid = root.querySelector("[data-call-grid]");
    const localTile = root.querySelector("[data-local-tile]");
    const localVideoHost = root.querySelector("[data-local-video]");
    const localAvatar = root.querySelector("[data-local-avatar]");
    const lobbyAvatar = root.querySelector("[data-lobby-avatar]");

    let callAgent = null;
    let call = null;
    let deviceManager = null;
    let localVideoStream = null;
    let localRenderer = null;
    let timerHandle = null;
    let connectedAt = null;
    const remoteTiles = new Map(); // participant -> { tile, videoHost, renderers: Map<stream, renderer> }

    function initials(name) {
        const parts = name.trim().split(/\s+/).filter(Boolean);
        if (parts.length === 0) {
            return "?";
        }
        const first = parts[0][0];
        const last = parts.length > 1 ? parts[parts.length - 1][0] : "";
        return (first + last).toUpperCase();
    }

    if (lobbyAvatar) {
        lobbyAvatar.textContent = initials(displayName);
    }
    localAvatar.textContent = initials(displayName);

    function setStatus(text) {
        status.textContent = text;
    }

    // The lobby adapts to who you are and whether a call is running: teachers
    // can always start/join; students can only join a call in progress (the
    // server enforces the same rule on the token endpoint).
    function renderLobby(joinInFlight) {
        if (!joinButton) {
            return;
        }
        const active = callParticipants > 0;
        if (active) {
            lobbyTitle.textContent = "A call is in progress";
            presenceLine.textContent = callParticipants === 1
                ? "1 person is in the call."
                : `${callParticipants} people are in the call.`;
            presenceLine.hidden = false;
            joinLabel.textContent = "Join call";
            joinButton.disabled = Boolean(joinInFlight);
        } else if (isTeacher) {
            lobbyTitle.textContent = "Ready to start?";
            presenceLine.textContent = "No one is in the call yet.";
            presenceLine.hidden = false;
            joinLabel.textContent = "Start call";
            joinButton.disabled = Boolean(joinInFlight);
        } else {
            lobbyTitle.textContent = "Waiting for a teacher";
            presenceLine.textContent = "You can join once a teacher has started the call.";
            presenceLine.hidden = false;
            joinLabel.textContent = "Join call";
            joinButton.disabled = true;
        }
    }

    // Hanging up a call that is still connecting can fail silently in the
    // calling SDK, leaving a ghost participant. Retry once the call settles.
    function hangUpCall(activeCall) {
        const retryWhenSettled = () => {
            const onState = () => {
                if (activeCall.state === "Connected") {
                    activeCall.off("stateChanged", onState);
                    activeCall.hangUp().catch(() => { });
                } else if (activeCall.state === "Disconnected") {
                    activeCall.off("stateChanged", onState);
                }
            };
            activeCall.on("stateChanged", onState);
            onState();
        };

        if (activeCall.state === "Disconnected" || activeCall.state === "Disconnecting") {
            return;
        }
        activeCall.hangUp().then(() => {
            if (activeCall.state !== "Disconnected" && activeCall.state !== "Disconnecting") {
                retryWhenSettled();
            }
        }).catch(retryWhenSettled);
    }

    function antiforgeryToken() {
        const input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : "";
    }

    async function fetchCallToken() {
        const response = await fetch(`/Classroom/CallToken?id=${encodeURIComponent(classroomId)}`, {
            method: "POST",
            headers: {
                "RequestVerificationToken": antiforgeryToken(),
                "X-Requested-With": "XMLHttpRequest"
            }
        });

        if (!response.ok) {
            const problem = await response.json().catch(() => null);
            throw new Error(problem && problem.message ? problem.message : "Could not get a call token.");
        }

        return response.json();
    }

    function participantName(participant) {
        return participant.displayName || "Member";
    }

    function layoutGrid() {
        const count = Math.max(1, remoteTiles.size);
        const cols = count <= 1 ? 1 : count <= 4 ? 2 : count <= 9 ? 3 : 4;
        grid.style.setProperty("--call-cols", String(cols));
        grid.setAttribute("data-count", String(count));
        countBadge.textContent = String(remoteTiles.size + 1);
    }

    function ensureRemoteTile(participant) {
        let entry = remoteTiles.get(participant);
        if (entry) {
            return entry;
        }

        const tile = document.createElement("div");
        tile.className = "classroom-call-tile";
        const video = document.createElement("div");
        video.className = "classroom-call-video";
        const avatar = document.createElement("div");
        avatar.className = "classroom-call-avatar";
        avatar.textContent = initials(participantName(participant));
        const name = document.createElement("span");
        name.className = "classroom-call-name";
        const mutedIcon = document.createElement("span");
        mutedIcon.className = "material-symbols-outlined classroom-call-muted";
        mutedIcon.textContent = "mic_off";
        mutedIcon.hidden = !participant.isMuted;
        name.append(document.createTextNode(participantName(participant)), mutedIcon);
        tile.append(video, avatar, name);
        grid.appendChild(tile);

        participant.on("isMutedChanged", () => {
            mutedIcon.hidden = !participant.isMuted;
        });

        entry = { tile, videoHost: video, renderers: new Map() };
        remoteTiles.set(participant, entry);
        layoutGrid();
        return entry;
    }

    function removeRemoteTile(participant) {
        const entry = remoteTiles.get(participant);
        if (!entry) {
            return;
        }

        entry.renderers.forEach((renderer) => renderer.dispose());
        entry.tile.remove();
        remoteTiles.delete(participant);
        layoutGrid();
    }

    async function renderRemoteStream(participant, stream) {
        const entry = ensureRemoteTile(participant);
        if (entry.renderers.has(stream)) {
            return;
        }

        try {
            const renderer = new acs.VideoStreamRenderer(stream);
            const view = await renderer.createView({ scalingMode: "Crop" });
            entry.videoHost.appendChild(view.target);
            entry.renderers.set(stream, renderer);
            entry.tile.classList.add("has-video");
        } catch {
            // Stream became unavailable between the event and rendering.
        }
    }

    function dropRemoteStream(participant, stream) {
        const entry = remoteTiles.get(participant);
        const renderer = entry && entry.renderers.get(stream);
        if (renderer) {
            renderer.dispose();
            entry.renderers.delete(stream);
            if (entry.renderers.size === 0) {
                entry.tile.classList.remove("has-video");
            }
        }
    }

    function watchStream(participant, stream) {
        const sync = () => {
            if (stream.isAvailable) {
                renderRemoteStream(participant, stream);
            } else {
                dropRemoteStream(participant, stream);
            }
        };
        stream.on("isAvailableChanged", sync);
        sync();
    }

    function watchParticipant(participant) {
        ensureRemoteTile(participant);
        participant.videoStreams.forEach((stream) => watchStream(participant, stream));
        participant.on("videoStreamsUpdated", (event) => {
            event.added.forEach((stream) => watchStream(participant, stream));
            event.removed.forEach((stream) => dropRemoteStream(participant, stream));
        });
    }

    async function startLocalVideo() {
        const cameras = await deviceManager.getCameras();
        if (cameras.length === 0) {
            return null;
        }

        localVideoStream = new acs.LocalVideoStream(cameras[0]);
        localRenderer = new acs.VideoStreamRenderer(localVideoStream);
        const view = await localRenderer.createView({ scalingMode: "Crop" });
        localVideoHost.appendChild(view.target);
        localTile.classList.add("has-video");
        return localVideoStream;
    }

    function setControlState(button, iconOn, iconOff, isOn, labelOn, labelOff) {
        const icon = button.querySelector(".material-symbols-outlined");
        icon.textContent = isOn ? iconOn : iconOff;
        button.classList.toggle("is-off", !isOn);
        button.setAttribute("aria-label", isOn ? labelOn : labelOff);
        button.title = isOn ? labelOn : labelOff;
    }

    function startTimer() {
        connectedAt = Date.now();
        timer.hidden = false;
        timerHandle = setInterval(() => {
            const total = Math.floor((Date.now() - connectedAt) / 1000);
            const minutes = Math.floor(total / 60);
            const seconds = total % 60;
            const hours = Math.floor(minutes / 60);
            const mm = String(minutes % 60).padStart(2, "0");
            const ss = String(seconds).padStart(2, "0");
            timer.textContent = hours > 0 ? `${hours}:${mm}:${ss}` : `${minutes}:${ss}`;
        }, 1000);
    }

    function stopTimer() {
        if (timerHandle) {
            clearInterval(timerHandle);
            timerHandle = null;
        }
        timer.hidden = true;
        timer.textContent = "";
    }

    // Incremented on every join attempt and on hang-up, so an in-flight join
    // notices it was cancelled after each await and backs out.
    let joinSession = 0;
    let joining = false;

    // Call presence rides on the classroom chat hub: joining/leaving the call
    // is reported so lobbies elsewhere update live, and a closed tab drops off
    // the roster with its hub connection.
    let hub = null;
    let inCall = false;

    function notifyJoinedCall() {
        inCall = true;
        if (hub && hub.state === "Connected") {
            hub.invoke("JoinCall", classroomId).catch(() => { });
        }
    }

    function notifyLeftCall() {
        if (!inCall) {
            return;
        }
        inCall = false;
        if (hub && hub.state === "Connected") {
            hub.invoke("LeaveCall").catch(() => { });
        }
    }

    if (typeof signalR !== "undefined") {
        hub = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/classroom-chat")
            .withAutomaticReconnect()
            .build();

        hub.on("callChanged", (payload) => {
            callParticipants = payload && typeof payload.participantCount === "number"
                ? payload.participantCount
                : 0;
            renderLobby(joining);
        });

        const register = async () => {
            await hub.invoke("JoinClassroom", classroomId);
            if (inCall) {
                await hub.invoke("JoinCall", classroomId);
            }
        };

        hub.onreconnected(() => {
            register().catch(() => { });
        });
        hub.start().then(register).catch(() => { });
    }

    function disposeLocalVideo() {
        if (localRenderer) {
            localRenderer.dispose();
            localRenderer = null;
        }
        localVideoHost.replaceChildren();
        localTile.classList.remove("has-video");
        localVideoStream = null;
    }

    async function join() {
        if (joinButton.disabled) {
            return;
        }
        const session = ++joinSession;
        joining = true;
        joinButton.disabled = true;
        setStatus("Connecting…");

        // Show the stage right away so the call can be hung up while it is
        // still connecting.
        lobby.hidden = true;
        stage.hidden = false;
        root.classList.add("is-live");
        document.body.classList.add("call-in-progress");
        localTile.hidden = false;
        liveStatus.textContent = "Connecting…";
        setControlState(muteButton, "mic", "mic_off", true, "Mute microphone", "Unmute microphone");
        setControlState(cameraButton, "videocam", "videocam_off", true, "Turn camera off", "Turn camera on");
        layoutGrid();

        try {
            const info = await fetchCallToken();
            if (session !== joinSession) {
                return;
            }

            if (!callAgent) {
                const callClient = new acs.CallClient();
                const credential = new acs.AzureCommunicationTokenCredential(info.token);
                callAgent = await callClient.createCallAgent(credential, { displayName });
                deviceManager = await callClient.getDeviceManager();
                if (session !== joinSession) {
                    return;
                }
            }

            await deviceManager.askDevicePermission({ audio: true, video: true });
            if (session !== joinSession) {
                return;
            }

            const videoStream = await startLocalVideo();
            if (session !== joinSession) {
                disposeLocalVideo();
                return;
            }
            setControlState(cameraButton, "videocam", "videocam_off", Boolean(videoStream), "Turn camera off", "Turn camera on");

            const joined = callAgent.join(
                { groupId: info.groupCallId },
                videoStream ? { videoOptions: { localVideoStreams: [videoStream] } } : undefined);
            if (session !== joinSession) {
                hangUpCall(joined);
                return;
            }
            call = joined;

            joined.remoteParticipants.forEach(watchParticipant);
            joined.on("remoteParticipantsUpdated", (event) => {
                if (call !== joined) {
                    return;
                }
                event.added.forEach(watchParticipant);
                event.removed.forEach(removeRemoteTile);
            });
            joined.on("stateChanged", () => {
                if (call !== joined) {
                    return;
                }
                if (joined.state === "Connected") {
                    liveStatus.textContent = "Connected";
                    root.classList.add("is-connected");
                    joining = false;
                    notifyJoinedCall();
                    if (!timerHandle) {
                        startTimer();
                    }
                } else if (joined.state === "Disconnected") {
                    cleanUp();
                } else {
                    liveStatus.textContent = joined.state;
                }
            });
        } catch (error) {
            if (session === joinSession) {
                cleanUp();
                setStatus(error.message || "Could not join the call.");
            }
        }
    }

    function cleanUp() {
        remoteTiles.forEach((_, participant) => removeRemoteTile(participant));
        disposeLocalVideo();
        localTile.hidden = true;
        call = null;
        stopTimer();
        notifyLeftCall();

        lobby.hidden = false;
        stage.hidden = true;
        root.classList.remove("is-live", "is-connected");
        document.body.classList.remove("call-in-progress");
        joining = false;
        renderLobby(false);
        setStatus("Not connected");
    }

    muteButton.addEventListener("click", async () => {
        if (!call) {
            return;
        }

        if (call.isMuted) {
            await call.unmute();
        } else {
            await call.mute();
        }
        setControlState(muteButton, "mic", "mic_off", !call.isMuted, "Mute microphone", "Unmute microphone");
    });

    cameraButton.addEventListener("click", async () => {
        if (!call) {
            return;
        }

        if (localVideoStream && call.localVideoStreams.length > 0) {
            await call.stopVideo(localVideoStream);
            if (localRenderer) {
                localRenderer.dispose();
                localRenderer = null;
            }
            localVideoHost.replaceChildren();
            localTile.classList.remove("has-video");
            setControlState(cameraButton, "videocam", "videocam_off", false, "Turn camera off", "Turn camera on");
        } else {
            const videoStream = await startLocalVideo();
            if (videoStream) {
                await call.startVideo(videoStream);
                setControlState(cameraButton, "videocam", "videocam_off", true, "Turn camera off", "Turn camera on");
            }
        }
    });

    leaveButton.addEventListener("click", () => {
        joinSession++; // cancels a join that is still in flight
        const active = call;
        call = null;
        cleanUp();
        if (active) {
            hangUpCall(active);
        }
    });

    joinButton.addEventListener("click", join);
    renderLobby(false);

    window.addEventListener("beforeunload", () => {
        if (call) {
            call.hangUp();
        }
    });
})();

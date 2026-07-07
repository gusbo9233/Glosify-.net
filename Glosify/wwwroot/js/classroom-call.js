(function () {
    "use strict";

    const root = document.querySelector("[data-classroom-call]");
    if (!root || typeof acs === "undefined") {
        return;
    }

    const classroomId = root.getAttribute("data-classroom-id");
    const displayName = root.getAttribute("data-display-name") || "Member";
    const status = root.querySelector("[data-call-status]");
    const joinButton = root.querySelector("[data-call-join]");
    const muteButton = root.querySelector("[data-call-mute]");
    const cameraButton = root.querySelector("[data-call-camera]");
    const leaveButton = root.querySelector("[data-call-leave]");
    const grid = root.querySelector("[data-call-grid]");
    const localTile = root.querySelector("[data-local-tile]");
    const localVideoHost = root.querySelector("[data-local-video]");

    let callAgent = null;
    let call = null;
    let deviceManager = null;
    let localVideoStream = null;
    let localRenderer = null;
    const remoteTiles = new Map(); // participant -> { tile, renderers: Map<stream, renderer> }

    function setStatus(text) {
        status.textContent = text;
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

    function ensureRemoteTile(participant) {
        let entry = remoteTiles.get(participant);
        if (entry) {
            return entry;
        }

        const tile = document.createElement("div");
        tile.className = "classroom-call-tile";
        const video = document.createElement("div");
        video.className = "classroom-call-video";
        const name = document.createElement("span");
        name.className = "classroom-call-name";
        name.textContent = participantName(participant);
        tile.append(video, name);
        grid.appendChild(tile);

        entry = { tile, videoHost: video, renderers: new Map() };
        remoteTiles.set(participant, entry);
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
        localTile.hidden = false;
        return localVideoStream;
    }

    async function join() {
        joinButton.disabled = true;
        setStatus("Connecting…");

        try {
            const info = await fetchCallToken();

            if (!callAgent) {
                const callClient = new acs.CallClient();
                const credential = new acs.AzureCommunicationTokenCredential(info.token);
                callAgent = await callClient.createCallAgent(credential, { displayName });
                deviceManager = await callClient.getDeviceManager();
            }

            await deviceManager.askDevicePermission({ audio: true, video: true });
            const videoStream = await startLocalVideo();

            call = callAgent.join(
                { groupId: info.groupCallId },
                videoStream ? { videoOptions: { localVideoStreams: [videoStream] } } : undefined);

            call.remoteParticipants.forEach(watchParticipant);
            call.on("remoteParticipantsUpdated", (event) => {
                event.added.forEach(watchParticipant);
                event.removed.forEach(removeRemoteTile);
            });
            call.on("stateChanged", () => {
                setStatus(call.state === "Connected" ? "Connected" : call.state);
                if (call.state === "Disconnected") {
                    cleanUp();
                }
            });

            joinButton.hidden = true;
            muteButton.hidden = false;
            cameraButton.hidden = false;
            leaveButton.hidden = false;
        } catch (error) {
            setStatus(error.message || "Could not join the call.");
            joinButton.disabled = false;
        }
    }

    function cleanUp() {
        remoteTiles.forEach((_, participant) => removeRemoteTile(participant));
        if (localRenderer) {
            localRenderer.dispose();
            localRenderer = null;
        }
        localVideoHost.replaceChildren();
        localTile.hidden = true;
        localVideoStream = null;
        call = null;

        joinButton.hidden = false;
        joinButton.disabled = false;
        muteButton.hidden = true;
        cameraButton.hidden = true;
        leaveButton.hidden = true;
        setStatus("Not connected");
    }

    muteButton.addEventListener("click", async () => {
        if (!call) {
            return;
        }

        if (call.isMuted) {
            await call.unmute();
            muteButton.textContent = "Mute";
        } else {
            await call.mute();
            muteButton.textContent = "Unmute";
        }
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
            localTile.hidden = true;
            cameraButton.textContent = "Camera on";
        } else {
            const videoStream = await startLocalVideo();
            if (videoStream) {
                await call.startVideo(videoStream);
                cameraButton.textContent = "Camera off";
            }
        }
    });

    leaveButton.addEventListener("click", async () => {
        if (call) {
            await call.hangUp();
        }
        cleanUp();
    });

    joinButton.addEventListener("click", join);

    window.addEventListener("beforeunload", () => {
        if (call) {
            call.hangUp();
        }
    });
})();

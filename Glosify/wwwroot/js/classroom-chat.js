(function () {
    "use strict";

    const root = document.querySelector("[data-classroom-chat]");
    if (!root || typeof signalR === "undefined") {
        return;
    }

    const classroomId = root.getAttribute("data-classroom-id");
    const list = root.querySelector("[data-chat-messages]");
    const emptyState = root.querySelector("[data-chat-empty]");
    const form = root.querySelector("[data-chat-form]");
    const input = root.querySelector("[data-chat-input]");
    const sendButton = root.querySelector("[data-chat-send]");
    const status = root.querySelector("[data-chat-status]");

    let currentUserId = null;
    let hasMessages = false;

    function setStatus(text) {
        if (status) {
            status.textContent = text;
        }
    }

    function formatTime(value) {
        const date = new Date(value);
        return date.toLocaleString(undefined, {
            month: "short", day: "numeric", hour: "2-digit", minute: "2-digit"
        });
    }

    function appendMessage(message) {
        if (!hasMessages && emptyState) {
            emptyState.remove();
            hasMessages = true;
        }

        const item = document.createElement("article");
        item.className = "classroom-chat-message" + (message.userId === currentUserId ? " is-own" : "");

        const meta = document.createElement("div");
        meta.className = "classroom-chat-meta";
        const author = document.createElement("strong");
        author.textContent = message.authorName;
        const time = document.createElement("span");
        time.className = "book-meta";
        time.textContent = formatTime(message.createdAt);
        meta.append(author, time);

        const body = document.createElement("p");
        body.className = "classroom-chat-body";
        body.textContent = message.body;

        item.append(meta, body);
        list.appendChild(item);
        list.scrollTop = list.scrollHeight;
    }

    function showEmpty(text) {
        if (emptyState) {
            emptyState.textContent = text;
        }
    }

    async function loadHistory() {
        const response = await fetch(`/Classroom/ChatHistory?id=${encodeURIComponent(classroomId)}`, {
            headers: { "X-Requested-With": "XMLHttpRequest" }
        });
        if (!response.ok) {
            showEmpty("Chat history could not be loaded.");
            return;
        }

        const data = await response.json();
        currentUserId = data.currentUserId;
        if (data.messages.length === 0) {
            showEmpty("No messages yet. Say hi!");
            return;
        }

        data.messages.forEach(appendMessage);
    }

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/classroom-chat")
        .withAutomaticReconnect()
        .build();

    connection.on("messageReceived", appendMessage);

    connection.onreconnecting(() => setStatus("Reconnecting…"));
    connection.onreconnected(async () => {
        setStatus("Connected");
        await connection.invoke("JoinClassroom", classroomId);
    });
    connection.onclose(() => {
        setStatus("Disconnected");
        sendButton.disabled = true;
    });

    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        const body = input.value.trim();
        if (!body) {
            return;
        }

        sendButton.disabled = true;
        try {
            await connection.invoke("SendMessage", classroomId, body);
            input.value = "";
        } catch (error) {
            setStatus(error.message ? error.message.replace(/^.*HubException: /, "") : "Message failed to send.");
        } finally {
            sendButton.disabled = false;
            input.focus();
        }
    });

    (async () => {
        try {
            await loadHistory();
            await connection.start();
            await connection.invoke("JoinClassroom", classroomId);
            setStatus("Connected");
            sendButton.disabled = false;
        } catch {
            setStatus("Chat is unavailable right now.");
        }
    })();
})();

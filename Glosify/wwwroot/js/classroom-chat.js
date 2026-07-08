(function () {
    "use strict";

    const root = document.querySelector("[data-classroom-chat]");
    if (!root || typeof signalR === "undefined") {
        return;
    }

    const classroomId = root.getAttribute("data-classroom-id");
    const toggleButton = root.querySelector("[data-chat-toggle]");
    const closeButton = root.querySelector("[data-chat-close]");
    const chatWindow = root.querySelector("[data-chat-window]");
    const unreadBadge = root.querySelector("[data-chat-unread]");
    const list = root.querySelector("[data-chat-messages]");
    const form = root.querySelector("[data-chat-form]");
    const input = root.querySelector("[data-chat-input]");
    const sendButton = root.querySelector("[data-chat-send]");
    const status = root.querySelector("[data-chat-status]");

    let currentUserId = null;
    let connected = false;
    let unreadCount = parseInt(root.getAttribute("data-unread-count") || "0", 10) || 0;

    function isOpen() {
        return !chatWindow.hidden;
    }

    function setStatus(text) {
        status.textContent = text;
    }

    function renderUnread() {
        if (unreadCount > 0) {
            unreadBadge.textContent = unreadCount > 99 ? "99+" : String(unreadCount);
            unreadBadge.hidden = false;
        } else {
            unreadBadge.hidden = true;
        }
    }

    renderUnread();

    // Keep the chat button/panel to the left of the assistant, which shares the corner.
    const assistantFloat = document.querySelector(".assistant-float");

    function positionNextToAssistant() {
        if (!assistantFloat) {
            return;
        }
        requestAnimationFrame(() => {
            const width = assistantFloat.getBoundingClientRect().width;
            root.style.setProperty("--assistant-offset", `${Math.ceil(width) + 14}px`);
        });
    }

    if (!assistantFloat) {
        root.style.setProperty("--assistant-offset", "0px");
    } else {
        positionNextToAssistant();
        window.addEventListener("resize", positionNextToAssistant);
        const assistantWindow = assistantFloat.querySelector("[data-assistant-window]");
        if (assistantWindow) {
            new MutationObserver(positionNextToAssistant)
                .observe(assistantWindow, { attributes: true, attributeFilter: ["hidden"] });
        }
    }

    function formatTime(value) {
        const date = new Date(value);
        return date.toLocaleString(undefined, {
            month: "short", day: "numeric", hour: "2-digit", minute: "2-digit"
        });
    }

    function showEmpty(text) {
        list.replaceChildren();
        const empty = document.createElement("div");
        empty.className = "assistant-empty";
        empty.setAttribute("data-chat-empty", "");
        const icon = document.createElement("span");
        icon.className = "material-symbols-outlined";
        icon.setAttribute("aria-hidden", "true");
        icon.textContent = "forum";
        const title = document.createElement("strong");
        title.textContent = "Class chat";
        const detail = document.createElement("span");
        detail.textContent = text;
        empty.append(icon, title, detail);
        list.appendChild(empty);
    }

    function appendMessage(message) {
        const empty = list.querySelector("[data-chat-empty]");
        if (empty) {
            empty.remove();
        }

        const isOwn = message.userId === currentUserId;
        const item = document.createElement("article");
        item.className = "assistant-message classroom-chat-message" + (isOwn ? " is-own" : "");

        const meta = document.createElement("div");
        meta.className = "classroom-chat-meta";
        const author = document.createElement("strong");
        author.textContent = isOwn ? "You" : message.authorName;
        const time = document.createElement("span");
        time.textContent = formatTime(message.createdAt);
        meta.append(author, time);

        const bubble = document.createElement("div");
        bubble.className = "assistant-bubble classroom-chat-bubble";
        bubble.textContent = message.body;

        item.append(meta, bubble);
        list.appendChild(item);
        list.scrollTop = list.scrollHeight;
    }

    async function loadHistory() {
        showEmpty("Loading messages…");
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

        list.replaceChildren();
        data.messages.forEach(appendMessage);
    }

    async function open() {
        chatWindow.hidden = false;
        root.classList.add("is-open");
        toggleButton.setAttribute("aria-expanded", "true");
        unreadCount = 0;
        renderUnread();
        positionNextToAssistant();
        // Refetching also marks the messages as read on the server.
        await loadHistory();
        input.focus();
    }

    function close() {
        chatWindow.hidden = true;
        root.classList.remove("is-open");
        toggleButton.setAttribute("aria-expanded", "false");
        positionNextToAssistant();
    }

    toggleButton.addEventListener("click", () => {
        if (isOpen()) {
            close();
        } else {
            open();
        }
    });

    closeButton.addEventListener("click", close);

    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hubs/classroom-chat")
        .withAutomaticReconnect()
        .build();

    connection.on("messageReceived", (message) => {
        if (isOpen()) {
            appendMessage(message);
        } else {
            unreadCount += 1;
            renderUnread();
        }
    });

    connection.onreconnecting(() => setStatus("Reconnecting…"));
    connection.onreconnected(async () => {
        setStatus("Connected");
        await connection.invoke("JoinClassroom", classroomId);
    });
    connection.onclose(() => {
        setStatus("Disconnected");
        connected = false;
        sendButton.disabled = true;
    });

    form.addEventListener("submit", async (event) => {
        event.preventDefault();
        const body = input.value.trim();
        if (!body || !connected) {
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
            await connection.start();
            await connection.invoke("JoinClassroom", classroomId);
            setStatus("Connected");
            connected = true;
            sendButton.disabled = false;
        } catch {
            setStatus("Chat is unavailable right now.");
        }
    })();
})();

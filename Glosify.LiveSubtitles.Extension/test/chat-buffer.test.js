import test from "node:test";
import assert from "node:assert/strict";
import "../lib/chat-buffer.js";

const { ChatBuffer } = globalThis.GlosifySubtitleChat;

test("builds one live message from deltas and commits it on done", () => {
  const chat = new ChatBuffer();
  chat.apply({ stream: "translation", delta: "God ", isFinal: false });
  chat.apply({ stream: "translation", delta: "morgon", isFinal: false });
  const result = chat.apply({
    stream: "translation",
    delta: "",
    isFinal: true,
    clientTimestamp: 123,
  });

  assert.deepEqual(result, { changed: true, committed: true });
  assert.deepEqual(chat.messages, [{ text: "God morgon", timestamp: 123 }]);
  assert.equal(chat.translation, "");
});

test("ignores source speech so the overlay remains translation-only", () => {
  const chat = new ChatBuffer();
  chat.apply({ stream: "source", delta: "Good morning", isFinal: false });
  chat.apply({ stream: "translation", delta: "Guten Morgen", isFinal: false });
  chat.apply({ stream: "translation", isFinal: true, clientTimestamp: 456 });

  assert.deepEqual(chat.messages[0], { text: "Guten Morgen", timestamp: 456 });
});

test("bounds both live text and retained chat history", () => {
  const chat = new ChatBuffer({
    maximumMessages: 2,
    maximumTranslationCharacters: 5,
  });
  for (const text of ["123456", "second", "third"]) {
    chat.apply({ stream: "translation", delta: text, isFinal: false });
    chat.apply({ stream: "translation", isFinal: true });
  }

  assert.deepEqual(chat.messages.map(message => message.text), ["econd", "third"]);
});

test("clear removes finalized and partial transcript text", () => {
  const chat = new ChatBuffer();
  chat.apply({ stream: "translation", delta: "partial", isFinal: false });
  chat.apply({ stream: "source", delta: "source", isFinal: false });
  chat.clear();

  assert.equal(chat.translation, "");
  assert.deepEqual(chat.messages, []);
});

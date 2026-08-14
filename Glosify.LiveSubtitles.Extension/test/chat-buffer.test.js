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

test("Enhanced subtitles commit completed sentences as separate chat bubbles", () => {
  const chat = new ChatBuffer();
  const result = chat.apply({
    stream: "translation",
    delta: "Good morning. How are",
    isFinal: false,
    clientTimestamp: 234,
  });

  assert.deepEqual(result, { changed: true, committed: true });
  assert.deepEqual(chat.messages, [{ text: "Good morning.", timestamp: 234 }]);
  assert.equal(chat.translation, "How are");

  chat.apply({
    stream: "translation",
    delta: " you? I am well",
    isFinal: false,
    clientTimestamp: 235,
  });
  chat.apply({ stream: "translation", isFinal: true, clientTimestamp: 236 });

  assert.deepEqual(chat.messages, [
    { text: "Good morning.", timestamp: 234 },
    { text: "How are you?", timestamp: 235 },
    { text: "I am well", timestamp: 236 },
  ]);
});

test("Enhanced subtitles commit a sentence ending at the delta boundary", () => {
  const chat = new ChatBuffer();
  const result = chat.apply({
    stream: "translation",
    delta: "Good morning.",
    isFinal: false,
    clientTimestamp: 237,
  });

  assert.deepEqual(result, { changed: true, committed: true });
  assert.deepEqual(chat.messages, [{ text: "Good morning.", timestamp: 237 }]);
  assert.equal(chat.translation, "");
});

test("Enhanced subtitles split unusually long speech at a word boundary", () => {
  const chat = new ChatBuffer({ maximumBubbleCharacters: 12 });
  chat.apply({
    stream: "translation",
    delta: "one two three four",
    isFinal: false,
    clientTimestamp: 345,
  });

  assert.deepEqual(chat.messages, [{ text: "one two", timestamp: 345 }]);
  assert.equal(chat.translation, "three four");
});

test("Scribe revisions replace live text before the finalized segment commits", () => {
  const chat = new ChatBuffer();
  chat.apply({ stream: "translation", delta: "Good", replace: true, isFinal: false });
  chat.apply({ stream: "translation", delta: "Good morning", replace: true, isFinal: false });
  chat.apply({
    stream: "translation",
    delta: "Good morning!",
    replace: true,
    isFinal: true,
    clientTimestamp: 321,
  });

  assert.equal(chat.translation, "");
  assert.deepEqual(chat.messages, [{ text: "Good morning!", timestamp: 321 }]);
});

test("Scribe finalized replacements remain one provider-defined bubble", () => {
  const chat = new ChatBuffer({ maximumBubbleCharacters: 12 });
  chat.apply({
    stream: "translation",
    delta: "First sentence. Second sentence.",
    replace: true,
    isFinal: true,
    clientTimestamp: 322,
  });

  assert.deepEqual(chat.messages, [{
    text: "First sentence. Second sentence.",
    timestamp: 322,
  }]);
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

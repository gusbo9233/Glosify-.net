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

test("Scribe partial replacements commit completed sentences without duplicating them", () => {
  const chat = new ChatBuffer();
  chat.apply({
    stream: "translation",
    sequence: 7,
    delta: "First sentence. Second",
    replace: true,
    isFinal: false,
    clientTimestamp: 322,
  });
  assert.deepEqual(chat.messages, []);
  assert.equal(chat.translation, "First sentence. Second");

  chat.apply({
    stream: "translation",
    sequence: 7,
    delta: "First sentence. Second sentence. Third",
    replace: true,
    isFinal: false,
    clientTimestamp: 323,
  });
  assert.deepEqual(chat.messages, [
    { text: "First sentence.", timestamp: 323 },
  ]);
  assert.equal(chat.translation, "Second sentence. Third");

  chat.apply({
    stream: "translation",
    sequence: 7,
    delta: "First sentence. Second sentence. Third sentence.",
    replace: true,
    isFinal: true,
    clientTimestamp: 324,
  });
  assert.deepEqual(chat.messages, [
    { text: "First sentence.", timestamp: 323 },
    { text: "Second sentence.", timestamp: 324 },
    { text: "Third sentence.", timestamp: 324 },
  ]);
  assert.equal(chat.translation, "");
});

test("Scribe waits for text after terminal punctuation before committing a partial", () => {
  const chat = new ChatBuffer();
  chat.apply({
    stream: "translation",
    sequence: 8,
    delta: "First sentence.",
    replace: true,
    isFinal: false,
    clientTimestamp: 325,
  });

  assert.deepEqual(chat.messages, []);
  assert.equal(chat.translation, "First sentence.");

  chat.apply({
    stream: "translation",
    sequence: 8,
    delta: "First sentence. Second sentence.",
    replace: true,
    isFinal: true,
    clientTimestamp: 326,
  });
  assert.deepEqual(chat.messages, [
    { text: "First sentence.", timestamp: 326 },
    { text: "Second sentence.", timestamp: 326 },
  ]);
});

test("Scribe does not commit punctuation that changes in the next partial", () => {
  const chat = new ChatBuffer();
  chat.apply({
    stream: "translation",
    sequence: 9,
    delta: "The party is in trouble. More",
    replace: true,
    isFinal: false,
    clientTimestamp: 327,
  });
  chat.apply({
    stream: "translation",
    sequence: 9,
    delta: "The party is in a tense situation after the vote. More details",
    replace: true,
    isFinal: false,
    clientTimestamp: 328,
  });

  assert.deepEqual(chat.messages, []);
  assert.equal(chat.translation, "The party is in a tense situation after the vote. More details");

  chat.apply({
    stream: "translation",
    sequence: 9,
    delta: "The party is in a tense situation after the vote. More details follow",
    replace: true,
    isFinal: false,
    clientTimestamp: 329,
  });
  assert.deepEqual(chat.messages, [{
    text: "The party is in a tense situation after the vote.",
    timestamp: 329,
  }]);
  assert.equal(chat.translation, "More details follow");
});

test("Scribe splits a long finalized sentence at word boundaries", () => {
  const chat = new ChatBuffer({ maximumBubbleCharacters: 16 });
  chat.apply({
    stream: "translation",
    sequence: 10,
    delta: "One unusually long translated sentence finishes here.",
    replace: true,
    isFinal: true,
    clientTimestamp: 330,
  });

  assert.deepEqual(chat.messages, [
    { text: "One unusually", timestamp: 330 },
    { text: "long translated", timestamp: 330 },
    { text: "sentence", timestamp: 330 },
    { text: "finishes here.", timestamp: 330 },
  ]);
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

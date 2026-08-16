export function appendBounded(current, delta, maximumLength) {
  const combined = `${current ?? ""}${delta ?? ""}`;
  return combined.length <= maximumLength
    ? combined
    : combined.slice(combined.length - maximumLength);
}

const TRANSLATION_DELTA_TYPES = new Set([
  "session.output_transcript.delta",
  "response.text.delta",
  "response.output_text.delta",
  "response.output_audio_transcript.delta",
]);

const TRANSLATION_FINAL_TYPES = new Set([
  "session.output_transcript.done",
  "response.text.done",
  "response.output_text.done",
  "response.output_audio_transcript.done",
]);

export function createRealtimeEventAccumulator(context, {
  idleFlushMs = 4_000,
  maximumLength = 32_000,
  maximumCompletedKeys = 512,
} = {}) {
  const buffers = new Map();
  const completed = new Set();

  function markCompleted(key) {
    completed.add(key);
    while (completed.size > maximumCompletedKeys) {
      completed.delete(completed.values().next().value);
    }
  }

  function apply(event, now = Date.now()) {
    if (!event || typeof event.type !== "string") {
      return null;
    }
    if (event.type === "glosify.translation.segment"
        || event.type === "glosify.translation.partial") {
      return normalizeRealtimeEvent(event, context);
    }

    const isDelta = TRANSLATION_DELTA_TYPES.has(event.type);
    const isFinal = TRANSLATION_FINAL_TYPES.has(event.type);
    if (!isDelta && !isFinal) {
      return null;
    }

    const { key, reusable } = eventKey(event);
    if (isDelta) {
      if (completed.has(key)) {
        if (!reusable) {
          return null;
        }
        completed.delete(key);
      }
      const delta = typeof event.delta === "string"
        ? event.delta
        : typeof event.text === "string" ? event.text : "";
      if (!delta) {
        return null;
      }
      let buffer = buffers.get(key);
      if (!buffer) {
        buffer = { text: "", sequence: context.nextSequence(), lastUpdatedAt: now };
        buffers.set(key, buffer);
      }
      buffer.text = appendBounded(buffer.text, delta, maximumLength);
      buffer.lastUpdatedAt = now;
      return normalizedReplacement(context, buffer.sequence, buffer.text, false, now);
    }

    if (completed.has(key)) {
      return null;
    }
    markCompleted(key);
    const buffer = buffers.get(key);
    buffers.delete(key);
    const finalText = [event.text, event.transcript]
      .find(value => typeof value === "string" && value.trim())
      ?? buffer?.text
      ?? "";
    if (!finalText.trim()) {
      return null;
    }
    return normalizedReplacement(
      context,
      buffer?.sequence ?? context.nextSequence(),
      appendBounded("", finalText, maximumLength),
      true,
      now);
  }

  function flushIdle(now = Date.now()) {
    const result = [];
    for (const [key, buffer] of buffers) {
      if (now - buffer.lastUpdatedAt < idleFlushMs) {
        continue;
      }
      buffers.delete(key);
      markCompleted(key);
      if (buffer.text.trim()) {
        result.push(normalizedReplacement(
          context,
          buffer.sequence,
          buffer.text,
          true,
          now));
      }
    }
    return result;
  }

  return Object.freeze({ apply, flushIdle });
}

function eventKey(event) {
  const outputIndex = Number.isInteger(event.output_index) ? event.output_index : 0;
  const contentIndex = Number.isInteger(event.content_index) ? event.content_index : 0;
  if (typeof event.response_id === "string" && event.response_id) {
    return { key: `response:${event.response_id}:${outputIndex}:${contentIndex}`, reusable: false };
  }
  if (typeof event.item_id === "string" && event.item_id) {
    return { key: `item:${event.item_id}:${contentIndex}`, reusable: false };
  }
  return { key: `stream:${contentIndex}`, reusable: true };
}

function normalizedReplacement(context, sequence, text, isFinal, now) {
  return {
    sessionId: context.sessionId,
    stream: "translation",
    language: context.targetLanguage,
    sequence,
    delta: text,
    replace: true,
    isFinal,
    clientTimestamp: now,
  };
}

export function normalizeRealtimeEvent(event, context) {
  if (!event || typeof event.type !== "string") {
    return null;
  }

  if ((event.type === "glosify.translation.segment"
      || event.type === "glosify.translation.partial")
      && typeof event.text === "string"
      && event.text.trim()) {
    return {
      sessionId: context.sessionId,
      stream: "translation",
      language: event.targetLanguage ?? context.targetLanguage,
      sourceLanguage: event.sourceLanguage ?? null,
      sequence: Number.isInteger(event.sequence) ? event.sequence : context.nextSequence(),
      delta: event.text,
      replace: true,
      isFinal: event.type === "glosify.translation.segment",
      clientTimestamp: Date.now(),
    };
  }

  const isTranslation = TRANSLATION_DELTA_TYPES.has(event.type)
    || TRANSLATION_FINAL_TYPES.has(event.type);
  if (!isTranslation) {
    return null;
  }

  const isFinal = TRANSLATION_FINAL_TYPES.has(event.type);
  const delta = typeof event.delta === "string"
    ? event.delta
    : typeof event.text === "string"
      ? event.text
    : isFinal && typeof event.transcript === "string"
      ? event.transcript
      : "";

  return {
    sessionId: context.sessionId,
    stream: "translation",
    language: context.targetLanguage,
    sequence: context.nextSequence(),
    delta,
    isFinal,
    clientTimestamp: Date.now(),
  };
}

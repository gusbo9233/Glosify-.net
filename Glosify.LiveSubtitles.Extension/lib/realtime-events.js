export function appendBounded(current, delta, maximumLength) {
  const combined = `${current ?? ""}${delta ?? ""}`;
  return combined.length <= maximumLength
    ? combined
    : combined.slice(combined.length - maximumLength);
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

  const translationEventTypes = new Set([
    "session.output_transcript.delta",
    "session.output_transcript.done",
    "response.text.delta",
    "response.text.done",
    "response.output_text.delta",
    "response.output_text.done",
    "response.output_audio_transcript.delta",
    "response.output_audio_transcript.done",
  ]);
  const isTranslation = translationEventTypes.has(event.type);
  if (!isTranslation) {
    return null;
  }

  const isFinal = event.type.endsWith(".done") || event.type.endsWith(".completed");
  const delta = typeof event.delta === "string"
    ? event.delta
    : !isFinal && typeof event.text === "string"
      ? event.text
    : isFinal && typeof event.transcript === "string"
      ? ""
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

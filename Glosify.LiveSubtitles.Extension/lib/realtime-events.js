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

  const sourceEventTypes = new Set([
    "session.input_transcript.delta",
    "session.input_transcript.done",
    "conversation.item.input_audio_transcription.delta",
    "conversation.item.input_audio_transcription.completed",
  ]);
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
  const isSource = sourceEventTypes.has(event.type);
  const isTranslation = translationEventTypes.has(event.type);
  if (!isSource && !isTranslation) {
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
    stream: isSource ? "source" : "translation",
    language: isSource ? (event.language ?? "und") : context.targetLanguage,
    sequence: context.nextSequence(),
    delta,
    isFinal,
    clientTimestamp: Date.now(),
  };
}

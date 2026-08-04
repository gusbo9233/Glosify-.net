(() => {
  class ChatBuffer {
    constructor({ maximumMessages = 30, maximumTranslationCharacters = 800, maximumSourceCharacters = 400 } = {}) {
      this.maximumMessages = maximumMessages;
      this.maximumTranslationCharacters = maximumTranslationCharacters;
      this.maximumSourceCharacters = maximumSourceCharacters;
      this.messages = [];
      this.translation = "";
      this.source = "";
    }

    apply(event) {
      if (!event || (event.stream !== "translation" && event.stream !== "source")) {
        return { changed: false, committed: false };
      }

      if (event.stream === "source") {
        if (event.delta) {
          this.source = appendBounded(
            this.source,
            event.delta,
            this.maximumSourceCharacters);
        }
        return { changed: Boolean(event.delta), committed: false };
      }

      if (event.delta) {
        this.translation = appendBounded(
          this.translation,
          event.delta,
          this.maximumTranslationCharacters);
      }
      if (!event.isFinal) {
        return { changed: Boolean(event.delta), committed: false };
      }

      const text = this.translation.trim();
      const source = this.source.trim();
      this.translation = "";
      this.source = "";
      if (!text) {
        return { changed: true, committed: false };
      }

      this.messages.push({
        text,
        source,
        timestamp: Number.isFinite(event.clientTimestamp)
          ? event.clientTimestamp
          : Date.now(),
      });
      if (this.messages.length > this.maximumMessages) {
        this.messages.splice(0, this.messages.length - this.maximumMessages);
      }
      return { changed: true, committed: true };
    }

    clear() {
      this.messages.length = 0;
      this.translation = "";
      this.source = "";
    }
  }

  function appendBounded(current, delta, maximumLength) {
    const combined = `${current ?? ""}${delta ?? ""}`;
    return combined.length <= maximumLength
      ? combined
      : combined.slice(combined.length - maximumLength);
  }

  globalThis.GlosifySubtitleChat = Object.freeze({ ChatBuffer });
})();

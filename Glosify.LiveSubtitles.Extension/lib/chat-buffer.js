(() => {
  class ChatBuffer {
    constructor({ maximumMessages = 30, maximumTranslationCharacters = 800 } = {}) {
      this.maximumMessages = maximumMessages;
      this.maximumTranslationCharacters = maximumTranslationCharacters;
      this.messages = [];
      this.translation = "";
    }

    apply(event) {
      if (!event || event.stream !== "translation") {
        return { changed: false, committed: false };
      }

      if (event.delta) {
        this.translation = appendBounded(
          event.replace ? "" : this.translation,
          event.delta,
          this.maximumTranslationCharacters);
      }
      if (!event.isFinal) {
        return { changed: Boolean(event.delta), committed: false };
      }

      const text = this.translation.trim();
      this.translation = "";
      if (!text) {
        return { changed: true, committed: false };
      }

      this.messages.push({
        text,
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

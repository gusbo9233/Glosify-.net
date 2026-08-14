(() => {
  class ChatBuffer {
    constructor({
      maximumMessages = 30,
      maximumTranslationCharacters = 800,
      maximumBubbleCharacters = 180,
    } = {}) {
      this.maximumMessages = maximumMessages;
      this.maximumTranslationCharacters = maximumTranslationCharacters;
      this.maximumBubbleCharacters = maximumBubbleCharacters;
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
      const timestamp = Number.isFinite(event.clientTimestamp)
        ? event.clientTimestamp
        : Date.now();
      const committedBubbles = event.replace
        ? 0
        : this.commitEnhancedBubbles(timestamp);
      if (!event.isFinal) {
        return {
          changed: Boolean(event.delta) || committedBubbles > 0,
          committed: committedBubbles > 0,
        };
      }

      const text = this.translation.trim();
      this.translation = "";
      if (!text) {
        return { changed: true, committed: committedBubbles > 0 };
      }

      this.pushMessage(text, timestamp);
      return { changed: true, committed: true };
    }

    commitEnhancedBubbles(timestamp) {
      let committed = 0;
      while (this.translation) {
        const sentenceEnd = findCompletedSentenceEnd(this.translation);
        const splitAt = sentenceEnd > 0
          ? sentenceEnd
          : findLengthSplit(this.translation, this.maximumBubbleCharacters);
        if (splitAt <= 0) {
          break;
        }

        const text = this.translation.slice(0, splitAt).trim();
        this.translation = this.translation.slice(splitAt).trimStart();
        if (text) {
          this.pushMessage(text, timestamp);
          committed += 1;
        }
      }
      return committed;
    }

    pushMessage(text, timestamp) {
      this.messages.push({ text, timestamp });
      if (this.messages.length > this.maximumMessages) {
        this.messages.splice(0, this.messages.length - this.maximumMessages);
      }
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

  function findCompletedSentenceEnd(text) {
    const boundary = /[.!?…]+(?:["'”’»\)\]]+)?(?=\s|$)|[。！？]+(?:["'”’»）\]]+)?/u.exec(text);
    return boundary ? boundary.index + boundary[0].length : -1;
  }

  function findLengthSplit(text, maximumLength) {
    if (!Number.isFinite(maximumLength) || maximumLength < 1 || text.length <= maximumLength) {
      return -1;
    }
    const wordBoundary = text.lastIndexOf(" ", maximumLength);
    return wordBoundary > 0 ? wordBoundary : maximumLength;
  }

  globalThis.GlosifySubtitleChat = Object.freeze({ ChatBuffer });
})();

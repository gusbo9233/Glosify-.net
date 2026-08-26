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
      this.replacementSequence = null;
      this.previousReplacementSentences = [];
      this.committedReplacementSentences = 0;
    }

    apply(event) {
      if (!event || event.stream !== "translation") {
        return { changed: false, committed: false };
      }

      const timestamp = Number.isFinite(event.clientTimestamp)
        ? event.clientTimestamp
        : Date.now();
      if (event.replace) {
        return this.applyReplacement(event, timestamp);
      }

      if (event.delta) {
        this.translation = appendBounded(
          this.translation,
          event.delta,
          this.maximumTranslationCharacters);
      }
      const committedBubbles = this.commitEnhancedBubbles(timestamp);
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

    applyReplacement(event, timestamp) {
      if (Array.isArray(event.committedBubbles)
          && typeof event.pendingText === "string") {
        return this.applyServerReplacement(event, timestamp);
      }
      if (this.replacementSequence !== event.sequence) {
        this.replacementSequence = event.sequence;
        this.previousReplacementSentences = [];
        this.committedReplacementSentences = 0;
      }

      const text = appendBounded("", event.delta, this.maximumTranslationCharacters).trim();
      const split = splitSentences(text);
      let committed = 0;

      if (event.isFinal) {
        for (const sentence of split.completed.slice(this.committedReplacementSentences)) {
          committed += this.pushTextAsBubbles(sentence, timestamp);
        }
        committed += this.pushTextAsBubbles(split.remainder, timestamp);
        this.translation = "";
        this.resetReplacement();
        return { changed: true, committed: committed > 0 };
      }

      while (this.committedReplacementSentences < split.completed.length
          && this.committedReplacementSentences < this.previousReplacementSentences.length
          && split.completed[this.committedReplacementSentences]
            === this.previousReplacementSentences[this.committedReplacementSentences]) {
        committed += this.pushTextAsBubbles(
          split.completed[this.committedReplacementSentences],
          timestamp);
        this.committedReplacementSentences += 1;
      }

      this.previousReplacementSentences = split.completed;
      this.translation = [
        ...split.completed.slice(this.committedReplacementSentences),
        split.remainder,
      ].filter(Boolean).join(" ");
      return {
        changed: Boolean(event.delta) || committed > 0,
        committed: committed > 0,
      };
    }

    applyServerReplacement(event, timestamp) {
      let committed = 0;
      for (const bubble of event.committedBubbles) {
        if (typeof bubble === "string" && bubble.trim()) {
          this.pushMessage(bubble, timestamp);
          committed += 1;
        }
      }
      this.translation = appendBounded(
        "",
        event.pendingText,
        this.maximumTranslationCharacters).trim();
      if (event.isFinal) {
        this.resetReplacement();
      } else {
        this.replacementSequence = event.sequence;
      }
      return {
        changed: committed > 0 || Boolean(event.pendingText) || event.isFinal,
        committed: committed > 0,
      };
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

    pushTextAsBubbles(text, timestamp) {
      let remaining = text.trim();
      let committed = 0;
      while (remaining) {
        const splitAt = findLengthSplit(remaining, this.maximumBubbleCharacters);
        const bubble = splitAt > 0 ? remaining.slice(0, splitAt).trim() : remaining;
        remaining = splitAt > 0 ? remaining.slice(splitAt).trimStart() : "";
        if (bubble) {
          this.pushMessage(bubble, timestamp);
          committed += 1;
        }
      }
      return committed;
    }

    resetReplacement() {
      this.replacementSequence = null;
      this.previousReplacementSentences = [];
      this.committedReplacementSentences = 0;
    }

    clear() {
      this.messages.length = 0;
      this.translation = "";
      this.resetReplacement();
    }
  }

  function appendBounded(current, delta, maximumLength) {
    const combined = `${current ?? ""}${delta ?? ""}`;
    return combined.length <= maximumLength
      ? combined
      : combined.slice(combined.length - maximumLength);
  }

  function findCompletedSentenceEnd(text) {
    const boundary = /[.!?…]+(?:["'”’»)\]]+)?(?=\s|$)|[。！？]+(?:["'”’»）\]]+)?/u.exec(text);
    return boundary ? boundary.index + boundary[0].length : -1;
  }

  function splitSentences(text) {
    const completed = [];
    let remainder = text.trim();
    while (remainder) {
      const sentenceEnd = findCompletedSentenceEnd(remainder);
      if (sentenceEnd <= 0) {
        break;
      }
      const trailingText = remainder.slice(sentenceEnd).trimStart();
      completed.push(remainder.slice(0, sentenceEnd).trim());
      remainder = trailingText;
    }
    return { completed, remainder };
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

import test from "node:test";
import assert from "node:assert/strict";
import {
  setPartialCaptionsVisibility,
  setQuizLanguageVisibility,
} from "../lib/popup-visibility.js";

test("quiz-language visibility follows transcript saving without changing its value", () => {
  const classes = new Set(["control-group", "hidden"]);
  const group = {
    classList: {
      toggle(name, force) {
        if (force) {
          classes.add(name);
        } else {
          classes.delete(name);
        }
      },
    },
  };
  const quizLanguage = { value: "sv" };

  setQuizLanguageVisibility(group, false);
  assert.equal(classes.has("hidden"), true);
  assert.equal(quizLanguage.value, "sv");

  setQuizLanguageVisibility(group, true);
  assert.equal(classes.has("hidden"), false);
  assert.equal(quizLanguage.value, "sv");

  setQuizLanguageVisibility(group, false);
  assert.equal(classes.has("hidden"), true);
  assert.equal(quizLanguage.value, "sv");
});

test("partial-caption control is shown only for Cloudflare Scribe", () => {
  const classes = new Set(["switch-row"]);
  const group = {
    classList: {
      toggle(name, force) {
        if (force) {
          classes.add(name);
        } else {
          classes.delete(name);
        }
      },
    },
  };

  setPartialCaptionsVisibility(group, "enhanced");
  assert.equal(classes.has("hidden"), true);

  setPartialCaptionsVisibility(group, "scribe-cf");
  assert.equal(classes.has("hidden"), false);
});

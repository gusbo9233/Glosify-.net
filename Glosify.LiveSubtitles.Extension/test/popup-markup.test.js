import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const popupPath = new URL("../popup/popup.html", import.meta.url);

test("popup offers mode, optional language hint, target, quiz-language, and transcript controls", async () => {
  const markup = await readFile(popupPath, "utf8");

  assert.match(markup, /<select id="quiz-language">/);
  assert.match(markup, /<select id="translation-mode">/);
  assert.doesNotMatch(markup, /<select id="speech-provider">/);
  assert.match(markup, /<select id="source-language">/);
  assert.match(markup, /<input id="save-transcript" type="checkbox">/);
  assert.match(markup, /audio is streamed through Glosify to Microsoft services/);
  assert.match(markup, /Audio is not stored/);
  assert.match(markup, /Each started minute consumes credits/);
  assert.match(markup, /until you delete the transcript or account/);
  assert.doesNotMatch(markup, /id="bilingual"/);
});

test("quiz language is hidden until transcript saving is enabled", async () => {
  const markup = await readFile(popupPath, "utf8");

  assert.match(markup, /<div id="quiz-language-group" class="control-group hidden">/);
});

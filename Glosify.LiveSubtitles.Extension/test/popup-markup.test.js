import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const popupPath = new URL("../popup/popup.html", import.meta.url);

test("popup offers quiz-language selection and only the transcript checkbox", async () => {
  const markup = await readFile(popupPath, "utf8");

  assert.match(markup, /<select id="quiz-language">/);
  assert.match(markup, /<input id="save-transcript" type="checkbox">/);
  assert.doesNotMatch(markup, /id="bilingual"/);
});

import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const popupPath = new URL("../popup/popup.html", import.meta.url);

test("popup offers quiz-language selection and only the transcript checkbox", async () => {
  const markup = await readFile(popupPath, "utf8");

  assert.match(markup, /<select id="quiz-language">/);
  assert.match(markup, /<input id="save-transcript" type="checkbox">/);
  assert.match(markup, /audio is streamed through Glosify to Microsoft Foundry/);
  assert.match(markup, /Audio is not stored/);
  assert.match(markup, /until you delete the transcript or account/);
  assert.doesNotMatch(markup, /id="bilingual"/);
});

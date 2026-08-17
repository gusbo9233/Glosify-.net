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
  assert.match(markup, /<input id="transparent-subtitles" type="checkbox">/);
  assert.match(markup, /Transparent subtitle window/);
  assert.match(markup, /Shows only subtitle text until you hover over or focus the window/);
  assert.match(markup, /audio is streamed through Glosify to Microsoft services/);
  assert.match(markup, /Audio is not stored/);
  assert.match(markup, /Each started minute consumes credits/);
  assert.match(markup, /Provider-reported audio usage consumes credits/);
  assert.match(markup, /Mandatory consumer rights still apply/);
  assert.match(markup, /AI-generated captions and translations may be incorrect/);
  assert.match(markup, /Do not rely on them for safety-critical or high-stakes decisions/);
  assert.match(markup, /until you delete the transcript or account/);
  assert.match(markup, /href="https:\/\/glosify\.se\/Home\/Privacy"/);
  assert.match(markup, /href="https:\/\/glosify\.se\/Home\/Terms"/);
  assert.match(markup, /href="https:\/\/glosify\.se\/Home\/Support"/);
  assert.equal((markup.match(/target="_blank"/g) ?? []).length, 3);
  assert.doesNotMatch(markup, /id="bilingual"/);
});

test("quiz language is hidden until transcript saving is enabled", async () => {
  const markup = await readFile(popupPath, "utf8");

  assert.match(markup, /<div id="quiz-language-group" class="control-group hidden">/);
});

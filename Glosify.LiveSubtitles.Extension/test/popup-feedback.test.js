import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";

test("a failed sign-in remains visible and restores the connect action", async () => {
  const nodes = new Map();
  const document = {
    querySelector(selector) {
      if (!nodes.has(selector)) {
        const classes = new Set(["hidden"]);
        nodes.set(selector, {
          textContent: "", disabled: false,
          addEventListener() {},
          classList: {
            add: value => classes.add(value),
            remove: value => classes.delete(value),
            toggle(value, enabled) {
              if (enabled) classes.add(value);
              else classes.delete(value);
            },
            contains: value => classes.has(value),
          },
        });
      }
      return nodes.get(selector);
    },
  };
  let source = await readFile(new URL("../popup/popup.js", import.meta.url), "utf8");
  source = source.replace(/import[\s\S]*?from\s+"[^"]+";/g, "")
    .replace('void run("popup:get-state", {}, false);', "");
  const context = vm.createContext({
    document,
    chrome: { runtime: {
      onMessage: { addListener() {} },
      sendMessage: async () => ({ ok: false, error: "Sign-in cancelled. Try again." }),
    } },
  });
  vm.runInContext(source, context);
  await vm.runInContext('run("popup:sign-in")', context);
  assert.equal(nodes.get("#signed-out").classList.contains("hidden"), false);
  assert.equal(nodes.get("#signed-in").classList.contains("hidden"), true);
  assert.equal(nodes.get("#error").classList.contains("hidden"), false);
  assert.equal(nodes.get("#error").textContent, "Sign-in cancelled. Try again.");
  assert.equal(nodes.get("#connect").disabled, false);
  assert.equal(nodes.get("#connect").textContent, "Connect GlobeGlotter");
});

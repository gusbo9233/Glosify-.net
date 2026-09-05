import test from "node:test";
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";
const scripts = path.resolve(
    path.dirname(fileURLToPath(import.meta.url)),
    "../plugins/project-vault/scripts",
);
test("Stop hook requests one continuation, then explicitly reports blocked", () => {
    const code = `import sys,json;sys.path.insert(0,sys.argv[1]);from hook import decision;print(json.dumps([decision(True,False),decision(False,False),decision(False,True)]))`;
    const [current, first, second] = JSON.parse(
        execFileSync("python3", ["-c", code, scripts], { encoding: "utf8" }),
    );
    assert.deepEqual(current, {});
    assert.equal(first.decision, "block");
    assert.equal(second.continue, false);
    assert.match(second.stopReason, /blocked/);
    assert.equal(second.decision, undefined);
});

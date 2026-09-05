import test from "node:test";
import assert from "node:assert/strict";
import { analyzeSource } from "../analyzers/frontend.mjs";
const run = (code, file = "client.ts") => analyzeSource(file, code, "hash");
test("binds a literal fetch to its function and click handler", () => {
    const s = run(
        'async function submit(){ return fetch("/api/pay", {method:"POST"}); } button.addEventListener("click",submit);',
    );
    const fn = s.elements.find((e) => e.name === "submit"),
        ui = s.elements.find((e) => e.kind === "ui"),
        request = s.elements.find((e) => e.kind === "request");
    assert.equal(request.route, "/api/pay");
    assert.equal(request.verb, "POST");
    assert.equal(fn.async, true);
    assert.ok(
        s.relations.some(
            (r) =>
                r.source === ui.id &&
                r.target === fn.id &&
                r.kind === "handles",
        ),
    );
    assert.ok(
        s.relations.some(
            (r) =>
                r.source === fn.id &&
                r.target === request.id &&
                r.kind === "calls",
        ),
    );
});
test("inline event handlers own their requests", () => {
    const s = run(
        'button.addEventListener("click",async()=>{await fetch("/api/save")});',
    );
    const ui = s.elements.find((e) => e.kind === "ui");
    const edge = s.relations.find(
        (r) => r.source === ui.id && r.kind === "handles",
    );
    const request = s.elements.find((e) => e.kind === "request");
    assert.ok(edge);
    assert.ok(
        s.relations.some(
            (r) => r.source === edge.target && r.target === request.id,
        ),
    );
});
test("Map.get is not invented as an HTTP request", () => {
    assert.equal(
        run('const values=new Map(); values.get("secret");').elements.filter(
            (e) => e.kind === "request",
        ).length,
        0,
    );
});
test("computed targets remain unresolved and query values are not stored", () => {
    const s = run('fetch(url);fetch("/api/data?token=private-value");');
    assert.equal(
        s.elements.filter(
            (e) => e.kind === "request" && e.status === "unresolved",
        ).length,
        1,
    );
    assert.equal(JSON.stringify(s).includes("private-value"), false);
});
test("Razor forms bind controller/action and carry source line evidence", () => {
    const s = run(
        '\n<form asp-controller="Orders" asp-action="Pay" method="post"><button id="pay">Pay</button></form>',
        "Views/Orders/Index.cshtml",
    );
    const form = s.elements.find((e) => e.route === "/Orders/Pay");
    assert.equal(form.verb, "POST");
    assert.equal(form.evidence[0].line, 2);
    assert.equal(s.elements.filter((e) => e.entryPoint).length, 2);
});
test("React event props reference named handlers", () => {
    const s = run(
        'function save(){return fetch("/save")} export function Page(){return <button onClick={save}>Save</button>}',
        "Page.tsx",
    );
    const ui = s.elements.find((e) => e.kind === "ui"),
        fn = s.elements.find((e) => e.name === "save");
    assert.ok(
        s.relations.some((r) => r.source === ui.id && r.target === fn.id),
    );
});
test("named function identities survive blank lines and body changes", () => {
    const a = run("function save(){return 1;}"),
        b = run("\n\nfunction save(){return 2;}");
    assert.equal(
        a.elements.find((e) => e.name === "save").id,
        b.elements.find((e) => e.name === "save").id,
    );
});

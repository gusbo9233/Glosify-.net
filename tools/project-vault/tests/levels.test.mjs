import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, writeFile, readFile, rm, unlink } from "node:fs/promises";
import path from "node:path";
import os from "node:os";
import { mcp } from "./mcp.mjs";
test(
    "four levels: typed links, exact declarations, reviewed source and backwards compatibility",
    { timeout: 60000 },
    async (t) => {
        const root = await mkdtemp(path.join(os.tmpdir(), "vault-levels-"));
        const client = mcp(root),
            call = client.call;
        const original = `namespace Example;\npublic class Entity { public string Name {get;set;} = ""; }\npublic class Service {\n public int Run(int input) {\n${Array.from({ length: 230 }, (_, i) => " // explanation line " + i).join("\n")}\n return input + 1;\n }\n public string Run(string input) => input;\n}\n`;
        try {
            await writeFile(path.join(root, "App.cs"), original);
            const declarations = await call("vault_declarations", {
                path: "App.cs",
            });
            assert.deepEqual(
                declarations
                    .filter((d) => d.declarationId.includes(".Run("))
                    .map((d) => d.declarationId),
                [
                    "method:Example.Service.Run(int)",
                    "method:Example.Service.Run(string)",
                ],
            );
            let binding = await call("vault_declaration", {
                path: "App.cs",
                declarationId: "method:Example.Service.Run(int)",
            });
            assert.ok(binding.endLine - binding.line > 200);
            assert.match(binding.reviewedCode, /explanation line 229/);
            assert.doesNotMatch(binding.reviewedCode, /public string Run/);
            const ev = (b) => [
                {
                    id: "source",
                    path: b.path,
                    line: b.line,
                    endLine: b.endLine,
                    hash: b.hash,
                },
            ];
            const base = (id, kind = "explanation") => ({
                id,
                kind,
                title: id,
                summary: "A deliberately authored explanation.",
                category: "Example",
                markdown: "Understand the behavior through its contract.",
                links: [],
                evidence: ev(binding),
                dependencies: [],
                unknowns: [],
                diagrams: [],
            });
            const publish = (document, expectedVersion = 0) =>
                call("vault_save_document", {
                    document,
                    expectedVersion,
                    publish: true,
                });
            let model = base("entity", "model");
            model.primarySource = await call("vault_declaration", {
                path: "App.cs",
                declarationId: "type:Example.Entity",
            });
            model.evidence = ev(model.primarySource);
            model.fields = [
                {
                    name: "Name",
                    type: "string",
                    description: "A name.",
                    validation: [],
                    modelId: null,
                },
            ];
            await publish(model);
            const fn = {
                ...base("run", "function"),
                primarySource: binding,
                contract: {
                    purpose: "Increment input.",
                    signature: "int Run(int input)",
                    inputs: [
                        {
                            name: "input",
                            type: "int",
                            description: "The original value.",
                            modelId: null,
                        },
                    ],
                    output: {
                        name: "result",
                        type: "int",
                        description: "Input plus one.",
                        modelId: null,
                    },
                    checks: [],
                    async: false,
                    cancellation: "No asynchronous work.",
                    sideEffects: [],
                    concepts: [],
                    concerns: [],
                },
                detailLinks: [
                    {
                        targetId: "entity",
                        relation: "uses-model",
                        label: "Entity",
                    },
                ],
            };
            await publish(fn);
            const item = {
                id: "call",
                label: "Run the function",
                kind: "function-call",
                description: "Deliberate operation.",
                evidence: ["source"],
                links: [],
                markers: [],
                x: 0,
                y: 0,
                detailLinks: [
                    { targetId: "run", relation: "calls", label: "Run" },
                ],
            };
            const action = {
                ...base("action", "action"),
                diagrams: [
                    {
                        id: "execution",
                        title: "Execution",
                        kind: "process",
                        description: "An ordered call.",
                        nodes: [item, { ...item, id: "done", detailLinks: [] }],
                        transitions: [
                            {
                                id: "flow",
                                source: "call",
                                target: "done",
                                label: "Return",
                                trigger: "Run returns",
                                condition: "",
                                effect: "Receive input + 1",
                                description: "Value returned",
                                evidence: ["source"],
                                inputs: ["input"],
                                outputs: ["input + 1"],
                                sideEffects: [],
                                detailLinks: [
                                    {
                                        targetId: "entity",
                                        relation: "uses-model",
                                        label: "Entity",
                                    },
                                ],
                            },
                        ],
                    },
                ],
            };
            await publish(action);
            await publish({
                ...base("workflow", "workflow"),
                detailLinks: [
                    {
                        targetId: "action",
                        relation: "expands",
                        label: "Run action",
                    },
                ],
            });
            await publish({
                ...base("overview", "workflow-overview"),
                detailLinks: [
                    {
                        targetId: "workflow",
                        relation: "workflow",
                        label: "Workflow",
                    },
                ],
            });
            await t.test(
                "published links span the levels and old documents retain their defaults",
                async () => {
                    const library = await call("vault_documents");
                    assert.equal(
                        library.find((d) => d.id === "overview").kind,
                        "workflow-overview",
                    );
                    const old = base("legacy");
                    delete old.kind;
                    const saved = await publish(old);
                    assert.equal(saved.published.document.kind, "explanation");
                    assert.deepEqual(saved.published.document.detailLinks, []);
                    await assert.rejects(
                        readFile(
                            path.join(
                                root,
                                ".project-visualization/current.json",
                            ),
                        ),
                        /ENOENT/,
                    );
                    await call("vault_save_document_note", {
                        id: "note",
                        documentId: "legacy",
                        targetId: null,
                        markdown: "Keep this note.",
                    });
                    const revised = await publish(
                        { ...saved.published.document, kind: "workflow" },
                        1,
                    );
                    assert.equal(
                        revised.history[0].document.kind,
                        "explanation",
                    );
                    assert.equal(
                        (
                            await call("vault_document_notes", { id: "legacy" })
                        )[0].note.markdown,
                        "Keep this note.",
                    );
                },
            );
            await t.test(
                "publication rejects wrong link kinds, missing source and forged excerpts",
                async () => {
                    await assert.rejects(
                        publish(
                            {
                                ...fn,
                                detailLinks: [
                                    {
                                        targetId: "action",
                                        relation: "uses-model",
                                        label: "Wrong",
                                    },
                                ],
                            },
                            1,
                        ),
                        /requires a model/,
                    );
                    await assert.rejects(
                        publish({ ...fn, primarySource: null }, 1),
                        /primary source/,
                    );
                    await assert.rejects(
                        publish(
                            {
                                ...fn,
                                primarySource: {
                                    ...binding,
                                    reviewedCode: "invented code",
                                },
                            },
                            1,
                        ),
                        /differs/,
                    );
                    await assert.rejects(
                        publish(
                            { ...fn, primarySource: model.primarySource },
                            1,
                        ),
                        /method or constructor/,
                    );
                    const saved = await call("vault_document", { id: "run" });
                    assert.equal(saved.version, 1);
                    assert.equal(
                        saved.published.document.primarySource.reviewedCode,
                        binding.reviewedCode,
                    );
                },
            );
            await t.test(
                "a missing-detail request reuses a published function and links its parent",
                async () => {
                    const request = {
                        id: "document-call",
                        question: "Document this function call",
                        documentId: "action",
                        targetId: "done",
                        status: "open",
                        resultDocumentIds: [],
                        response: "",
                        version: 0,
                    };
                    await call("vault_save_request", {
                        request,
                        expectedVersion: 0,
                    });
                    const parent = await call("vault_document", {
                        id: "action",
                    });
                    parent.published.document.diagrams[0].nodes.find(
                        (n) => n.id === "done",
                    ).detailLinks = [
                        { targetId: "run", relation: "calls", label: "Run" },
                    ];
                    await publish(parent.published.document, parent.version);
                    await call("vault_save_request", {
                        request: {
                            ...request,
                            status: "answered",
                            resultDocumentIds: ["run"],
                            response:
                                "Reused the exact-function page and linked it from the requested call.",
                        },
                        expectedVersion: 1,
                    });
                    const answered = (
                        await call("vault_document_requests")
                    ).find((r) => r.id === "document-call");
                    assert.equal(answered.targetId, "done");
                    assert.equal(answered.status, "answered");
                    const updated = await call("vault_document", {
                        id: "action",
                    });
                    assert.equal(
                        updated.published.document.diagrams[0].nodes.find(
                            (n) => n.id === "done",
                        ).detailLinks[0].targetId,
                        "run",
                    );
                },
            );
            await t.test(
                "body edits retain reviewed source, require revision, and preserve history",
                async () => {
                    await writeFile(
                        path.join(root, "App.cs"),
                        original.replace(
                            "return input + 1",
                            "return input + 2",
                        ),
                    );
                    const current = await call("vault_document_source", {
                        id: "run",
                        version: null,
                    });
                    assert.equal(current.status, "changed");
                    assert.match(current.reviewed.reviewedCode, /input \+ 1/);
                    assert.match(current.current.reviewedCode, /input \+ 2/);
                    assert.equal(
                        (await call("vault_document_impacts")).find(
                            (d) => d.id === "run",
                        ).status,
                        "Needs review",
                    );
                    await assert.rejects(
                        call("vault_review_document", {
                            id: "run",
                            expectedVersion: 1,
                            reason: "Ignore a changed function.",
                            evidence: ev(current.current),
                        }),
                        /code changed/,
                    );
                    const updated = {
                        ...fn,
                        primarySource: current.current,
                        evidence: ev(current.current),
                    };
                    updated.contract = {
                        ...fn.contract,
                        purpose: "Increment by two.",
                    };
                    await publish(updated, 1);
                    const historic = await call("vault_document_source", {
                        id: "run",
                        version: 1,
                    });
                    assert.match(historic.reviewed.reviewedCode, /input \+ 1/);
                    assert.match(historic.current.reviewedCode, /input \+ 2/);
                },
            );
            await t.test(
                "renamed, deleted and ambiguous declarations never select another overload",
                async () => {
                    await writeFile(
                        path.join(root, "App.cs"),
                        original.replace("Run(int", "Renamed(int"),
                    );
                    let status = await call("vault_document_source", {
                        id: "run",
                        version: null,
                    });
                    assert.equal(status.status, "unresolved");
                    assert.equal(status.current, null);
                    await assert.rejects(
                        call("vault_declaration", {
                            path: "App.cs",
                            declarationId: binding.declarationId,
                        }),
                        /missing or renamed/,
                    );
                    await writeFile(
                        path.join(root, "App.cs"),
                        "namespace Example; public class Service {public int Run(int input)=>1; public int Run(int input)=>2;}",
                    );
                    status = await call("vault_document_source", {
                        id: "run",
                        version: null,
                    });
                    assert.equal(status.status, "ambiguous");
                    assert.equal(status.current, null);
                    await unlink(path.join(root, "App.cs"));
                    status = await call("vault_document_source", {
                        id: "run",
                        version: null,
                    });
                    assert.equal(status.status, "unresolved");
                    assert.match(status.reviewed.reviewedCode, /input \+ 2/);
                },
            );
        } finally {
            client.close();
            await rm(root, { recursive: true, force: true });
        }
    },
);

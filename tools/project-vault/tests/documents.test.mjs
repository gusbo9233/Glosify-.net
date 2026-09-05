import test from "node:test";
import assert from "node:assert/strict";
import { spawn, execFile } from "node:child_process";
import { promisify } from "node:util";
import { mkdtemp, writeFile, readFile, mkdir, rm } from "node:fs/promises";
import { createInterface } from "node:readline";
import path from "node:path";
import os from "node:os";
import { fileURLToPath } from "node:url";
const exec = promisify(execFile),
    home = path.resolve(path.dirname(fileURLToPath(import.meta.url)), ".."),
    dll = path.join(home, "server/bin/Debug/net10.0/ProjectVault.dll");
import { mcp } from "./mcp.mjs";
for (const name of ["Orders", "Reading"])
    test(
        `authored documents in independent ${name} repository without static indexing`,
        { timeout: 90000 },
        async (t) => {
            const root = await mkdtemp(
                path.join(os.tmpdir(), "authored-vault-"),
            );
            let client;
            try {
                await exec("git", ["init", "-b", "main", root]);
                await writeFile(
                    path.join(root, "Program.cs"),
                    `// ${name} workflow\npublic enum State { Ready, Done }\n`,
                );
                await writeFile(
                    path.join(root, "Demo.csproj"),
                    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>',
                );
                client = mcp(root);
                const call = client.call;
                const schema = (
                    await client.rpc("tools/list")
                ).result.tools.find(
                    (t) => t.name === "vault_save_document",
                ).inputSchema;
                assert.equal(
                    schema.properties.document.properties.diagrams.items
                        .properties.transitions.items.properties.trigger.type,
                    "string",
                );
                let source = await call("vault_source", {
                    path: "Program.cs",
                    line: 1,
                    count: 10,
                });
                const doc = {
                    id: "understand-workflow",
                    title: `How ${name} finishes`,
                    summary: "A useful conceptual lifecycle.",
                    category: "Workflows",
                    markdown:
                        "The conceptual states describe user-visible progress, not a one-to-one code inventory.",
                    links: [],
                    unknowns: [],
                    dependencies: ["Demo.csproj"],
                    evidence: [
                        {
                            id: "source",
                            path: "Program.cs",
                            line: 1,
                            endLine: 2,
                            hash: source.hash,
                        },
                    ],
                    diagrams: [
                        {
                            id: "lifecycle",
                            title: "Lifecycle",
                            kind: "state-machine",
                            description: "Conceptual lifecycle",
                            nodes: [
                                {
                                    id: "ready",
                                    label: "Ready",
                                    description: "Waiting for input.",
                                    kind: "conceptual",
                                    evidence: ["source"],
                                    links: [],
                                    markers: [],
                                    x: 0,
                                    y: 0,
                                },
                                {
                                    id: "done",
                                    label: "Done",
                                    description: "The user sees the outcome.",
                                    kind: "conceptual",
                                    evidence: ["source"],
                                    links: [],
                                    markers: [],
                                    x: 300,
                                    y: 0,
                                },
                            ],
                            transitions: [
                                {
                                    id: "finish",
                                    source: "ready",
                                    target: "done",
                                    label: "Finish",
                                    trigger: "User submits",
                                    condition: "Input accepted",
                                    effect: "Complete the operation",
                                    description:
                                        "A deliberate conceptual transition.",
                                    evidence: ["source"],
                                },
                            ],
                        },
                    ],
                };
                await t.test(
                    "drafts, publication and no index requirement",
                    async () => {
                        let d = await call("vault_save_document", {
                            document: doc,
                            expectedVersion: 0,
                            publish: false,
                        });
                        assert.equal(d.published, null);
                        assert.equal(d.version, 1);
                        d = await call("vault_save_document", {
                            document: doc,
                            expectedVersion: 1,
                            publish: true,
                        });
                        assert.equal(
                            d.published.document.diagrams[0].nodes[0].label,
                            "Ready",
                        );
                        assert.equal(d.version, 2);
                        await assert.rejects(
                            readFile(
                                path.join(
                                    root,
                                    ".project-visualization/current.json",
                                ),
                            ),
                            /ENOENT/,
                        );
                        assert.equal(
                            (await call("vault_document_status")).fresh,
                            true,
                        );
                    },
                );
                await t.test(
                    "browser endpoints load a document, notes and presentation and save a targeted request",
                    async () => {
                        const server = spawn(
                            "dotnet",
                            [
                                dll,
                                "serve",
                                "--repo",
                                root,
                                "--tool-root",
                                home,
                                "--port",
                                "5196",
                            ],
                            { stdio: "ignore" },
                        );
                        try {
                            const url = "http://127.0.0.1:5196/api/";
                            let ready = false;
                            for (let i = 0; i < 80; i++) {
                                try {
                                    if ((await fetch(url + "documents")).ok) {
                                        ready = true;
                                        break;
                                    }
                                } catch {}
                                await new Promise((r) => setTimeout(r, 100));
                            }
                            assert.equal(ready, true);
                            for (const suffix of [
                                "",
                                "/notes",
                                "/presentation",
                                "/backlinks",
                            ]) {
                                const response = await fetch(
                                    url + "documents/" + doc.id + suffix,
                                );
                                assert.equal(response.status, 200, suffix);
                                await response.json();
                            }
                            const request = {
                                id: "browser-question",
                                question: "Explain this transition",
                                documentId: doc.id,
                                targetId: "finish",
                                status: "open",
                                resultDocumentIds: [],
                                response: "",
                                version: 0,
                            };
                            const response = await fetch(
                                url + "document-requests",
                                {
                                    method: "POST",
                                    headers: {
                                        "Content-Type": "application/json",
                                        "X-Project-Vault": "local",
                                    },
                                    body: JSON.stringify({
                                        request,
                                        expectedVersion: 0,
                                    }),
                                },
                            );
                            assert.equal(response.status, 200);
                            assert.equal(
                                (await response.json()).targetId,
                                "finish",
                            );
                            const noHeader = await fetch(
                                url + "document-requests",
                                {
                                    method: "POST",
                                    headers: {
                                        "Content-Type": "application/json",
                                    },
                                    body: JSON.stringify({
                                        request,
                                        expectedVersion: 1,
                                    }),
                                },
                            );
                            assert.equal(noHeader.status, 403);
                        } finally {
                            server.kill();
                            await new Promise((resolve) =>
                                server.once("exit", resolve),
                            );
                        }
                    },
                );
                await t.test(
                    "conflicts and invalid evidence/links preserve the published revision",
                    async () => {
                        await assert.rejects(
                            call("vault_save_document", {
                                document: { ...doc, title: "Overwrite" },
                                expectedVersion: 1,
                                publish: true,
                            }),
                            /conflict/,
                        );
                        await assert.rejects(
                            call("vault_save_document", {
                                document: {
                                    ...doc,
                                    evidence: [
                                        { ...doc.evidence[0], hash: "wrong" },
                                    ],
                                },
                                expectedVersion: 2,
                                publish: true,
                            }),
                            /Evidence changed/,
                        );
                        await assert.rejects(
                            call("vault_save_document", {
                                document: { ...doc, links: ["missing"] },
                                expectedVersion: 2,
                                publish: true,
                            }),
                            /not been published/,
                        );
                        const bad = structuredClone(doc);
                        bad.diagrams[0].transitions[0].target = "missing";
                        await assert.rejects(
                            call("vault_save_document", {
                                document: bad,
                                expectedVersion: 2,
                                publish: true,
                            }),
                            /endpoints/,
                        );
                        const d = await call("vault_document", { id: doc.id });
                        assert.equal(d.version, 2);
                        assert.equal(d.published.document.title, doc.title);
                    },
                );
                await t.test(
                    "requests require published answers and preserve partial questions",
                    async () => {
                        const r = {
                            id: "question",
                            question: "When does this fail?",
                            documentId: doc.id,
                            targetId: "ready",
                            status: "open",
                            resultDocumentIds: [],
                            response: "",
                            version: 0,
                        };
                        await call("vault_save_request", {
                            request: r,
                            expectedVersion: 0,
                        });
                        await assert.rejects(
                            call("vault_save_request", {
                                request: { ...r, status: "answered" },
                                expectedVersion: 1,
                            }),
                            /published results/,
                        );
                        await call("vault_save_request", {
                            request: {
                                ...r,
                                status: "partial",
                                resultDocumentIds: [doc.id],
                                response:
                                    "Happy path documented; cancellation remains open.",
                            },
                            expectedVersion: 1,
                        });
                        assert.equal(
                            (await call("vault_document_requests")).find(
                                (r) => r.id === "question",
                            ).status,
                            "partial",
                        );
                        await call("vault_save_request", {
                            request: {
                                ...r,
                                status: "answered",
                                resultDocumentIds: [doc.id],
                                response:
                                    "The document explains both outcomes.",
                            },
                            expectedVersion: 2,
                        });
                        assert.deepEqual(
                            (await call("vault_document_requests")).find(
                                (r) => r.id === "question",
                            ).resultDocumentIds,
                            [doc.id],
                        );
                    },
                );
                await t.test(
                    "manual source changes require review; index data cannot clear it",
                    async () => {
                        await writeFile(
                            path.join(root, "Unrelated.cs"),
                            "// unrelated\n",
                        );
                        assert.equal(
                            (await call("vault_document_status")).fresh,
                            true,
                        );
                        await writeFile(
                            path.join(root, "Program.cs"),
                            `// ${name} workflow reviewed\npublic enum State { Ready, Done }\n`,
                        );
                        assert.equal(
                            (await call("vault_document_status")).fresh,
                            false,
                        );
                        assert.deepEqual(
                            (await call("vault_document_impacts"))[0]
                                .changedFiles,
                            ["Program.cs"],
                        );
                        await writeFile(
                            path.join(
                                root,
                                ".project-visualization/current.json",
                            ),
                            JSON.stringify({ id: "pretend-current" }),
                        );
                        assert.equal(
                            (await call("vault_document_status")).fresh,
                            false,
                        );
                        source = await call("vault_source", {
                            path: "Program.cs",
                            line: 1,
                            count: 10,
                        });
                        doc.evidence[0].hash = source.hash;
                        const reviewed = await call("vault_review_document", {
                            id: doc.id,
                            expectedVersion: 2,
                            reason: "Only a comment changed; the Ready-to-Done behavior and explanation remain accurate.",
                            evidence: doc.evidence,
                        });
                        assert.equal(reviewed.version, 3);
                        assert.equal(reviewed.history.length, 1);
                        assert.equal(
                            (await call("vault_document_status")).fresh,
                            true,
                        );
                    },
                );
                await t.test(
                    "removed item annotations remain unresolved and layouts survive revisions",
                    async () => {
                        await call("vault_save_document_note", {
                            id: "note",
                            documentId: doc.id,
                            targetId: "ready",
                            markdown: "Why is this safe?",
                        });
                        await mkdir(
                            path.join(
                                root,
                                ".project-visualization/document-layouts",
                            ),
                            { recursive: true },
                        );
                        const layout = {
                            positions: { ready: { x: 50, y: 60 } },
                            bookmarks: [],
                        };
                        await writeFile(
                            path.join(
                                root,
                                ".project-visualization/document-layouts",
                                doc.id + ".json",
                            ),
                            JSON.stringify(layout),
                        );
                        const revised = structuredClone(doc);
                        revised.diagrams[0].nodes.shift();
                        revised.diagrams[0].transitions = [];
                        await call("vault_save_document", {
                            document: revised,
                            expectedVersion: 3,
                            publish: true,
                        });
                        const n = (
                            await call("vault_document_notes", { id: doc.id })
                        )[0];
                        assert.equal(n.unresolved, true);
                        assert.equal(n.note.markdown, "Why is this safe?");
                        assert.deepEqual(
                            JSON.parse(
                                await readFile(
                                    path.join(
                                        root,
                                        ".project-visualization/document-layouts",
                                        doc.id + ".json",
                                    ),
                                ),
                            ),
                            layout,
                        );
                    },
                );
                await t.test(
                    "branch context prompts review and path traversal is rejected",
                    async () => {
                        await exec("git", [
                            "-C",
                            root,
                            "symbolic-ref",
                            "HEAD",
                            "refs/heads/other",
                        ]);
                        const impact = (
                            await call("vault_document_impacts")
                        )[0];
                        assert.equal(impact.contextChanged, true);
                        assert.equal(impact.status, "Needs review");
                        await assert.rejects(
                            call("vault_source", {
                                path: "../outside.cs",
                                line: 1,
                                count: 1,
                            }),
                            /relative/,
                        );
                    },
                );
            } finally {
                client?.close();
                await rm(root, { recursive: true, force: true });
            }
        },
    );

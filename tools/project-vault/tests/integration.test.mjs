import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, writeFile, readFile, mkdir, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { spawn, execFile } from "node:child_process";
import { promisify } from "node:util";
const exec = promisify(execFile),
    home = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const dll = path.join(home, "server/bin/Debug/net10.0/ProjectVault.dll");
const source = `using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
var builder=WebApplication.CreateBuilder(args);builder.Services.AddControllers();var app=builder.Build();app.MapControllers();app.MapGet("/health",()=>"ok");
[Route("api/orders")]
public class OrdersController:ControllerBase {
 [HttpPost("pay")][Authorize]
 public async Task<int> Pay(OrderRequest request,CancellationToken ct){if(request.Amount<=0)return -1;await Task.Delay(1,ct);return Calculate(request.Amount);}
 private int Calculate(int amount)=>amount*2;
 private string Calculate(string amount)=>amount;
}
public record OrderRequest(int Amount);
`;
async function cli(root, action, ...extra) {
    return exec(
        "dotnet",
        [dll, action, "--repo", root, "--tool-root", home, ...extra],
        { timeout: 90000, maxBuffer: 1024 * 1024 },
    );
}
async function waitServer(url, child) {
    for (let i = 0; i < 100; i++) {
        try {
            const r = await fetch(url + "/api/status");
            if (r.ok) return;
        } catch {}
        if (child.exitCode !== null) throw new Error("Test server exited");
        await new Promise((r) => setTimeout(r, 100));
    }
    throw new Error("Test server did not start");
}
test(
    "second repository: semantic extraction, vault mutations, freshness, and MCP",
    { timeout: 180000 },
    async (t) => {
        const root = await mkdtemp(
            path.join(os.tmpdir(), "project-vault-test-"),
        );
        let server;
        try {
            await writeFile(
                path.join(root, "Demo.csproj"),
                '<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>',
            );
            await writeFile(path.join(root, "Program.cs"), source);
        await writeFile(path.join(root,"LockService.cs"),"public class LockService { public void Acquire() {} }");
            await writeFile(
                path.join(root, "client.ts"),
                'async function send(){return fetch("/api/orders/pay",{method:"POST"})} document.addEventListener("click",send);',
            );
            await writeFile(path.join(root, ".gitignore"), "bin/\nobj/\n");
            await exec("git", ["init", "-b", "main", root]);
            await exec(
                "dotnet",
                [
                    "restore",
                    path.join(root, "Demo.csproj"),
                    "--ignore-failed-sources",
                ],
                { timeout: 60000 },
            );
            await cli(root, "refresh");
            let snap = JSON.parse(
                await readFile(
                    path.join(root, ".project-visualization/current.json"),
                    "utf8",
                ),
            );
            const pay = snap.elements.find((e) => e.name === "Pay"),
                overloads = snap.elements.filter((e) => e.name === "Calculate");
            await t.test(
                "resolves overloads, route, parameter types, UI-to-server links and control flow",
                () => {
                    assert.equal(
                        snap.diagnostics.some(
                            (d) =>
                                d.includes("compilation errors") ||
                                d.includes("loading failed"),
                        ),
                        false,
                        JSON.stringify(snap.diagnostics),
                    );
                    assert.ok(snap.elements.some(e=>e.name==="LockService"));
            assert.equal(overloads.length, 2);
                    assert.notEqual(overloads[0].id, overloads[1].id);
                    assert.equal(pay.route, "/api/orders/pay");
                    assert.equal(pay.inputs[0].type, "OrderRequest");
                    const target = overloads.find((e) => e.output === "int");
                    assert.ok(
                        snap.relations.some(
                            (r) =>
                                r.source === pay.id &&
                                r.target === target.id &&
                                r.kind === "calls",
                        ),
                    );
                    const flow = snap.workflows.find(
                        (w) => w.entryId === pay.id,
                    );
                    assert.ok(flow.steps.some((s) => s.kind === "decision"));
                    assert.ok(flow.edges.some((e) => e.label === "false"));
                    const ui = snap.elements.find((e) => e.kind === "ui");
                    assert.ok(
                        snap.workflows
                            .find((w) => w.entryId === ui.id)
                            .members.includes(pay.id),
                    );
                },
            );
            const port = 5199;
            server = spawn(
                "dotnet",
                [
                    dll,
                    "serve",
                    "--repo",
                    root,
                    "--tool-root",
                    home,
                    "--port",
                    String(port),
                ],
                { stdio: ["ignore", "pipe", "pipe"] },
            );
            let logs = "";
            server.stderr.on("data", (d) => (logs += d));
            server.stdout.on("data", () => {});
            const url = `http://127.0.0.1:${port}`;
            await waitServer(url, server);
            const request = async (endpoint, method = "GET", body) => {
                const r = await fetch(url + "/api/" + endpoint, {
                    method,
                    headers: {
                        "Content-Type": "application/json",
                        "X-Project-Vault": "local",
                    },
                    body: body === undefined ? undefined : JSON.stringify(body),
                });
                const text = await r.text();
                return {
                    status: r.status,
                    data: text ? JSON.parse(text) : null,
                };
            };
            await t.test(
                "rejects cross-origin writes and path traversal identifiers",
                async () => {
                    const bad = await fetch(url + "/api/notes/" + pay.id, {
                        method: "PUT",
                        headers: {
                            "Content-Type": "application/json",
                            "X-Project-Vault": "local",
                            Origin: "https://untrusted.example",
                        },
                        body: JSON.stringify({
                            elementId: pay.id,
                            markdown: "bad",
                            snapshotId: snap.id,
                        }),
                    });
                    assert.equal(bad.status, 403);
                    assert.equal(
                        (await request("elements/not-an-id")).status,
                        400,
                    );
                },
            );
            let proposal;
            await t.test(
                "saves notes/layout/proposals independently and rejects unevidenced completion",
                async () => {
                    assert.equal(
                        (
                            await request("notes/" + pay.id, "PUT", {
                                elementId: pay.id,
                                markdown: "Preserve this note.",
                                snapshotId: snap.id,
                            })
                        ).status,
                        204,
                    );
                    await request("layout", "PUT", {
                        positions: { [pay.id]: { x: 12, y: 34 } },
                        bookmarks: [pay.id],
                    });
                    proposal = (
                        await request("proposals", "POST", {
                            id: "",
                            title: "Adjust calculation",
                            baseSnapshot: snap.id,
                            affectedIds: [pay.id],
                            narrative: "Keep the validation.",
                            edits: [],
                            criteria: [
                                {
                                    text: "Double is changed to triple",
                                    verified: false,
                                    evidence: "",
                                },
                            ],
                            status: "draft",
                            deviations: "",
                            version: 0,
                        })
                    ).data;
                    assert.equal(proposal.status, "draft");
                    const rejected = await request("proposals", "POST", {
                        ...proposal,
                        status: "implemented",
                        resultSnapshot: snap.id,
                    });
                    assert.equal(rejected.status, 400);
                    assert.equal(
                        (
                            await request("interpretations/" + pay.id, "PUT", {
                                elementId: pay.id,
                                markdown: "Reasoned explanation",
                                snapshotId: snap.id,
                                evidence: pay.evidence,
                            })
                        ).status,
                        204,
                    );
                },
            );
            await t.test(
                "failed refresh retains the last snapshot and recovers after the lock is released",
                async () => {
                    const locker = spawn(
                        "python3",
                        [
                            "-u",
                            "-c",
                            'import fcntl,sys;f=open(sys.argv[1],"a");fcntl.flock(f,fcntl.LOCK_EX);print("locked",flush=True);sys.stdin.read()',
                            path.join(
                                root,
                                ".project-visualization/local/refresh.lock",
                            ),
                        ],
                        { stdio: ["pipe", "pipe", "pipe"] },
                    );
                    await new Promise((resolve, reject) => {
                        locker.stdout.once("data", resolve);
                        locker.once("error", reject);
                    });
                    try {
                        assert.equal(
                            (await request("refresh", "POST", {})).status,
                            500,
                        );
                        assert.equal(
                            (await request("status")).data.status,
                            "blocked",
                        );
                        assert.equal(
                            (await request("snapshot")).data.id,
                            snap.id,
                        );
                    } finally {
                        locker.stdin.end();
                        await new Promise((r) => locker.once("exit", r));
                    }
                    assert.equal(
                        (await request("refresh", "POST", {})).status,
                        200,
                    );
                    assert.equal((await request("status")).data.fresh, true);
                },
            );
            await t.test(
                "explanations reject nonexistent source lines",
                async () => {
                    const invalid = {
                        elementId: pay.id,
                        markdown: "Unsupported claim",
                        snapshotId: snap.id,
                        evidence: [{ ...pay.evidence[0], line: 999999 }],
                    };
                    assert.equal(
                        (
                            await request(
                                "interpretations/" + pay.id,
                                "PUT",
                                invalid,
                            )
                        ).status,
                        400,
                    );
                },
            );
            await t.test(
                "manual edits invalidate freshness and refresh preserves identities, notes, and layout",
                async () => {
                    await writeFile(
                        path.join(root, "Program.cs"),
                        source.replace("amount*2", "amount*3"),
                    );
                    assert.equal((await request("status")).data.fresh, false);
                    assert.equal(
                        (await request("refresh", "POST", {})).status,
                        200,
                    );
                    const newer = (await request("snapshot")).data;
                    assert.notEqual(newer.id, snap.id);
                    assert.equal(
                        newer.elements.find((e) => e.name === "Pay").id,
                        pay.id,
                    );
                    const detail = (await request("elements/" + pay.id)).data;
                    assert.equal(
                        detail.annotation.markdown,
                        "Preserve this note.",
                    );
                    assert.equal(detail.interpretation.stale, true);
                    assert.deepEqual(
                        (await request("layout")).data.positions[pay.id],
                        { x: 12, y: 34 },
                    );
                    const diff = (await request("compare/" + snap.id)).data;
                    assert.ok(diff.changed.some((e) => e.id === pay.id));
                    assert.equal(
                        (await request("proposals")).data[0].baseSnapshot,
                        snap.id,
                    );
                    snap = newer;
                },
            );
            await t.test(
                "renaming leaves an unresolved page with its note intact",
                async () => {
                    await writeFile(
                        path.join(root, "Program.cs"),
                        source.replace(" Pay(", " PayRenamed("),
                    );
                    await request("refresh", "POST", {});
                    const detail = (await request("elements/" + pay.id)).data;
                    assert.equal(detail.unresolved, true);
                    assert.equal(
                        detail.annotation.markdown,
                        "Preserve this note.",
                    );
                },
            );
            await t.test("branch changes invalidate the snapshot", async () => {
                await exec("git", [
                    "-C",
                    root,
                    "symbolic-ref",
                    "HEAD",
                    "refs/heads/other",
                ]);
                assert.equal((await request("status")).data.fresh, false);
            });
            await t.test(
                "MCP initializes and exposes real tools without stdout noise",
                async () => {
                    const child = spawn(
                        "dotnet",
                        [dll, "mcp", "--repo", root, "--tool-root", home],
                        { stdio: ["pipe", "pipe", "pipe"] },
                    );
                    let out = "";
                    child.stdout.on("data", (d) => (out += d));
                    child.stderr.on("data", () => {});
                    child.stdin.end(
                        JSON.stringify({
                            jsonrpc: "2.0",
                            id: 1,
                            method: "initialize",
                            params: {
                                protocolVersion: "2024-11-05",
                                capabilities: {},
                                clientInfo: { name: "test", version: "1" },
                            },
                        }) +
                            "\n" +
                            JSON.stringify({
                                jsonrpc: "2.0",
                                id: 2,
                                method: "tools/list",
                            }) +
                            "\n",
                    );
                    await new Promise((resolve, reject) => {
                        child.on("exit", (code) =>
                            code === 0
                                ? resolve()
                                : reject(new Error("MCP exited " + code)),
                        );
                    });
                    const messages = out.trim().split("\n").map(JSON.parse);
                    assert.equal(messages.length, 2);
                    assert.equal(
                        messages[0].result.serverInfo.name,
                        "project-vault",
                    );
                    assert.ok(
                        messages[1].result.tools.some(
                            (t) => t.name === "vault_refresh",
                        ),
                    );
                },
            );
        } finally {
            if (server) {
                server.kill("SIGTERM");
                await new Promise((r) => server.once("exit", r));
            }
            await rm(root, { recursive: true, force: true });
        }
    },
);

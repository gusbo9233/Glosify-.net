import { spawn } from "node:child_process";
import { createInterface } from "node:readline";
import path from "node:path";
import { fileURLToPath } from "node:url";
const home = path.resolve(path.dirname(fileURLToPath(import.meta.url)), ".."),
    dll = path.join(home, "server/bin/Debug/net10.0/ProjectVault.dll");
export function mcp(root) {
    const child = spawn(
        "dotnet",
        [dll, "mcp", "--repo", root, "--tool-root", home],
        { stdio: ["pipe", "pipe", "pipe"] },
    );
    let next = 0;
    const pending = new Map();
    let stderr = "";
    child.stderr.on("data", (d) => (stderr += d));
    createInterface({ input: child.stdout }).on("line", (line) => {
        const value = JSON.parse(line);
        const entry = pending.get(value.id);
        if (entry) {
            pending.delete(value.id);
            clearTimeout(entry.timer);
            entry.resolve(value);
        }
    });
    child.on("exit", () => {
        for (const p of pending.values()) {
            clearTimeout(p.timer);
            p.reject(new Error("MCP exited: " + stderr));
        }
        pending.clear();
    });
    const rpc = (method, params = {}) =>
        new Promise((resolve, reject) => {
            const id = ++next;
            const timer = setTimeout(() => {
                pending.delete(id);
                reject(new Error("MCP timeout"));
            }, 30000);
            pending.set(id, { resolve, reject, timer });
            child.stdin.write(
                JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n",
            );
        });
    return {
        rpc,
        close: () => child.stdin.end(),
        call: async (name, args = {}) => {
            const value = await rpc("tools/call", { name, arguments: args });
            if (value.error) throw new Error(value.error.message);
            if (value.result.isError)
                throw new Error(value.result.content[0].text);
            return JSON.parse(value.result.content[0].text);
        },
    };
}

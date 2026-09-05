import ts from "typescript";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { createHash } from "node:crypto";
export const id = (text) =>
    createHash("sha256").update(text).digest("hex").slice(0, 24);
const walk = (node, visit) => {
    visit(node);
    ts.forEachChild(node, (n) => walk(n, visit));
};
const nameOf = (node) =>
    node?.name?.getText() ??
    (node?.parent && ts.isVariableDeclaration(node.parent)
        ? node.parent.name.getText()
        : undefined);
export function analyzeSource(file, source, hash) {
    const elements = [],
        relations = [],
        diagnostics = [];
    const addLink = (a, b, kind, status = "extracted") =>
        relations.push({
            id: id(a + kind + b),
            source: a,
            target: b,
            kind,
            status,
        });
    const evidence = (line) => [{ path: file, line, hash }];
    const functions = new Map();
    const base = id("frontend:" + file);
    elements.push({
        id: base,
        name: path.basename(file),
        kind: "module",
        layer: "architecture",
        group: path.dirname(file),
        summary: "Frontend source module.",
        status: "extracted",
        evidence: evidence(1),
        inputs: [],
        checks: [],
        concepts: [],
        concerns: [],
    });
    function element(key, name, kind, line, extra = {}) {
        const e = {
            id: id(file + ":" + key),
            name,
            kind,
            layer: "functions",
            group: file,
            summary:
                kind === "ui"
                    ? "User interaction binding; dynamic dispatch may need review."
                    : "Frontend declaration.",
            status: "extracted",
            inputs: [],
            checks: [],
            concepts: [],
            concerns: [],
            evidence: evidence(line),
            ...extra,
        };
        elements.push(e);
        addLink(base, e.id, "contains");
        return e;
    }
    const isMarkup = /\.(cshtml|html)$/.test(file);
    const chunks = isMarkup
        ? [...source.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/gi)].map(
              (m) => ({
                  text: m[1],
                  offset: source.slice(0, m.index).split("\n").length - 1,
              }),
          )
        : [{ text: source, offset: 0 }];
    for (const { text, offset } of chunks) {
        const sf = ts.createSourceFile(
            file,
            text,
            ts.ScriptTarget.Latest,
            true,
            /\.tsx$/.test(file)
                ? ts.ScriptKind.TSX
                : /\.jsx$/.test(file)
                  ? ts.ScriptKind.JSX
                  : ts.ScriptKind.TS,
        );
        const line = (n) =>
            sf.getLineAndCharacterOfPosition(n.getStart(sf)).line + 1 + offset;
        const decls = new Map();
        const semanticKey = (n) => {
            const owners = [];
            let p = n.parent;
            while (p) {
                if (ts.isFunctionLike(p) || ts.isClassDeclaration(p)) {
                    const name = nameOf(p);
                    if (name) owners.unshift(name);
                }
                p = p.parent;
            }
            return [...owners, nameOf(n) ?? `anonymous@${line(n)}`].join(".");
        };
        walk(sf, (n) => {
            if (
                ts.isFunctionDeclaration(n) ||
                ts.isMethodDeclaration(n) ||
                ts.isArrowFunction(n) ||
                ts.isFunctionExpression(n)
            ) {
                const name = nameOf(n) ?? `anonymous@${line(n)}`;
                const key = semanticKey(n);
                const e = element("function:" + key, key, "function", line(n), {
                    signature: key,
                    async: !!n.modifiers?.some(
                        (m) => m.kind === ts.SyntaxKind.AsyncKeyword,
                    ),
                    inputs: n.parameters.map((p) => ({
                        name: p.name.getText(sf),
                        type: p.type?.getText(sf) ?? "inferred at runtime",
                    })),
                    output: n.type?.getText(sf) ?? "not declared",
                });
                decls.set(n, e);
                if (!functions.has(name)) functions.set(name, []);
                functions.get(name).push(e);
            }
        });
        const owner = (n) => {
            let p = n.parent;
            while (p) {
                if (decls.has(p)) return decls.get(p);
                p = p.parent;
            }
            return elements.find((e) => e.id === base);
        };
        walk(sf, (n) => {
            if (ts.isCallExpression(n)) {
                const callee = n.expression.getText(sf);
                const parent = owner(n);
                if (
                    ts.isIdentifier(n.expression) &&
                    functions.get(callee)?.length === 1
                )
                    addLink(parent.id, functions.get(callee)[0].id, "calls");
                if (
                    callee === "fetch" ||
                    /^(?:axios|\$|jQuery)\.(get|post|put|delete|patch)$/.test(
                        callee,
                    )
                ) {
                    const arg = n.arguments[0];
                    const route =
                        arg && ts.isStringLiteralLike(arg)
                            ? arg.text.split("?")[0].split("#")[0]
                            : undefined;
                    let verb =
                        callee === "fetch"
                            ? "GET"
                            : callee.split(".").at(-1).toUpperCase();
                    const opts = n.arguments[1];
                    if (opts && ts.isObjectLiteralExpression(opts)) {
                        const prop = opts.properties.find(
                            (p) => p.name?.getText(sf) === "method",
                        );
                        if (
                            prop &&
                            ts.isPropertyAssignment(prop) &&
                            ts.isStringLiteralLike(prop.initializer)
                        )
                            verb = prop.initializer.text.toUpperCase();
                    }
                    const e = element(
                        "request:" + parent.id + ":" + line(n),
                        route ? `${verb} ${route}` : "Dynamic HTTP request",
                        "request",
                        line(n),
                        {
                            route,
                            verb,
                            status: route ? "extracted" : "unresolved",
                            summary: route
                                ? "Literal request target; server routing and origin must be verified."
                                : "Request URL is computed and cannot be bound statically.",
                        },
                    );
                    addLink(parent.id, e.id, "calls");
                }
                if (/(?:addEventListener|\.on|\.addListener)$/.test(callee)) {
                    const event = n.arguments[0];
                    const literal =
                        event && ts.isStringLiteralLike(event)
                            ? event.text
                            : "dynamic event";
                    const e = element(
                        "event:" + line(n),
                        literal +
                            " · " +
                            callee.replace(/\.addEventListener$/, ""),
                        "ui",
                        line(n),
                        { entryPoint: true },
                    );
                    const handler = n.arguments[1];
                    let targets = [];
                    if (handler && ts.isIdentifier(handler))
                        targets = functions.get(handler.text) ?? [];
                    else if (handler && decls.has(handler))
                        targets = [decls.get(handler)];
                    if (targets.length)
                        for (const target of targets)
                            addLink(
                                e.id,
                                target.id,
                                "handles",
                                targets.length === 1
                                    ? "extracted"
                                    : "interpreted",
                            );
                    else if (handler) {
                        const hid = element(
                            "handler:" + line(handler),
                            "Inline " + literal + " handler",
                            "function",
                            line(handler),
                        );
                        walk(handler, (child) => {
                            if (ts.isCallExpression(child)) {
                                const targetName = child.expression.getText(sf);
                                for (const target of functions.get(
                                    targetName,
                                ) ?? [])
                                    addLink(hid.id, target.id, "calls");
                            }
                        });
                        addLink(e.id, hid.id, "handles");
                    }
                }
            }
            if (ts.isJsxAttribute(n) && /^on[A-Z]/.test(n.name.getText(sf))) {
                const e = element(
                    "jsx:" + line(n) + ":" + n.name.getText(sf),
                    n.name.getText(sf) + " · React interaction",
                    "ui",
                    line(n),
                    { entryPoint: true },
                );
                const exp =
                    n.initializer && ts.isJsxExpression(n.initializer)
                        ? n.initializer.expression
                        : undefined;
                if (exp && ts.isIdentifier(exp))
                    for (const target of functions.get(exp.text) ?? [])
                        addLink(e.id, target.id, "handles");
                else if (exp)
                    walk(exp, (call) => {
                        if (ts.isCallExpression(call))
                            for (const target of functions.get(
                                call.expression.getText(sf),
                            ) ?? [])
                                addLink(e.id, target.id, "handles");
                    });
            }
            if (
                ts.isBinaryExpression(n) &&
                n.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
                /\.on[a-z]+$/.test(n.left.getText(sf))
            ) {
                const e = element(
                    "eventproperty:" + line(n),
                    n.left.getText(sf),
                    "ui",
                    line(n),
                    { entryPoint: true },
                );
                if (ts.isIdentifier(n.right))
                    for (const target of functions.get(n.right.text) ?? [])
                        addLink(e.id, target.id, "handles");
            }
        });
        if (sf.parseDiagnostics.length)
            diagnostics.push(
                `${file}: ${sf.parseDiagnostics.length} frontend parse diagnostics; coverage is partial.`,
            );
    }
    if (isMarkup) {
        for (const m of source.matchAll(/<(button|form|a|input)\b([^>]*)>/gi)) {
            const attrs = m[2];
            if (
                m[1].toLowerCase() === "input" &&
                !/type\s*=\s*["'](?:submit|button)["']/i.test(attrs)
            )
                continue;
            const attribute = (k) =>
                attrs.match(
                    new RegExp(`\\b${k}\\s*=\\s*["']([^"']+)["']`, "i"),
                )?.[1];
            const line = source.slice(0, m.index).split("\n").length;
            const controller =
                attribute("asp-controller") ??
                file.match(/Views\/([^/]+)\//)?.[1];
            const action = attribute("asp-action");
            const route =
                action && controller
                    ? `/${controller}/${action}`
                    : (attribute("action") ?? attribute("href"));
            const label =
                attribute("id") ??
                attribute("aria-label") ??
                action ??
                source
                    .slice(m.index + m[0].length)
                    .match(/^\s*([^<\n]{1,60})/)?.[1]
                    ?.trim() ??
                m[1];
            element("markup:" + line + ":" + label, label, "ui", line, {
                entryPoint: true,
                route: route?.startsWith("/") ? route : undefined,
                verb:
                    m[1].toLowerCase() === "form"
                        ? (attribute("method") ?? "GET").toUpperCase()
                        : undefined,
            });
        }
    }
    return { elements, relations, diagnostics };
}
if (process.argv[1] === new URL(import.meta.url).pathname) {
    let input = "";
    for await (const chunk of process.stdin) input += chunk;
    const { root, files } = JSON.parse(input);
    const result = {
        elements: [],
        relations: [],
        workflows: [],
        diagnostics: [],
    };
    for (const [file, hash] of Object.entries(files)) {
        if (
            !/\.(js|jsx|ts|tsx|cshtml|html)$/.test(file) ||
            /\.test\.|\.spec\.|Tests\/|\.d\.ts$|\.min\.js$|wwwroot\/lib\//i.test(
                file,
            )
        )
            continue;
        const data = analyzeSource(
            file,
            await readFile(path.join(root, file), "utf8"),
            hash,
        );
        for (const key of ["elements", "relations", "diagnostics"])
            result[key].push(...data[key]);
    }
    result.diagnostics.push(
        "Frontend coverage: local declarations, literal HTTP targets, Razor actions, React event props, and event listeners. Cross-module binding, DOM delegation, computed URLs, and generated handlers require review.",
    );
    process.stdout.write(JSON.stringify(result));
}

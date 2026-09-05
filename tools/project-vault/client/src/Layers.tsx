import { useEffect, useState } from "react";
import { ArrowRight, Braces, Database, FileCode2 } from "lucide-react";
import Markdown from "react-markdown";
export type DetailLink = {
    targetId: string;
    relation: "workflow" | "expands" | "calls" | "uses-model" | "related";
    label: string;
};
export type Binding = {
    path: string;
    declarationId: string;
    hash: string;
    line: number;
    endLine: number;
    reviewedCode: string;
};
export type Value = {
    name: string;
    type: string;
    description: string;
    modelId: string | null;
};
export type Contract = {
    purpose: string;
    signature: string;
    inputs: Value[];
    output: Value;
    checks: string[];
    async: boolean;
    cancellation: string;
    sideEffects: string[];
    concepts: string[];
    concerns: { category: string; reason: string; certainty: string }[];
};
export type ModelField = {
    name: string;
    type: string;
    description: string;
    validation: string[];
    modelId: string | null;
};
export const levelLabel = (kind?: string) =>
    ({
        "workflow-overview": "1 · Workflows",
        workflow: "2 · Workflow detail",
        action: "3 · Action detail",
        function: "4 · Function detail",
        model: "Model",
        explanation: "Explanation",
    })[kind ?? "explanation"] ?? "Explanation";
const actionLabel = (relation: string) =>
    ({
        workflow: "Open workflow",
        expands: "Open action detail",
        calls: "Open function",
        "uses-model": "Open model",
        related: "Read explanation",
    })[relation] ?? "Open detail";
export function DetailLinks({
    links,
    onOpen,
}: {
    links: DetailLink[];
    onOpen: (id: string) => void;
}) {
    return (
        <div className="k-detail-links">
            {links.map((l, i) => (
                <button
                    key={l.relation + l.targetId + i}
                    onClick={() => onOpen(l.targetId)}
                >
                    <span>
                        <small>{actionLabel(l.relation)}</small>
                        <strong>{l.label}</strong>
                    </span>
                    <ArrowRight size={15} />
                </button>
            ))}
        </div>
    );
}
function Code({ source }: { source: Binding }) {
    const pattern =
        /(\/\/.*|\/\*.*?\*\/|@?"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*'|\b(?:public|private|internal|protected|class|record|struct|enum|interface|async|await|using|namespace|return|if|else|throw|new|null|true|false|var|static|readonly|sealed|override|virtual|partial|get|set|init|void|bool|string|int|out|ref|in|is|not|or|and)\b|\b\d+\b)/g;
    return (
        <pre className="k-code" aria-label="Declaration source">
            <code>
                {source.reviewedCode.split("\n").map((line, index) => (
                    <span className="k-code-line" key={index}>
                        <span className="k-line-number">
                            {source.line + index}
                        </span>
                        <span>
                            {line.split(pattern).map((part, i) => {
                                const kind = /^(\/\/|\/\*)/.test(part)
                                    ? "comment"
                                    : /^[@"']/.test(part)
                                      ? "string"
                                      : /^\d+$/.test(part)
                                        ? "number"
                                        : pattern.test(part)
                                          ? "keyword"
                                          : "";
                                pattern.lastIndex = 0;
                                return (
                                    <span key={i} className={"k-token-" + kind}>
                                        {part}
                                    </span>
                                );
                            })}
                        </span>
                    </span>
                ))}
            </code>
        </pre>
    );
}
export function DeclarationPage({
    id,
    version,
    doc,
    onOpen,
}: {
    id: string;
    version?: number;
    doc: {
        kind?: string;
        markdown: string;
        contract?: Contract | null;
        primarySource?: Binding | null;
        fields?: ModelField[];
        unknowns: string[];
    };
    onOpen: (id: string) => void;
}) {
    const [source, setSource] = useState<{
            status: string;
            reviewed: Binding;
            current: Binding | null;
            message: string;
        } | null>(null),
        [error, setError] = useState(""),
        [which, setWhich] = useState<"reviewed" | "current">("reviewed");
    useEffect(() => {
        let active = true;
        setSource(null);
        setWhich("reviewed");
        const load = () =>
            fetch(
                "/api/documents/" +
                    id +
                    "/source" +
                    (version ? "?version=" + version : ""),
            )
                .then(async (r) => {
                    if (!r.ok)
                        throw new Error(
                            (await r.json()).detail ??
                                "Could not resolve declaration",
                        );
                    return r.json();
                })
                .then((value) => {
                    if (active) {
                        setSource(value);
                        setError("");
                    }
                })
                .catch((e) => {
                    if (active) setError(String(e));
                });
        load();
        const timer = setInterval(load, 5000);
        return () => {
            active = false;
            clearInterval(timer);
        };
    }, [id, version]);
    const contract = doc.contract;
    const shown =
        which === "current"
            ? source?.current
            : (source?.reviewed ?? doc.primarySource);
    const value = (v: Value) => (
        <div className="k-contract-value" key={v.name}>
            <code>
                {v.name}: {v.type}
            </code>
            <p>{v.description}</p>
            {v.modelId && (
                <button onClick={() => onOpen(v.modelId!)}>
                    <Database size={13} /> Open model
                </button>
            )}
        </div>
    );
    return (
        <div className="k-declaration-page">
            <article className="k-contract">
                <span className="k-pill">{levelLabel(doc.kind)}</span>
                {contract && (
                    <>
                        <h2>Contract</h2>
                        <p>{contract.purpose}</p>
                        <code className="k-signature">
                            {contract.signature}
                        </code>
                        <h3>Inputs</h3>
                        {contract.inputs.map(value)}
                        <h3>Returns</h3>
                        {value(contract.output)}
                        <h3>Checks</h3>
                        <ul>
                            {contract.checks.map((x) => (
                                <li key={x}>{x}</li>
                            ))}
                        </ul>
                        <h3>Execution</h3>
                        <p>
                            {contract.async ? "Asynchronous" : "Synchronous"} ·{" "}
                            {contract.cancellation}
                        </p>
                        <h3>Side effects</h3>
                        <ul>
                            {contract.sideEffects.map((x) => (
                                <li key={x}>{x}</li>
                            ))}
                        </ul>
                        <h3>Concepts</h3>
                        <p>{contract.concepts.join(" · ")}</p>
                        {contract.concerns.map((c, i) => (
                            <div className="k-concern" key={i}>
                                <strong>
                                    {c.category} · {c.certainty}
                                </strong>
                                <p>{c.reason}</p>
                            </div>
                        ))}
                    </>
                )}
                {doc.kind === "model" && (
                    <>
                        <h2>Fields and relationships</h2>
                        {doc.fields?.map((f) => (
                            <div className="k-contract-value" key={f.name}>
                                <code>
                                    {f.name}: {f.type}
                                </code>
                                <p>{f.description}</p>
                                {f.validation.length > 0 && (
                                    <small>{f.validation.join(" · ")}</small>
                                )}
                                {f.modelId && (
                                    <button onClick={() => onOpen(f.modelId!)}>
                                        Open related model
                                    </button>
                                )}
                            </div>
                        ))}
                    </>
                )}
                <Markdown>{doc.markdown}</Markdown>
                {doc.unknowns.length > 0 && (
                    <>
                        <h3>Limits</h3>
                        {doc.unknowns.map((x) => (
                            <p key={x}>{x}</p>
                        ))}
                    </>
                )}
            </article>
            <section className="k-declaration-code">
                <div className="k-source-heading">
                    <FileCode2 size={16} />
                    <strong>
                        {shown?.path.split("/").pop() ?? "Source declaration"}
                    </strong>
                    {shown && (
                        <a
                            href={
                                "/api/source-file?path=" +
                                encodeURIComponent(shown.path)
                            }
                            target="_blank"
                            rel="noreferrer"
                        >
                            Open current file ↗
                        </a>
                    )}
                </div>
                {error && (
                    <div className="k-review-banner">
                        {error}. The reviewed excerpt remains below.
                    </div>
                )}
                {source && source.status !== "unchanged" && (
                    <div className="k-review-banner">{source.message}</div>
                )}
                <div className="k-source-tabs">
                    <button
                        className={which === "reviewed" ? "active" : ""}
                        onClick={() => setWhich("reviewed")}
                    >
                        Reviewed source
                    </button>
                    {source?.status === "changed" && source.current && (
                        <button
                            className={which === "current" ? "active" : ""}
                            onClick={() => setWhich("current")}
                        >
                            Current source · needs review
                        </button>
                    )}
                    <span>
                        {source?.status === "unchanged"
                            ? "Matches current source"
                            : source?.status}
                    </span>
                </div>
                {shown ? (
                    <>
                        <div className="k-source-identity">
                            <Braces size={12} />
                            {shown.declarationId}
                        </div>
                        <Code source={shown} />
                    </>
                ) : (
                    <p className="k-empty">No current declaration selected.</p>
                )}
            </section>
        </div>
    );
}

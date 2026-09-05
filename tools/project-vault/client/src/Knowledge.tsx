import {
    lazy,
    Suspense,
    useCallback,
    useEffect,
    useMemo,
    useState,
} from "react";
import Markdown from "react-markdown";
import {
    ReactFlow,
    Background,
    Controls,
    MiniMap,
    Handle,
    Position as Port,
    applyNodeChanges,
    MarkerType,
    type Node,
    type NodeProps,
    type NodeChange,
} from "@xyflow/react";
import {
    ArrowLeft,
    ArrowRight,
    BookOpen,
    Bookmark,
    Check,
    ChevronRight,
    FileText,
    GitBranch,
    Library,
    MessageSquare,
    Moon,
    PanelLeftClose,
    PanelRightClose,
    Search,
    Sparkles,
    Sun,
    X,
} from "lucide-react";
import "@xyflow/react/dist/style.css";
import "./knowledge.css";
import {
    DeclarationPage,
    DetailLinks,
    levelLabel,
    type DetailLink,
    type Binding,
    type Contract,
    type ModelField,
} from "./Layers";
const Legacy = lazy(() => import("./App"));
type Evidence = {
    id: string;
    path: string;
    line: number;
    endLine: number;
    hash: string;
};
type Marker = { category: string; reason: string; certainty: string };
type Item = {
    detailLinks?: DetailLink[];
    id: string;
    label: string;
    description: string;
    kind: string;
    evidence: string[];
    links: string[];
    markers: Marker[];
    x: number;
    y: number;
};
type Transition = {
    detailLinks?: DetailLink[];
    inputs?: string[];
    outputs?: string[];
    sideEffects?: string[];
    id: string;
    source: string;
    target: string;
    label: string;
    trigger: string;
    condition: string;
    effect: string;
    description: string;
    evidence: string[];
};
type Diagram = {
    id: string;
    title: string;
    kind: string;
    description: string;
    nodes: Item[];
    transitions: Transition[];
};
type Doc = {
    kind?: string;
    detailLinks?: DetailLink[];
    contract?: Contract | null;
    primarySource?: Binding | null;
    fields?: ModelField[];
    id: string;
    title: string;
    summary: string;
    category: string;
    markdown: string;
    links: string[];
    evidence: Evidence[];
    dependencies: string[];
    unknowns: string[];
    diagrams: Diagram[];
};
type Review = { at: string; reason: string; branch: string };
type Envelope = {
    id: string;
    version: number;
    draft: Doc | null;
    published: { version: number; document: Doc; review: Review } | null;
    history: { version: number; document: Doc; review: Review }[];
};
type Impact = {
    id: string;
    status: string;
    changedFiles: string[];
    contextChanged: boolean;
};
type Listing = {
    kind?: string;
    id: string;
    title: string;
    summary: string;
    category: string;
    published: boolean;
    hasDraft: boolean;
    version: number;
    impact: Impact;
};
type Request = {
    id: string;
    question: string;
    documentId: string | null;
    targetId: string | null;
    status: string;
    resultDocumentIds: string[];
    response: string;
    version: number;
};
type Note = {
    id: string;
    documentId: string;
    targetId: string | null;
    markdown: string;
};
type Presentation = {
    positions: Record<string, { x: number; y: number }>;
    bookmarks: string[];
};
const empty: Presentation = { positions: {}, bookmarks: [] };
async function api<T>(
    path: string,
    method = "GET",
    value?: unknown,
): Promise<T> {
    const response = await fetch("/api/" + path, {
        method,
        headers:
            method === "GET"
                ? {}
                : {
                      "Content-Type": "application/json",
                      "X-Project-Vault": "local",
                  },
        body: value === undefined ? undefined : JSON.stringify(value),
    });
    if (!response.ok) {
        const error = await response.json().catch(() => ({}));
        throw new Error(
            error.detail ?? "The vault could not complete that operation.",
        );
    }
    return response.json();
}
function Text({ text }: { text: string }) {
    return (
        <Markdown>
            {text.replace(
                /\[\[([\w-]+)(?:\|([^\]]+))?\]\]/g,
                (_, id, label) => `[${label ?? id}](#document=${id})`,
            )}
        </Markdown>
    );
}
function Card({ data, selected }: NodeProps) {
    const item = data.item as Item;
    return (
        <div className={"k-state " + (selected ? "chosen" : "")}>
            {[Port.Left, Port.Right, Port.Top, Port.Bottom].map((position) => (
                <Handle
                    key={"in-" + position}
                    id={"in-" + position}
                    type="target"
                    position={position}
                />
            ))}
            <small>{item.kind.replaceAll("-", " ")}</small>
            <strong>{item.label}</strong>
            <p>{item.description.split("\n")[0].slice(0, 135)}</p>
            {item.markers.length > 0 && (
                <span className="k-markers">
                    {item.markers.map((m) => (
                        <span key={m.category}>◇ {m.category}</span>
                    ))}
                </span>
            )}
            {[Port.Left, Port.Right, Port.Top, Port.Bottom].map((position) => (
                <Handle
                    key={"out-" + position}
                    id={"out-" + position}
                    type="source"
                    position={position}
                />
            ))}
        </div>
    );
}
const nodeTypes = { authored: Card };
function DiagramCanvas({
    diagram,
    presentation,
    onPosition,
    onSelect,
    selection,
    documentId,
    onOpen,
}: {
    diagram: Diagram;
    presentation: Presentation;
    onPosition: (id: string, x: number, y: number) => void;
    onSelect: (id: string) => void;
    selection: string;
    documentId: string;
    onOpen: (id: string) => void;
}) {
    const initial = useMemo(
        () =>
            diagram.nodes.map((n) => ({
                id: n.id,
                type: "authored",
                position: presentation.positions[n.id] ?? { x: n.x, y: n.y },
                data: { item: n },
            })),
        [diagram, presentation],
    );
    const savedViewport = useMemo(() => {
        try {
            const value = JSON.parse(
                localStorage.getItem(
                    "vault-view-" + documentId + "-" + diagram.id,
                ) ?? "null",
            );
            return value &&
                [value.x, value.y, value.zoom].every(Number.isFinite)
                ? value
                : undefined;
        } catch {
            return undefined;
        }
    }, [documentId, diagram.id]);
    const [nodes, setNodes] = useState<Node[]>(initial);
    useEffect(() => setNodes(initial), [initial]);
    const edges = useMemo(
        () =>
            diagram.transitions.map((e) => {
                const source = initial.find((n) => n.id === e.source)!.position;
                const target = initial.find((n) => n.id === e.target)!.position;
                const dx = target.x - source.x,
                    dy = target.y - source.y;
                let from =
                    Math.abs(dy) < 100
                        ? dx >= 0
                            ? "right"
                            : "left"
                        : dy >= 0
                          ? "bottom"
                          : "top";
                let to =
                    Math.abs(dy) < 100
                        ? dx >= 0
                            ? "left"
                            : "right"
                        : dy >= 0
                          ? "top"
                          : "bottom";
                // Route a returning loop or a long same-row jump outside the cards.
                if (
                    Math.abs(dy) < 100 &&
                    ((dx < 0 &&
                        diagram.transitions.some(
                            (other) =>
                                other.source === e.target &&
                                other.target === e.source,
                        )) ||
                        Math.abs(dx) > 420)
                ) {
                    from = "bottom";
                    to = "bottom";
                }
                if (Math.abs(dy) > 400 && Math.abs(dx) > 100) {
                    from = "left";
                    to = "left";
                }
                return {
                    id: e.id,
                    source: e.source,
                    target: e.target,
                    label: e.label,
                    type: "smoothstep",
                    pathOptions: { offset: Math.abs(dx) > 420 ? 50 : 20 },
                    sourceHandle: "out-" + from,
                    targetHandle: "in-" + to,
                    ariaLabel: e.label + ": " + e.trigger,
                    markerEnd: {
                        type: MarkerType.ArrowClosed,
                        color: selection === e.id ? "#d3b2ff" : "#77718a",
                    },
                    style: {
                        stroke: selection === e.id ? "#cba6ff" : "#77718a",
                        strokeWidth: selection === e.id ? 2.5 : 1.4,
                    },
                    labelStyle: { fill: "var(--text)", fontSize: 11 },
                    labelBgStyle: { fill: "var(--panel)" },
                    selected: selection === e.id,
                };
            }),
        [diagram, selection, initial],
    );
    return (
        <ReactFlow
            key={documentId + diagram.id}
            nodes={nodes.map((n) => ({ ...n, selected: n.id === selection }))}
            edges={edges}
            nodeTypes={nodeTypes}
            onNodesChange={(changes: NodeChange[]) =>
                setNodes((n) => applyNodeChanges(changes, n))
            }
            onNodeClick={(_, n) => onSelect(n.id)}
            onNodeDoubleClick={(_, n) => {
                const link = (n.data.item as Item).detailLinks?.[0];
                if (link) onOpen(link.targetId);
            }}
            onEdgeClick={(_, e) => onSelect(e.id)}
            onNodeDragStop={(_, n) =>
                onPosition(n.id, n.position.x, n.position.y)
            }
            onMoveEnd={(_, viewport) =>
                localStorage.setItem(
                    "vault-view-" + documentId + "-" + diagram.id,
                    JSON.stringify(viewport),
                )
            }
            minZoom={0.18}
            maxZoom={1.6}
            nodesConnectable={false}
            defaultViewport={savedViewport}
            fitView={!savedViewport}
        >
            <Background gap={24} size={1} />
            <Controls showInteractive={false} />
            <MiniMap
                pannable
                zoomable
                nodeColor="#796491"
                maskColor="var(--minimap-mask)"
            />
        </ReactFlow>
    );
}
export default function Knowledge() {
    const [legacy, setLegacy] = useState(
        location.hash.startsWith("#reference"),
    );
    const [library, setLibrary] = useState<Listing[]>([]),
        [requests, setRequests] = useState<Request[]>([]),
        [envelope, setEnvelope] = useState<Envelope | null>(null),
        [global, setGlobal] = useState<Presentation>(empty),
        [presentation, setPresentation] = useState<Presentation>(empty);
    const [id, setId] = useState(
            new URLSearchParams(location.hash.slice(1)).get("document") ?? "",
        ),
        [selection, setSelection] = useState(""),
        [diagramId, setDiagramId] = useState(""),
        [mode, setMode] = useState<"split" | "diagram" | "read">("diagram"),
        [section, setSection] = useState(
            location.hash.includes("document=") || location.hash === "#library"
                ? "library"
                : "workflows",
        ),
        [query, setQuery] = useState(""),
        [category, setCategory] = useState(""),
        [error, setError] = useState("");
    const [left, setLeft] = useState(true),
        [right, setRight] = useState(true),
        [palette, setPalette] = useState(false),
        [theme, setTheme] = useState(
            localStorage.getItem("vault-theme") ?? "dark",
        );
    const [project, setProject] = useState("Project");
    const [requestContext, setRequestContext] = useState<{
        documentId: string | null;
        targetId: string | null;
        label: string;
    }>({ documentId: null, targetId: null, label: "" });
    const [requestOpen, setRequestOpen] = useState(false),
        [question, setQuestion] = useState(""),
        [handoff, setHandoff] = useState(""),
        [note, setNote] = useState(""),
        [notes, setNotes] = useState<{ note: Note; unresolved: boolean }[]>([]),
        [source, setSource] = useState<{
            path: string;
            line: number;
            text: string;
        } | null>(null),
        [busy, setBusy] = useState(false),
        [showHistory, setShowHistory] = useState(false),
        [revision, setRevision] = useState<number | null>(null);
    const [tabs, setTabs] = useState<string[]>(() => {
        try {
            return JSON.parse(
                localStorage.getItem("vault-document-tabs") ?? "[]",
            );
        } catch {
            return [];
        }
    });
    const fail = (e: unknown) => setError(String(e));
    const refresh = useCallback(async () => {
        const [l, r, g, status] = await Promise.all([
            api<Listing[]>("documents"),
            api<Request[]>("document-requests"),
            api<Presentation>("documents/library/presentation"),
            api<{ project: string }>("document-status"),
        ]);
        setLibrary(l);
        setRequests(r);
        setGlobal(g);
        setProject(status.project);
    }, []);
    useEffect(() => {
        refresh().catch(fail);
        const timer = setInterval(() => refresh().catch(fail), 5000);
        return () => clearInterval(timer);
    }, [refresh]);
    useEffect(() => {
        document.documentElement.dataset.theme = theme;
        localStorage.setItem("vault-theme", theme);
    }, [theme]);
    useEffect(() => {
        localStorage.setItem("vault-document-tabs", JSON.stringify(tabs));
    }, [tabs]);
    const [trail, setTrail] = useState<string[]>(() =>
        (new URLSearchParams(location.hash.slice(1)).get("via") ?? "")
            .split(",")
            .filter(Boolean),
    );
    const open = useCallback((next: string, nextTrail: string[] = []) => {
        setId(next);
        setTrail(nextTrail);
        setSelection(localStorage.getItem("vault-selected-" + next) ?? "");
        setDiagramId(localStorage.getItem("vault-diagram-" + next) ?? "");
        setMode(
            (localStorage.getItem("vault-mode-" + next) as
                "split" | "diagram" | "read") ?? "diagram",
        );
        setRevision(null);
        setSource(null);
        setSection("library");
        setTabs((t) => (t.includes(next) ? t : [...t.slice(-7), next]));
        const hash =
            "document=" +
            next +
            (nextTrail.length ? "&via=" + nextTrail.join(",") : "");
        if (location.hash !== "#" + hash) location.hash = hash;
    }, []);
    const drillDown = (next: string) =>
        open(
            next,
            next === id
                ? trail
                : trail.includes(next)
                  ? trail.slice(0, trail.indexOf(next))
                  : [...trail, id].filter(Boolean).slice(-12),
        );
    const overview = library.find(
        (d) => d.kind === "workflow-overview" && d.published,
    );
    const goWorkflows = () => {
        if (overview) open(overview.id);
        else {
            setId("");
            setSection("workflows");
            location.hash = "workflows";
        }
    };
    useEffect(() => {
        if (section === "workflows" && !id && overview) open(overview.id);
    }, [section, id, overview?.id, open]);
    useEffect(() => {
        const handle = () => {
            if (location.hash.startsWith("#reference")) return;
            const params = new URLSearchParams(location.hash.slice(1));
            const target = params.get("document") ?? "";
            setId(target);
            setTrail((params.get("via") ?? "").split(",").filter(Boolean));
            setSection(
                target || location.hash === "#library"
                    ? "library"
                    : "workflows",
            );
            setSelection(
                localStorage.getItem("vault-selected-" + target) ?? "",
            );
            setDiagramId(localStorage.getItem("vault-diagram-" + target) ?? "");
            setMode(
                (localStorage.getItem("vault-mode-" + target) as
                    "split" | "diagram" | "read") ?? "diagram",
            );
            setRevision(null);
            if (target)
                setTabs((t) =>
                    t.includes(target) ? t : [...t.slice(-7), target],
                );
        };
        window.addEventListener("hashchange", handle);
        return () => window.removeEventListener("hashchange", handle);
    }, []);
    useEffect(() => {
        const listener = (event: KeyboardEvent) => {
            if (
                (event.metaKey || event.ctrlKey) &&
                ["k", "o"].includes(event.key)
            ) {
                event.preventDefault();
                setPalette((p) => !p);
            }
            if (event.key === "Escape") {
                setPalette(false);
                setRequestOpen(false);
            }
        };
        window.addEventListener("keydown", listener);
        return () => window.removeEventListener("keydown", listener);
    }, []);
    const version = library.find((d) => d.id === id)?.version;
    useEffect(() => {
        let active = true;
        if (!id) {
            setEnvelope(null);
            return;
        }
        setSource(null);
        Promise.all([
            api<Envelope>("documents/" + id),
            api<Presentation>("documents/" + id + "/presentation"),
            api<{ note: Note; unresolved: boolean }[]>(
                "documents/" + id + "/notes",
            ),
        ])
            .then(([d, p, n]) => {
                if (active) {
                    setEnvelope(d);
                    setPresentation(p);
                    setNotes(n);
                }
            })
            .catch(fail);
        return () => {
            active = false;
        };
    }, [id, version]);
    const published =
        envelope?.id !== id
            ? undefined
            : revision === null
              ? envelope?.published
              : envelope?.history.find((h) => h.version === revision);
    const doc =
        published?.document ??
        (envelope?.id === id ? envelope?.draft : undefined);
    const diagram =
        doc?.diagrams.find((d) => d.id === diagramId) ?? doc?.diagrams[0];
    const node = diagram?.nodes.find((n) => n.id === selection),
        edge = diagram?.transitions.find((e) => e.id === selection);
    const impact = library.find((d) => d.id === id)?.impact;
    const visible = library.filter(
        (d) =>
            (!category || d.category === category) &&
            (!query ||
                (d.title + " " + d.summary)
                    .toLowerCase()
                    .includes(query.toLowerCase())),
    );
    useEffect(() => {
        setNote("");
        setSource(null);
    }, [selection, id]);
    const savePosition = async (item: string, x: number, y: number) => {
        const next = {
            ...presentation,
            positions: { ...presentation.positions, [item]: { x, y } },
        };
        setPresentation(next);
        try {
            await api("documents/" + id + "/presentation", "PUT", next);
        } catch (e) {
            fail(e);
        }
    };
    const bookmark = async (target: string) => {
        const next = {
            ...global,
            bookmarks: global.bookmarks.includes(target)
                ? global.bookmarks.filter((x) => x !== target)
                : [...global.bookmarks, target],
        };
        try {
            await api("documents/library/presentation", "PUT", next);
            setGlobal(next);
        } catch (e) {
            fail(e);
        }
    };
    const newRequest = (requestedQuestion = "") => {
        setRequestContext({
            documentId: section === "library" && doc ? id : null,
            targetId: section === "library" && doc ? selection || null : null,
            label:
                section === "library" && doc
                    ? (node?.label ?? edge?.label ?? doc.title)
                    : "",
        });
        setQuestion(requestedQuestion);
        setHandoff("");
        setRequestOpen(true);
    };
    const submitRequest = async () => {
        setBusy(true);
        try {
            const r: Request = {
                id: crypto.randomUUID(),
                question,
                documentId: requestContext.documentId,
                targetId: requestContext.targetId,
                status: "open",
                resultDocumentIds: [],
                response: "",
                version: 0,
            };
            const saved = await api<Request>("document-requests", "POST", {
                request: r,
                expectedVersion: 0,
            });
            setHandoff(
                `Use the Project Vault MCP tools to fulfill documentation request ${saved.id}. Read the request and any targeted document/annotation, investigate relevant code and tests, then author and publish a useful explanation or diagram. Static analysis is reference and a sanity check, not the content driver. Update the request with published result IDs and any unanswered questions. This request is for documentation; do not change application behavior.`,
            );
            await refresh();
        } catch (e) {
            fail(e);
        } finally {
            setBusy(false);
        }
    };
    const saveNote = async () => {
        setBusy(true);
        try {
            await api("document-notes", "POST", {
                id: crypto.randomUUID(),
                documentId: id,
                targetId: selection || null,
                markdown: note,
            });
            setNotes(await api("documents/" + id + "/notes"));
            setNote("");
        } catch (e) {
            fail(e);
        } finally {
            setBusy(false);
        }
    };
    const evidence =
        doc?.evidence.filter((e) =>
            (
                node?.evidence ??
                edge?.evidence ??
                doc.evidence.map((x) => x.id)
            ).includes(e.id),
        ) ?? [];
    const links = Array.from(
        new Set([...(doc?.links ?? []), ...(node?.links ?? [])]),
    );
    if (legacy)
        return (
            <>
                <button
                    className="k-return"
                    onClick={() => {
                        setLegacy(false);
                        location.hash = "";
                    }}
                >
                    ← Authored documents
                </button>
                <Suspense fallback={<p>Loading reference inventory…</p>}>
                    <Legacy />
                </Suspense>
            </>
        );
    return (
        <div className="knowledge">
            {left && (
                <aside className="k-sidebar">
                    <div className="k-brand">
                        <span className="k-logo">
                            <GitBranch size={23} />
                        </span>
                        <div>
                            <strong>Project Vault</strong>
                            <small>A connected understanding</small>
                        </div>
                    </div>
                    <button
                        className="k-search"
                        onClick={() => setPalette(true)}
                    >
                        <Search size={15} /> Find a document <kbd>⌘ K</kbd>
                    </button>
                    <div className="k-nav">
                        <button
                            className={
                                section === "workflows" ||
                                doc?.kind === "workflow-overview"
                                    ? "active"
                                    : ""
                            }
                            onClick={goWorkflows}
                        >
                            <GitBranch size={17} /> Workflows{" "}
                            <span>
                                {
                                    library.filter((d) => d.kind === "workflow")
                                        .length
                                }
                            </span>
                        </button>
                        <button
                            className={
                                section === "library" && !id ? "active" : ""
                            }
                            onClick={() => {
                                setSection("library");
                                setId("");
                                location.hash = "library";
                            }}
                        >
                            <Library size={17} /> Library{" "}
                            <span>{library.length}</span>
                        </button>
                        <button
                            className={section === "requests" ? "active" : ""}
                            onClick={() => setSection("requests")}
                        >
                            <MessageSquare size={17} /> Documentation requests{" "}
                            <span>
                                {
                                    requests.filter(
                                        (r) => r.status !== "answered",
                                    ).length
                                }
                            </span>
                        </button>
                    </div>
                    <div className="k-label">YOUR DOCUMENTS</div>
                    <div className="k-doc-list">
                        {library.map((d) => (
                            <button
                                key={d.id}
                                className={id === d.id ? "active" : ""}
                                onClick={() => open(d.id)}
                            >
                                <FileText size={16} />
                                <span>{d.title}</span>
                                {d.impact.status === "Needs review" && (
                                    <i title="Needs review" />
                                )}
                            </button>
                        ))}
                    </div>
                    {global.bookmarks.length > 0 && (
                        <>
                            <div className="k-label">BOOKMARKS</div>
                            {global.bookmarks.map((b) => (
                                <button
                                    className="k-bookmark"
                                    key={b}
                                    onClick={() => open(b)}
                                >
                                    <Bookmark size={14} />
                                    {library.find((d) => d.id === b)?.title ??
                                        "Unresolved document"}
                                </button>
                            ))}
                        </>
                    )}
                    <div className="k-sidebar-bottom">
                        <p>
                            Built around questions.
                            <br />
                            Grounded in your code.
                        </p>
                        <button
                            onClick={() => {
                                setLegacy(true);
                                location.hash = "reference";
                            }}
                        >
                            <GitBranch size={14} /> Source reference inventory{" "}
                            <ChevronRight size={13} />
                        </button>
                    </div>
                </aside>
            )}
            <div className="k-workspace">
                <header className="k-topbar">
                    <button
                        title="Toggle sidebar"
                        onClick={() => setLeft(!left)}
                    >
                        <PanelLeftClose size={17} />
                    </button>
                    <button title="Back" onClick={() => history.back()}>
                        <ArrowLeft size={15} />
                    </button>
                    <button title="Forward" onClick={() => history.forward()}>
                        <ArrowRight size={15} />
                    </button>
                    <span>
                        {project} <ChevronRight size={12} />{" "}
                        {section === "requests"
                            ? "Requests"
                            : (doc?.title ?? "Document library")}
                    </span>
                    <button
                        title="Toggle theme"
                        onClick={() =>
                            setTheme(theme === "dark" ? "light" : "dark")
                        }
                    >
                        {theme === "dark" ? (
                            <Sun size={16} />
                        ) : (
                            <Moon size={16} />
                        )}
                    </button>
                    <button
                        title="Toggle details"
                        onClick={() => setRight(!right)}
                    >
                        <PanelRightClose size={17} />
                    </button>
                </header>
                {id && doc && (
                    <nav className="k-breadcrumbs" aria-label="Document path">
                        <button onClick={goWorkflows}>Workflows</button>
                        {trail.map((parent, i) =>
                            parent === overview?.id ? null : (
                                <span key={parent + i}>
                                    <ChevronRight size={12} />
                                    <button
                                        onClick={() =>
                                            open(parent, trail.slice(0, i))
                                        }
                                    >
                                        {library.find((d) => d.id === parent)
                                            ?.title ?? parent}
                                    </button>
                                </span>
                            ),
                        )}
                        <span>
                            <ChevronRight size={12} />
                            <strong>{levelLabel(doc.kind)}</strong>
                        </span>
                    </nav>
                )}
                {tabs.length > 0 && (
                    <nav className="k-tabs">
                        {tabs
                            .filter((t) => library.some((l) => l.id === t))
                            .map((t) => (
                                <div
                                    key={t}
                                    className={t === id ? "active" : ""}
                                >
                                    <button onClick={() => open(t)}>
                                        <FileText size={13} />
                                        {library.find((l) => l.id === t)?.title}
                                    </button>
                                    <button
                                        aria-label="Close tab"
                                        onClick={() =>
                                            setTabs(tabs.filter((x) => x !== t))
                                        }
                                    >
                                        <X size={12} />
                                    </button>
                                </div>
                            ))}
                    </nav>
                )}
                {error && (
                    <div role="alert" className="k-error">
                        {error}
                        <button onClick={() => setError("")}>
                            <X size={14} />
                        </button>
                    </div>
                )}
                {section === "requests" ? (
                    <main className="k-library">
                        <div className="k-eyebrow">
                            ASK → INVESTIGATE → EXPLAIN
                        </div>
                        <h1>What would you like to understand?</h1>
                        <p className="k-lead">
                            Give Codex a question. Keep the answer here.
                        </p>
                        <button
                            className="k-primary"
                            onClick={() => newRequest()}
                        >
                            <Sparkles size={16} /> Request documentation
                        </button>
                        <div className="k-requests">
                            {requests.map((r) => (
                                <article key={r.id}>
                                    <span className="k-pill">{r.status}</span>
                                    <h3>{r.question}</h3>
                                    {r.documentId && (
                                        <button
                                            onClick={() => open(r.documentId!)}
                                        >
                                            About{" "}
                                            {library.find(
                                                (d) => d.id === r.documentId,
                                            )?.title ?? r.documentId}
                                            {r.targetId
                                                ? " / " + r.targetId
                                                : ""}
                                        </button>
                                    )}
                                    {r.response && <Text text={r.response} />}
                                    <div>
                                        {r.resultDocumentIds.map((d) => (
                                            <button
                                                key={d}
                                                onClick={() => open(d)}
                                            >
                                                <BookOpen size={14} />
                                                {library.find((x) => x.id === d)
                                                    ?.title ?? d}
                                            </button>
                                        ))}
                                    </div>
                                    <button
                                        onClick={() => {
                                            setHandoff(
                                                `Use Project Vault MCP to read and fulfill documentation request ${r.id}. Investigate the code, publish the documentation, and update this request with results and remaining questions. Do not change application behavior.`,
                                            );
                                            setRequestOpen(true);
                                        }}
                                    >
                                        Copy Codex handoff
                                    </button>
                                </article>
                            ))}
                        </div>
                    </main>
                ) : !id && section === "workflows" ? (
                    <main className="k-library">
                        <div className="k-eyebrow">1 · WORKFLOWS</div>
                        <h1>What does this project do?</h1>
                        <p className="k-lead">
                            An authored map of meaningful workflows. Add detail
                            as you need it.
                        </p>
                        <button
                            className="k-primary"
                            onClick={() =>
                                newRequest(
                                    "Create a workflow overview for this project, grouped by feature. Link authored workflows; do not generate a code inventory.",
                                )
                            }
                        >
                            Request workflow overview
                        </button>
                        <div className="k-library-grid">
                            {library
                                .filter((d) => d.kind === "workflow")
                                .map((d) => (
                                    <article
                                        className="k-document-card"
                                        key={d.id}
                                    >
                                        <h2>{d.title}</h2>
                                        <p>{d.summary}</p>
                                        <button onClick={() => open(d.id)}>
                                            Open workflow{" "}
                                            <ArrowRight size={14} />
                                        </button>
                                    </article>
                                ))}
                        </div>
                    </main>
                ) : !id ? (
                    <main className="k-library">
                        <div className="k-eyebrow">YOUR PROJECT, EXPLAINED</div>
                        <h1>
                            A place for the things
                            <br />
                            worth understanding.
                        </h1>
                        <p className="k-lead">
                            Follow a workflow. Understand a decision.
                            <br />
                            Ask a better question about your code.
                        </p>
                        <div className="k-library-actions">
                            <button
                                className="k-primary"
                                onClick={() => newRequest()}
                            >
                                <Sparkles size={16} /> Request documentation
                            </button>
                            <span>
                                Investigated and authored by your coding agent
                            </span>
                        </div>
                        <div className="k-section-title">
                            <h2>Explore the project</h2>
                            <select
                                aria-label="Filter category"
                                value={category}
                                onChange={(e) => setCategory(e.target.value)}
                            >
                                <option value="">All topics</option>
                                {Array.from(
                                    new Set(library.map((d) => d.category)),
                                ).map((c) => (
                                    <option key={c}>{c}</option>
                                ))}
                            </select>
                        </div>
                        <div className="k-library-grid">
                            {visible.map((d) => (
                                <article key={d.id} className="k-document-card">
                                    <div className="k-card-top">
                                        <span>
                                            <GitBranch size={18} />{" "}
                                            {d.category || "Document"}
                                        </span>
                                        <button
                                            aria-label="Bookmark document"
                                            onClick={() => bookmark(d.id)}
                                        >
                                            <Bookmark
                                                size={17}
                                                fill={
                                                    global.bookmarks.includes(
                                                        d.id,
                                                    )
                                                        ? "currentColor"
                                                        : "none"
                                                }
                                            />
                                        </button>
                                    </div>
                                    <button
                                        className="k-card-body"
                                        onClick={() => open(d.id)}
                                    >
                                        <h2>{d.title}</h2>
                                        <p>{d.summary}</p>
                                        <div>
                                            <span
                                                className={
                                                    "k-pill " +
                                                    (d.impact.status ===
                                                    "Needs review"
                                                        ? "warning"
                                                        : "")
                                                }
                                            >
                                                {d.impact.status}
                                            </span>
                                            <span>
                                                Explore <ArrowRight size={15} />
                                            </span>
                                        </div>
                                    </button>
                                </article>
                            ))}
                        </div>
                        {library.length === 0 && (
                            <div className="k-empty">
                                <BookOpen size={28} />
                                <h2>Start with a question, not an index.</h2>
                                <p>
                                    Request a workflow, architectural
                                    explanation, or state machine. Codex
                                    publishes the result here through MCP.
                                </p>
                            </div>
                        )}
                    </main>
                ) : doc ? (
                    <>
                        <div className="k-doc-header">
                            <div>
                                <div className="k-eyebrow">
                                    {levelLabel(doc.kind)} · {doc.category} ·{" "}
                                    {revision !== null
                                        ? "HISTORICAL REVISION"
                                        : envelope?.published
                                          ? "AUTHORED DOCUMENT"
                                          : "DRAFT"}
                                </div>
                                <h1>{doc.title}</h1>
                                <p>{doc.summary}</p>
                            </div>
                            <button
                                aria-label="Bookmark current document"
                                onClick={() => bookmark(id)}
                            >
                                <Bookmark
                                    size={19}
                                    fill={
                                        global.bookmarks.includes(id)
                                            ? "currentColor"
                                            : "none"
                                    }
                                />
                            </button>
                        </div>
                        {impact?.status === "Needs review" && (
                            <div className="k-review-banner">
                                Needs review ·{" "}
                                {impact.contextChanged
                                    ? "Repository context changed. "
                                    : ""}
                                {impact.changedFiles.length} supporting files
                                changed. The last published explanation is
                                retained.
                            </div>
                        )}
                        {revision !== null && (
                            <div className="k-review-banner">
                                Viewing revision {revision}.{" "}
                                <button onClick={() => setRevision(null)}>
                                    Return to current
                                </button>
                            </div>
                        )}
                        <div className="k-doc-toolbar">
                            <div>
                                {doc.kind === "function" ||
                                doc.kind === "model" ? (
                                    <span className="k-pill">
                                        Explanation + code
                                    </span>
                                ) : (
                                    (["split", "diagram", "read"] as const).map(
                                        (m) => (
                                            <button
                                                key={m}
                                                className={
                                                    mode === m ? "active" : ""
                                                }
                                                onClick={() => {
                                                    setMode(m);
                                                    localStorage.setItem(
                                                        "vault-mode-" + id,
                                                        m,
                                                    );
                                                }}
                                            >
                                                {m === "split"
                                                    ? "Diagram + explanation"
                                                    : m === "diagram"
                                                      ? "Diagram"
                                                      : "Read"}
                                            </button>
                                        ),
                                    )
                                )}
                            </div>
                            {doc.diagrams.length > 1 && (
                                <select
                                    aria-label="Select diagram"
                                    value={diagram?.id}
                                    onChange={(e) => {
                                        setDiagramId(e.target.value);
                                        localStorage.setItem(
                                            "vault-diagram-" + id,
                                            e.target.value,
                                        );
                                        setSelection("");
                                    }}
                                >
                                    {doc.diagrams.map((d) => (
                                        <option key={d.id} value={d.id}>
                                            {d.title}
                                        </option>
                                    ))}
                                </select>
                            )}
                            <button onClick={() => newRequest()}>
                                <MessageSquare size={14} /> Ask about this
                            </button>
                        </div>
                        {doc.kind === "function" || doc.kind === "model" ? (
                            <DeclarationPage
                                key={id}
                                id={id}
                                version={published?.version}
                                doc={doc}
                                onOpen={drillDown}
                            />
                        ) : (
                            <div className="k-document-body">
                                {mode !== "read" && diagram && (
                                    <section className="k-canvas-pane">
                                        <div className="k-canvas-caption">
                                            <strong>{diagram.title}</strong>
                                            <span>{diagram.description}</span>
                                        </div>
                                        <DiagramCanvas
                                            key={id + diagram.id}
                                            onOpen={drillDown}
                                            documentId={id}
                                            diagram={diagram}
                                            presentation={presentation}
                                            onPosition={savePosition}
                                            onSelect={(value) => {
                                                setSelection(value);
                                                localStorage.setItem(
                                                    "vault-selected-" + id,
                                                    value,
                                                );
                                                setRight(true);
                                            }}
                                            selection={selection}
                                        />
                                    </section>
                                )}
                                {(mode !== "diagram" || !diagram) && (
                                    <article
                                        className={
                                            "k-reading " +
                                            (mode === "read" ? "full" : "")
                                        }
                                    >
                                        <Text text={doc.markdown} />
                                        {doc.unknowns.length > 0 && (
                                            <div className="k-unknowns">
                                                <h3>Open questions & limits</h3>
                                                {doc.unknowns.map((u, i) => (
                                                    <p key={i}>{u}</p>
                                                ))}
                                            </div>
                                        )}
                                    </article>
                                )}
                            </div>
                        )}
                    </>
                ) : (
                    <div className="k-empty">
                        {envelope === null
                            ? "Loading document…"
                            : "This document has no published content."}
                    </div>
                )}
                <footer className="k-status">
                    <span>
                        <span className="k-status-dot" /> Local project vault
                    </span>
                    <span>
                        {
                            library.filter(
                                (d) => d.impact.status === "Needs review",
                            ).length
                        }{" "}
                        need review ·{" "}
                        {library.filter((d) => d.published).length} authored
                        documents
                    </span>
                    <span>Source checks support agent review</span>
                </footer>
            </div>
            {right && id && doc && section === "library" && (
                <aside className="k-details">
                    <div className="k-details-title">
                        <span>
                            {selection
                                ? "SELECTED " +
                                  (edge
                                      ? "TRANSITION"
                                      : doc.kind === "action"
                                        ? "OPERATION"
                                        : doc.kind === "workflow-overview"
                                          ? "WORKFLOW"
                                          : "STATE")
                                : "DOCUMENT DETAILS"}
                        </span>
                        {selection && (
                            <button
                                title="Document details"
                                onClick={() => setSelection("")}
                            >
                                <X size={14} />
                            </button>
                        )}
                    </div>
                    <h2>
                        {node?.label ?? edge?.label ?? "About this document"}
                    </h2>
                    {node && <Text text={node.description} />}{" "}
                    {edge && (
                        <>
                            <Text text={edge.description} />
                            <dl>
                                <dt>Trigger</dt>
                                <dd>{edge.trigger}</dd>
                                <dt>Condition</dt>
                                <dd>
                                    {edge.condition ||
                                        "Unconditional once triggered"}
                                </dd>
                                <dt>Effect</dt>
                                <dd>{edge.effect}</dd>
                            </dl>
                        </>
                    )}
                    {!selection && (
                        <>
                            <p className="k-detail-summary">{doc.summary}</p>
                            <span
                                className={
                                    "k-pill " +
                                    (impact?.status === "Needs review"
                                        ? "warning"
                                        : "")
                                }
                            >
                                <Check size={12} />
                                {impact?.status ?? "Draft"}
                            </span>
                            <p className="k-muted">
                                Last reviewed{" "}
                                {published
                                    ? new Date(
                                          published.review.at,
                                      ).toLocaleDateString()
                                    : "not yet"}
                                <br />
                                {published?.review.reason}
                            </p>
                        </>
                    )}
                    {node?.markers.map((m, i) => (
                        <div className="k-concern" key={i}>
                            <strong>
                                ◇ {m.category} · {m.certainty}
                            </strong>
                            <p>{m.reason}</p>
                        </div>
                    ))}
                    <DetailLinks
                        links={
                            selection
                                ? (node?.detailLinks ?? edge?.detailLinks ?? [])
                                : (doc.detailLinks ?? [])
                        }
                        onOpen={drillDown}
                    />
                    {selection &&
                        doc.kind === "workflow" &&
                        !(node?.detailLinks ?? edge?.detailLinks ?? []).some(
                            (l) => l.relation === "expands",
                        ) && (
                            <button
                                className="k-ask-selection"
                                onClick={() =>
                                    newRequest(
                                        "Document the implementation of " +
                                            (node?.label ?? edge?.label) +
                                            ". Create an action detail diagram showing ordered calls, data flow, checks, side effects and failures, and link it from this item.",
                                    )
                                }
                            >
                                Request action detail
                            </button>
                        )}
                    {selection &&
                        doc.kind === "action" &&
                        node &&
                        ["function-call", "function"].includes(node.kind) &&
                        !(node.detailLinks ?? []).some(
                            (l) => l.relation === "calls",
                        ) && (
                            <button
                                className="k-ask-selection"
                                onClick={() =>
                                    newRequest(
                                        "Document the function behind " +
                                            node.label +
                                            ". Bind the exact declaration, explain its contract, and link relevant models. If this is a framework or external boundary, explain that explicitly.",
                                    )
                                }
                            >
                                Document this function
                            </button>
                        )}
                    {edge && doc.kind === "action" && (
                        <dl>
                            {[
                                ["Inputs", edge.inputs],
                                ["Outputs", edge.outputs],
                                ["Side effects", edge.sideEffects],
                            ].map(([label, values]) =>
                                Array.isArray(values) && values.length > 0 ? (
                                    <div key={String(label)}>
                                        <dt>{label}</dt>
                                        <dd>{values.join("; ")}</dd>
                                    </div>
                                ) : null,
                            )}
                        </dl>
                    )}
                    <button
                        className="k-ask-selection"
                        onClick={() => newRequest()}
                    >
                        <Sparkles size={15} /> Ask Codex to explain or expand
                    </button>
                    <details
                        className="k-evidence-section"
                        key={id + selection}
                        open={!!selection}
                    >
                        <summary>
                            Supporting evidence{" "}
                            <span>{evidence.length} references</span>
                        </summary>
                        {evidence.map((e) => (
                            <button
                                className="k-evidence"
                                key={e.id}
                                onClick={() =>
                                    api<{
                                        path: string;
                                        line: number;
                                        text: string;
                                    }>(
                                        "document-source?path=" +
                                            encodeURIComponent(e.path) +
                                            "&line=" +
                                            e.line +
                                            "&count=" +
                                            Math.min(
                                                200,
                                                e.endLine - e.line + 1,
                                            ),
                                    )
                                        .then(setSource)
                                        .catch(fail)
                                }
                            >
                                <FileText size={13} />
                                <span>
                                    {e.path.split("/").pop()}
                                    <small>
                                        Lines {e.line}–{e.endLine}
                                    </small>
                                </span>
                            </button>
                        ))}
                        {source && (
                            <div className="k-source">
                                <button onClick={() => setSource(null)}>
                                    Close source <X size={12} />
                                </button>
                                <small>{source.path}</small>
                                <pre>{source.text}</pre>
                            </div>
                        )}
                    </details>
                    {links.length > 0 && (
                        <>
                            <h3>Explore further</h3>
                            {links.map((l) => (
                                <button
                                    className="k-linked"
                                    key={l}
                                    onClick={() => drillDown(l)}
                                >
                                    <BookOpen size={14} />
                                    {library.find((d) => d.id === l)?.title ??
                                        l}
                                </button>
                            ))}
                        </>
                    )}
                    <h3>Your annotations</h3>
                    {notes
                        .filter(
                            (n) =>
                                n.unresolved ||
                                n.note.targetId === (selection || null),
                        )
                        .map((n) => (
                            <div className="k-note" key={n.note.id}>
                                {n.unresolved && (
                                    <small className="warning">
                                        Unresolved target: {n.note.targetId}
                                    </small>
                                )}
                                <Text text={n.note.markdown} />
                            </div>
                        ))}
                    <textarea
                        aria-label="Annotation"
                        placeholder={
                            selection
                                ? "Leave a question or correction on this item…"
                                : "Leave a note on this document…"
                        }
                        value={note}
                        onChange={(e) => setNote(e.target.value)}
                    />
                    <button disabled={!note.trim() || busy} onClick={saveNote}>
                        Save annotation
                    </button>
                    <h3>Linked from</h3>
                    <Backlinks id={id} onOpen={drillDown} />
                    <button onClick={() => setShowHistory(!showHistory)}>
                        Revision history <ChevronRight size={13} />
                    </button>
                    {showHistory && (
                        <div className="k-history">
                            {[...(envelope?.history ?? [])]
                                .reverse()
                                .map((h) => (
                                    <button
                                        key={h.version}
                                        onClick={() => setRevision(h.version)}
                                    >
                                        Revision {h.version} ·{" "}
                                        {new Date(
                                            h.review.at,
                                        ).toLocaleDateString()}
                                    </button>
                                ))}
                        </div>
                    )}
                </aside>
            )}
            {palette && (
                <div className="k-overlay" onClick={() => setPalette(false)}>
                    <div
                        className="k-modal"
                        onClick={(e) => e.stopPropagation()}
                    >
                        <h2>Find a document</h2>
                        <input
                            autoFocus
                            placeholder="Search questions, titles, and descriptions…"
                            value={query}
                            onChange={(e) => setQuery(e.target.value)}
                        />
                        {visible.map((d) => (
                            <button
                                className="k-search-result"
                                key={d.id}
                                onClick={() => {
                                    open(d.id);
                                    setPalette(false);
                                    setQuery("");
                                }}
                            >
                                <FileText size={16} />
                                <span>
                                    {d.title}
                                    <small>{d.summary}</small>
                                </span>
                            </button>
                        ))}
                    </div>
                </div>
            )}
            {requestOpen && (
                <div
                    className="k-overlay"
                    onClick={() => setRequestOpen(false)}
                >
                    <div
                        className="k-modal"
                        onClick={(e) => e.stopPropagation()}
                    >
                        <div className="k-modal-title">
                            <h2>
                                {handoff
                                    ? "Ready for Codex"
                                    : "What would you like to understand?"}
                            </h2>
                            <button
                                title="Close"
                                onClick={() => setRequestOpen(false)}
                            >
                                <X size={18} />
                            </button>
                        </div>
                        {handoff ? (
                            <>
                                <p>
                                    Your request is saved. Paste this into your
                                    current Codex task. The published answer
                                    will appear here.
                                </p>
                                <textarea
                                    aria-label="Codex handoff"
                                    readOnly
                                    value={handoff}
                                />
                                <button
                                    className="k-primary"
                                    onClick={() =>
                                        navigator.clipboard
                                            .writeText(handoff)
                                            .catch(fail)
                                    }
                                >
                                    Copy handoff
                                </button>
                            </>
                        ) : (
                            <>
                                <p>
                                    {requestContext.documentId
                                        ? "About " + requestContext.label
                                        : "Request a workflow, state machine, architecture explanation, or anything you want to understand."}
                                </p>
                                <textarea
                                    autoFocus
                                    aria-label="Documentation question"
                                    placeholder="For example: When can this operation fail, and what happens next?"
                                    value={question}
                                    onChange={(e) =>
                                        setQuestion(e.target.value)
                                    }
                                />
                                <p className="k-muted">
                                    This asks for documentation. Proposed
                                    changes to application behavior belong in a
                                    separate code proposal.
                                </p>
                                <button
                                    className="k-primary"
                                    disabled={!question.trim() || busy}
                                    onClick={submitRequest}
                                >
                                    Save request & prepare handoff
                                </button>
                            </>
                        )}
                    </div>
                </div>
            )}
        </div>
    );
}
function Backlinks({
    id,
    onOpen,
}: {
    id: string;
    onOpen: (id: string) => void;
}) {
    const [links, setLinks] = useState<{ id: string; title: string }[]>([]);
    useEffect(() => {
        api<{ id: string; title: string }[]>("documents/" + id + "/backlinks")
            .then(setLinks)
            .catch(() => setLinks([]));
    }, [id]);
    return (
        <>
            {links.length ? (
                links.map((l) => (
                    <button
                        className="k-linked"
                        key={l.id}
                        onClick={() => onOpen(l.id)}
                    >
                        {l.title}
                    </button>
                ))
            ) : (
                <p className="k-muted">No incoming document links yet.</p>
            )}
        </>
    );
}

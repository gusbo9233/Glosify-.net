import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import Markdown from "react-markdown";
import {
    Activity,
    ArrowLeft,
    ArrowRight,
    ArrowUpRight,
    BookOpen,
    Bookmark,
    Box,
    Braces,
    Check,
    ChevronDown,
    ChevronRight,
    Cloud,
    Command,
    Database,
    FileText,
    GitBranch,
    GitCompareArrows,
    Layers,
    Link2,
    LoaderCircle,
    Maximize2,
    MessageSquare,
    MoreHorizontal,
    Network,
    PanelLeftClose,
    PanelRightClose,
    Plus,
    RefreshCw,
    Search,
    Settings2,
    ShieldCheck,
    Sparkles,
    Sun,
    TriangleAlert,
    Workflow as WorkflowIcon,
    X,
} from "lucide-react";
import Canvas from "./Canvas";
import { registerVaultTools } from "./webmcp";
import {
    api,
    type Element,
    type Snapshot,
    type Freshness,
    type Layout,
    type Proposal,
    type Edit,
    type Relation,
    type Evidence,
} from "./types";
const layerDefs = [
    { id: "architecture", label: "Architecture", icon: Box },
    { id: "stack", label: "Tech stack", icon: Layers },
    { id: "azure", label: "Azure services", icon: Cloud },
    { id: "models", label: "Models", icon: Database },
    { id: "workflows", label: "Workflows", icon: WorkflowIcon },
    { id: "functions", label: "Functions", icon: Braces },
];
const emptyLayout: Layout = { positions: {}, bookmarks: [] };
type Details = {
    element: Element | null;
    unresolved: boolean;
    annotation: { markdown: string };
    interpretation?: {
        value: { markdown: string; evidence: Evidence[] };
        stale: boolean;
    };
    mentions: string[];
};
type Diff = {
    added: Element[];
    removed: Element[];
    changed: Element[];
    addedRelations: Relation[];
    removedRelations: Relation[];
};
const markdownLinks = (text: string) =>
    text.replace(
        /\[\[([a-f0-9]{24})(?:\|([^\]]+))?\]\]/g,
        (_, id, label) => `[${label ?? id}](#element=${id})`,
    );
function Badge({
    children,
    kind = "",
}: {
    children: React.ReactNode;
    kind?: string;
}) {
    return <span className={"badge " + kind}>{children}</span>;
}
function App() {
    const [snapshot, setSnapshot] = useState<Snapshot | null>(null),
        [status, setStatus] = useState<Freshness | null>(null),
        [layout, setLayout] = useState<Layout>(emptyLayout),
        [loading, setLoading] = useState(true),
        [refreshing, setRefreshing] = useState(false),
        [error, setError] = useState("");
    const [scope, setScope] = useState("");
    const [layer, setLayer] = useState("architecture"),
        [view, setView] = useState<"canvas" | "graph" | "page">("canvas"),
        [mode, setMode] = useState<"current" | "proposal" | "changes">(
            "current",
        ),
        [query, setQuery] = useState(""),
        [limit, setLimit] = useState(90),
        [selected, setSelected] = useState<string>(),
        [tabs, setTabs] = useState<string[]>([]),
        [workflowId, setWorkflowId] = useState<string>(),
        [stateView, setStateView] = useState(false);
    const [leftOpen, setLeftOpen] = useState(true),
        [rightOpen, setRightOpen] = useState(true),
        [theme, setTheme] = useState(
            localStorage.getItem("vault-theme") ?? "dark",
        ),
        [palette, setPalette] = useState(false),
        [paletteQuery, setPaletteQuery] = useState(""),
        [risk, setRisk] = useState(""),
        [localGraph, setLocalGraph] = useState(false),
        [depth, setDepth] = useState(1);
    const [details, setDetails] = useState<Details | null>(null),
        [detailTab, setDetailTab] = useState("overview"),
        [note, setNote] = useState(""),
        [noteSaved, setNoteSaved] = useState(true),
        [notes, setNotes] = useState<{ id: string; unresolved: boolean }[]>([]);
    const [proposals, setProposals] = useState<Proposal[]>([]),
        [proposalId, setProposalId] = useState<string>(),
        [newTitle, setNewTitle] = useState(""),
        [newStep, setNewStep] = useState(""),
        [diff, setDiff] = useState<Diff | null>(null),
        [proposalDirty, setProposalDirty] = useState(false),
        [showDiagnostics, setShowDiagnostics] = useState(false);
    const history = useRef<string[]>([]),
        historyIndex = useRef(-1),
        selectionRef = useRef(selected),
        noteTimer = useRef<Record<string, ReturnType<typeof setTimeout>>>({});
    const index = useMemo(
        () => new Map(snapshot?.elements.map((e) => [e.id, e]) ?? []),
        [snapshot],
    );
    const active = index.get(selected ?? "");
    const proposal = proposals.find((p) => p.id === proposalId);
    const workflow = snapshot?.workflows.find((w) => w.id === workflowId);
    const counts = useMemo(
        () =>
            Object.fromEntries(
                layerDefs.map((l) => [
                    l.id,
                    l.id === "workflows"
                        ? (snapshot?.workflows.length ?? 0)
                        : (snapshot?.elements.filter(
                              (e) => e.layer === l.id && e.kind !== "external",
                          ).length ?? 0),
                ]),
            ),
        [snapshot],
    );
    const load = useCallback(async () => {
        try {
            const [s, t, l, p, n] = await Promise.all([
                api<Snapshot | null>("snapshot"),
                api<Freshness>("status"),
                api<Layout>("layout"),
                api<Proposal[]>("proposals"),
                api<{ id: string; unresolved: boolean }[]>("notes"),
            ]);
            setSnapshot(s);
            setStatus(t);
            setLayout(l);
            setProposals(p);
            setNotes(n);
        } catch (e) {
            setError(String(e));
        } finally {
            setLoading(false);
        }
    }, []);
    useEffect(() => {
        load();
        const timer = setInterval(
            () =>
                api<Freshness>("status")
                    .then(setStatus)
                    .catch(() => {}),
            10000,
        );
        return () => clearInterval(timer);
    }, [load]);
    useEffect(() => {
        document.documentElement.dataset.theme = theme;
        localStorage.setItem("vault-theme", theme);
    }, [theme]);
    const select = useCallback((id: string, push = true) => {
        setSelected(id);
        selectionRef.current = id;
        setRightOpen(true);
        setTabs((t) => (t.includes(id) ? t : [...t.slice(-9), id]));
        if (push) {
            history.current = history.current.slice(
                0,
                historyIndex.current + 1,
            );
            history.current.push(id);
            historyIndex.current = history.current.length - 1;
        }
        window.history.replaceState(null, "", "#element=" + id);
    }, []);
    const go = useCallback(
        (delta: number) => {
            const i = historyIndex.current + delta;
            if (i >= 0 && i < history.current.length) {
                historyIndex.current = i;
                select(history.current[i], false);
            }
        },
        [select],
    );
    useEffect(() => {
        const listener = () => {
            const match = location.hash.match(/element=([a-f0-9]{24})/);
            if (match) select(match[1]);
        };
        listener();
        window.addEventListener("hashchange", listener);
        return () => window.removeEventListener("hashchange", listener);
    }, [select]);
    useEffect(() => {
        const key = (e: KeyboardEvent) => {
            if ((e.metaKey || e.ctrlKey) && ["k", "o"].includes(e.key)) {
                e.preventDefault();
                setPalette((p) => !p);
            }
            if ((e.metaKey || e.ctrlKey) && e.key === "b") {
                e.preventDefault();
                setLeftOpen((p) => !p);
            }
            if (e.key === "Escape") setPalette(false);
            if (e.altKey && e.key === "ArrowLeft") {
                e.preventDefault();
                go(-1);
            }
            if (e.altKey && e.key === "ArrowRight") {
                e.preventDefault();
                go(1);
            }
        };
        window.addEventListener("keydown", key);
        return () => window.removeEventListener("keydown", key);
    }, [go]);
    useEffect(() => {
        if (!selected || selected.startsWith("proposed-")) {
            setDetails(null);
            return;
        }
        let current = true;
        setDetails(null);
        api<Details>("elements/" + selected)
            .then((d) => {
                if (current) {
                    setDetails(d);
                    setNote(d.annotation.markdown);
                    setNoteSaved(true);
                }
            })
            .catch((e) => {
                if (current) setError(String(e));
            });
        return () => {
            current = false;
        };
    }, [selected, snapshot?.id]);
    useEffect(() => {
        setLimit(90);
    }, [layer, query, risk, localGraph, depth, workflowId]);
    useEffect(() => {
        if (!proposal) return;
        api<Diff>("compare/" + proposal.baseSnapshot)
            .then(setDiff)
            .catch((e) => setError(String(e)));
    }, [proposal?.baseSnapshot, snapshot?.id]);
    useEffect(() => {
        if (status?.fresh && snapshot && status.fingerprint !== snapshot.id)
            api<Snapshot>("snapshot")
                .then(setSnapshot)
                .catch((e) => setError(String(e)));
    }, [status?.fingerprint, status?.fresh, snapshot?.id]);
    const refresh = async () => {
        setRefreshing(true);
        setError("");
        let failure = "";
        for (let attempt = 0; attempt < 2; attempt++) {
            try {
                await api("refresh", "POST", {});
                await load();
                failure = "";
                break;
            } catch (e) {
                failure = String(e);
            }
        }
        if (failure) {
            setError(failure);
            await api<Freshness>("status")
                .then(setStatus)
                .catch(() => {});
        }
        setRefreshing(false);
    };
    useEffect(
        () =>
            registerVaultTools({
                focus: select,
                refresh,
                search: (query) =>
                    snapshot?.elements
                        .filter((e) =>
                            (e.name + " " + e.group)
                                .toLowerCase()
                                .includes(query.toLowerCase()),
                        )
                        .slice(0, 50)
                        .map((e) => ({
                            id: e.id,
                            name: e.name,
                            kind: e.kind,
                        })) ?? [],
            }),
        [snapshot, select],
    );
    const saveLayout = useCallback((value: Layout) => {
        setLayout(value);
        api("layout", "PUT", value).catch((e) => setError(String(e)));
    }, []);
    const bookmark = (id: string) =>
        saveLayout({
            ...layout,
            bookmarks: layout.bookmarks.includes(id)
                ? layout.bookmarks.filter((x) => x !== id)
                : [...layout.bookmarks, id],
        });
    const changeNote = (value: string) => {
        setNote(value);
        setNoteSaved(false);
        const id = selected;
        if (!id) return;
        clearTimeout(noteTimer.current[id]);
        noteTimer.current[id] = setTimeout(() => {
            api("notes/" + id, "PUT", {
                elementId: id,
                markdown: value,
                snapshotId: snapshot?.id ?? "",
            })
                .then(() => {
                    if (selectionRef.current === id) setNoteSaved(true);
                })
                .catch((e) => setError(String(e)));
        }, 500);
    };
    const openLayer = (id: string) => {
        setScope("");
        setLayer(id);
        setWorkflowId(undefined);
        setStateView(false);
        setQuery("");
        setView("canvas");
    };
    const openWorkflow = (id: string) => {
        setScope("");
        const w = snapshot?.workflows.find((w) => w.id === id);
        if (w) {
            setWorkflowId(id);
            setLayer("workflows");
            setView("canvas");
            setStateView(false);
            select(w.entryId);
        }
    };
    const graphFocus = localGraph && view === "graph" ? selected : undefined;
    const candidates = useMemo(() => {
        if (!snapshot) return [];
        let values = snapshot.elements.filter((e) => e.kind !== "external");
        if (layer === "workflows" && workflow)
            values = workflow.members
                .map((id) => index.get(id))
                .filter((e): e is Element => !!e);
        else if (layer === "workflows")
            values = values.filter((e) => e.entryPoint);
        else values = values.filter((e) => e.layer === layer);
        if (scope)
            values = values.filter((e) =>
                e.evidence.some((x) => x.path.startsWith(scope + "/")),
            );
        if (query)
            values = values.filter((e) =>
                (e.name + " " + e.group + " " + e.route + " " + e.summary)
                    .toLowerCase()
                    .includes(query.toLowerCase()),
            );
        if (risk)
            values = values.filter((e) =>
                e.concerns.some((c) => c.category === risk),
            );
        if (view === "graph" && localGraph && graphFocus) {
            const ids = new Set([graphFocus]);
            for (let n = 0; n < depth; n++) {
                const frontier = new Set(ids);
                for (const r of snapshot.relations.filter(
                    (r) => frontier.has(r.source) || frontier.has(r.target),
                )) {
                    ids.add(r.source);
                    ids.add(r.target);
                }
            }
            values = snapshot.elements.filter((e) => ids.has(e.id));
        }
        return values.sort(
            (a, b) =>
                (a.kind === "application" ? -1 : 0) -
                    (b.kind === "application" ? -1 : 0) ||
                a.name.localeCompare(b.name),
        );
    }, [
        snapshot,
        layer,
        workflow,
        index,
        query,
        risk,
        view,
        localGraph,
        graphFocus,
        depth,
        scope,
    ]);
    const visible = useMemo(
        () =>
            workflow && view === "canvas"
                ? workflow.members
                      .map((id) => index.get(id))
                      .filter((e): e is Element => !!e)
                : candidates.slice(0, limit),
        [workflow, view, candidates, limit, index],
    );
    const links = useMemo(() => {
        const ids = new Set(visible.map((e) => e.id));
        return (
            snapshot?.relations.filter(
                (r) =>
                    ids.has(r.source) &&
                    ids.has(r.target) &&
                    r.kind !== "appears in",
            ) ?? []
        );
    }, [snapshot, visible]);
    const groupedScene = useMemo(() => {
        const canGroup =
            view === "canvas" &&
            !workflow &&
            !query &&
            !risk &&
            mode === "current" &&
            ["architecture", "models", "functions"].includes(layer) &&
            candidates.length > 18;
        if (!canGroup) return { elements: visible, relations: links };
        const bucket = (e: Element) => {
            const file = e.evidence[0]?.path ?? e.group;
            const parts = file.split("/");
            const count = scope
                ? scope.split("/").length + 1
                : Math.min(2, parts.length - 1);
            return (
                parts.slice(0, Math.min(count, parts.length - 1)).join("/") ||
                file
            );
        };
        const groups = new Map<string, Element[]>();
        const apps = candidates.filter((e) => e.kind === "application");
        for (const e of candidates.filter((e) => e.kind !== "application")) {
            const key = bucket(e);
            groups.set(key, [...(groups.get(key) ?? []), e]);
        }
        const groupNodes: Element[] = [...groups]
            .sort((a, b) => b[1].length - a[1].length)
            .map(([key, items]) => ({
                id: "group:" + key,
                name: key.split("/").at(-1) ?? key,
                kind: "group",
                layer,
                group: key,
                summary: items.length + " linked elements · open to explore",
                status: "extracted",
                async: false,
                entryPoint: false,
                inputs: [],
                checks: [],
                concepts: [],
                concerns: [],
                evidence: items.flatMap((e) => e.evidence).slice(0, 3),
            }));
        const all = [...apps, ...groupNodes];
        const ids = new Set(all.map((e) => e.id));
        const owner = (id: string) => {
            const e = index.get(id);
            if (!e) return "";
            return e.kind === "application" ? e.id : "group:" + bucket(e);
        };
        const projected = new Map<string, Relation>();
        for (const r of snapshot?.relations ?? []) {
            if (r.kind !== "contains") continue;
            const a = owner(r.source),
                b = owner(r.target);
            if (a !== b && ids.has(a) && ids.has(b)) {
                const key = a + r.kind + b;
                projected.set(key, { ...r, id: key, source: a, target: b });
            }
        }
        return { elements: all, relations: [...projected.values()] };
    }, [
        view,
        workflow,
        query,
        risk,
        mode,
        layer,
        candidates,
        visible,
        links,
        scope,
        index,
        snapshot,
    ]);
    const selectCanvas = (id: string) => {
        if (id.startsWith("group:")) {
            setScope(id.slice(6));
            setLimit(90);
        } else select(id);
    };
    const neighborhood =
        snapshot?.relations.filter(
            (r) => r.source === selected || r.target === selected,
        ) ?? [];
    const updateProposal = (fields: Partial<Proposal>) => {
        if (!proposal) return;
        setProposals((ps) =>
            ps.map((p) => (p.id === proposal.id ? { ...p, ...fields } : p)),
        );
        setProposalDirty(true);
    };
    const saveProposal = async () => {
        if (!proposal) return;
        try {
            const saved = await api<Proposal>("proposals", "POST", proposal);
            setProposals((ps) =>
                ps.map((p) => (p.id === saved.id ? saved : p)),
            );
            setProposalDirty(false);
        } catch (e) {
            setError(String(e));
        }
    };
    const createProposal = async () => {
        if (!newTitle.trim() || !snapshot) return;
        try {
            const p = await api<Proposal>("proposals", "POST", {
                id: "",
                title: newTitle,
                baseSnapshot: snapshot.id,
                affectedIds: selected ? [selected] : [],
                narrative: "",
                edits: [],
                criteria: [],
                status: "draft",
                deviations: "",
                version: 0,
            });
            setProposals((ps) => [...ps, p]);
            setProposalId(p.id);
            setMode("proposal");
            setNewTitle("");
            setRightOpen(true);
            setDetailTab("proposal");
        } catch (e) {
            setError(String(e));
        }
    };
    const edit = (value: Edit) => {
        if (!proposal) return;
        updateProposal({
            edits: [...proposal.edits, value],
            affectedIds: [
                ...new Set([
                    ...proposal.affectedIds,
                    ...[value.elementId, value.source, value.target].filter(
                        (x): x is string => !!x && !x.startsWith("proposed-"),
                    ),
                ]),
            ],
        });
    };
    const choosePalette = (e: Element) => {
        select(e.id);
        setPalette(false);
        setPaletteQuery("");
    };
    const paletteItems =
        snapshot?.elements
            .filter(
                (e) =>
                    e.kind !== "external" &&
                    (e.name + " " + e.group)
                        .toLowerCase()
                        .includes(paletteQuery.toLowerCase()),
            )
            .slice(0, 30) ?? [];
    const renderMarkdown = (value: string) => (
        <Markdown
            components={{
                a: ({ href, children }) => (
                    <a
                        href={href}
                        onClick={(e) => {
                            if (href?.startsWith("#element=")) {
                                e.preventDefault();
                                select(href.slice(9));
                            }
                        }}
                    >
                        {children}
                    </a>
                ),
            }}
        >
            {markdownLinks(value)}
        </Markdown>
    );
    const overview = (full = false) =>
        active ? (
            <div className={"element-content " + (full ? "full" : "")}>
                <div className="detail-heading">
                    <span className="eyebrow">
                        {active.kind} / {active.group.split("/").at(-1)}
                    </span>
                    <h2>{active.name}</h2>
                    <p>{active.summary}</p>
                    <div className="badges">
                        <Badge kind={active.status}>{active.status}</Badge>
                        {active.async && <Badge>async</Badge>}
                        {active.route && (
                            <Badge>
                                {active.verb} {active.route}
                            </Badge>
                        )}
                    </div>
                </div>
                {details?.interpretation && (
                    <section>
                        <h4>
                            Agent explanation{" "}
                            {details.interpretation.stale && (
                                <Badge kind="warning">stale</Badge>
                            )}
                        </h4>
                        <div className="markdown">
                            {renderMarkdown(
                                details.interpretation.value.markdown,
                            )}
                        </div>
                    </section>
                )}
                {active.signature && (
                    <section>
                        <h4>Signature</h4>
                        <code className="signature">{active.signature}</code>
                    </section>
                )}
                {active.inputs.length > 0 && (
                    <section>
                        <h4>
                            {active.layer === "models"
                                ? "Properties"
                                : "Inputs"}
                        </h4>
                        {active.inputs.map((p, i) => (
                            <div className="field" key={i}>
                                <span>{p.name}</span>
                                <code>{p.type}</code>
                            </div>
                        ))}
                    </section>
                )}
                {active.output && (
                    <section>
                        <h4>Output</h4>
                        <code className="signature">{active.output}</code>
                    </section>
                )}
                {active.checks.length > 0 && (
                    <section>
                        <h4>
                            <ShieldCheck size={13} /> Checks & behavior
                        </h4>
                        {active.checks.map((c, i) => (
                            <div className="check-row" key={i}>
                                <span className="small-dot" />
                                {c}
                            </div>
                        ))}
                    </section>
                )}
                {active.concerns.length > 0 && (
                    <section>
                        <h4>Critical operations</h4>
                        {active.concerns.map((c, i) => (
                            <div className="concern" key={i}>
                                <Badge kind="warning">
                                    {c.category} · {c.certainty}
                                </Badge>
                                <p>{c.reason}</p>
                            </div>
                        ))}
                    </section>
                )}
                {snapshot?.workflows
                    .filter((w) => w.members.includes(active.id))
                    .slice(0, 15)
                    .map((w) => (
                        <button
                            className="workflow-link"
                            key={w.id}
                            onClick={() => openWorkflow(w.id)}
                        >
                            <WorkflowIcon size={15} />
                            <span>{w.name}</span>
                            <ChevronRight size={13} />
                        </button>
                    ))}
                <section>
                    <h4>Source evidence</h4>
                    {active.evidence.length ? (
                        active.evidence.map((e, i) => (
                            <div key={i} className="source-line">
                                <FileText size={13} />
                                <span>
                                    {e.path}:{e.line}
                                </span>
                                <Badge>
                                    {status?.fresh
                                        ? "current"
                                        : "check freshness"}
                                </Badge>
                            </div>
                        ))
                    ) : (
                        <p className="muted">
                            No repository source for this external boundary or
                            general concept.
                        </p>
                    )}
                </section>
                {full && (
                    <section>
                        <h4>Personal notes</h4>
                        <div className="markdown">
                            {renderMarkdown(
                                details?.annotation.markdown ??
                                    "No notes yet. Add your thoughts in the Notes panel.",
                            )}
                        </div>
                    </section>
                )}
            </div>
        ) : (
            <div className="inspector-empty">
                <Box size={28} />
                <h3>
                    {selected ? "Unresolved element" : "Follow a connection"}
                </h3>
                <p>
                    {selected
                        ? "This identity no longer exists in the current map. Its notes remain available."
                        : "Select a card to see its purpose, relationships, and source evidence."}
                </p>
            </div>
        );
    return (
        <div className="app-shell">
            <nav className="ribbon">
                <button
                    className="brand-mark"
                    aria-label="Project vault home"
                    onClick={() => openLayer("architecture")}
                >
                    <Layers size={25} />
                </button>
                <button
                    title="Project canvas"
                    className={view === "canvas" ? "active" : ""}
                    onClick={() => setView("canvas")}
                >
                    <Box size={21} />
                </button>
                <button
                    title="Relationship graph"
                    className={view === "graph" ? "active" : ""}
                    onClick={() => setView("graph")}
                >
                    <Network size={21} />
                </button>
                <button
                    title="Quick switcher ⌘K"
                    onClick={() => setPalette(true)}
                >
                    <Search size={21} />
                </button>
                <button
                    title="Proposals"
                    className={mode === "proposal" ? "active" : ""}
                    onClick={() => {
                        setMode("proposal");
                        setRightOpen(true);
                        setDetailTab("proposal");
                    }}
                >
                    <MessageSquare size={21} />
                </button>
                <div className="ribbon-bottom">
                    <button
                        title="Toggle theme"
                        onClick={() =>
                            setTheme((t) => (t === "dark" ? "light" : "dark"))
                        }
                    >
                        <Sun size={20} />
                    </button>
                    <button
                        title="Analysis coverage"
                        onClick={() => setShowDiagnostics(true)}
                    >
                        <Settings2 size={20} />
                    </button>
                </div>
            </nav>
            {leftOpen && (
                <aside className="explorer">
                    <div className="vault-title">
                        <div className="project-avatar">
                            {snapshot?.project[0]?.toUpperCase() ?? "V"}
                        </div>
                        <div>
                            <strong>
                                {snapshot?.project ?? "Project Vault"}
                            </strong>
                            <span>PROJECT VAULT</span>
                        </div>
                        <button
                            title="Collapse explorer"
                            onClick={() => setLeftOpen(false)}
                        >
                            <PanelLeftClose size={16} />
                        </button>
                    </div>
                    <button
                        className="search-launch"
                        onClick={() => setPalette(true)}
                    >
                        <Search size={14} />
                        <span>Find anything</span>
                        <kbd>⌘ K</kbd>
                    </button>
                    <div className="section-label">
                        YOUR PROJECT <MoreHorizontal size={15} />
                    </div>
                    <div className="layer-list">
                        {layerDefs.map((l) => (
                            <button
                                key={l.id}
                                onClick={() => openLayer(l.id)}
                                className={layer === l.id ? "active" : ""}
                            >
                                <l.icon size={17} />
                                <span>{l.label}</span>
                                <small>{counts[l.id]}</small>
                            </button>
                        ))}
                    </div>
                    <div className="section-label">
                        {layer === "workflows"
                            ? "WORKFLOW INVENTORY"
                            : "IN THIS LAYER"}
                        <ChevronDown size={13} />
                    </div>
                    <div className="explorer-items">
                        {(layer === "workflows"
                            ? snapshot?.workflows
                                  .filter((w) =>
                                      w.name
                                          .toLowerCase()
                                          .includes(query.toLowerCase()),
                                  )
                                  .slice(0, limit)
                            : candidates.slice(0, limit)
                        )?.map((item) => (
                            <button
                                key={item.id}
                                title={
                                    "summary" in item
                                        ? item.summary
                                        : item.coverage + " tracing"
                                }
                                className={
                                    selected === item.id ||
                                    workflowId === item.id
                                        ? "active"
                                        : ""
                                }
                                onClick={() =>
                                    "entryId" in item
                                        ? openWorkflow(item.id)
                                        : select(item.id)
                                }
                            >
                                {"entryId" in item ? (
                                    <WorkflowIcon size={13} />
                                ) : (
                                    <FileText size={13} />
                                )}
                                <span>{item.name}</span>
                            </button>
                        ))}
                        {candidates.length > limit && (
                            <button
                                className="load-more"
                                onClick={() => setLimit((l) => l + 100)}
                            >
                                Show next 100 · {candidates.length} total
                            </button>
                        )}
                    </div>
                    <div className="section-label">
                        BOOKMARKS <Bookmark size={13} />
                    </div>
                    <div className="bookmarks">
                        {layout.bookmarks.length ? (
                            layout.bookmarks.map((id) => (
                                <button key={id} onClick={() => select(id)}>
                                    <Bookmark size={12} />
                                    <span>
                                        {index.get(id)?.name ??
                                            "Unresolved bookmark"}
                                    </span>
                                </button>
                            ))
                        ) : (
                            <p>Pin the places you return to.</p>
                        )}
                        {notes
                            .filter((n) => n.unresolved)
                            .map((n) => (
                                <button
                                    key={n.id}
                                    onClick={() => {
                                        select(n.id);
                                        setDetailTab("notes");
                                    }}
                                >
                                    <TriangleAlert size={13} />
                                    <span>
                                        Unresolved note · {n.id.slice(0, 6)}
                                    </span>
                                </button>
                            ))}
                    </div>
                    <div className="explorer-footer">
                        <GitBranch size={14} />
                        <span>{status?.branch || "Local worktree"}</span>
                        <span className="small-dot green" />
                    </div>
                </aside>
            )}
            <main className="main-workspace">
                <header className="tabbar">
                    <div className="tab-actions">
                        {!leftOpen && (
                            <button
                                title="Show explorer"
                                onClick={() => setLeftOpen(true)}
                            >
                                <PanelLeftClose size={16} />
                            </button>
                        )}
                        <button title="Back" onClick={() => go(-1)}>
                            <ArrowLeft size={15} />
                        </button>
                        <button title="Forward" onClick={() => go(1)}>
                            <ArrowRight size={15} />
                        </button>
                    </div>
                    <button
                        className={
                            "workspace-tab " + (view !== "page" ? "active" : "")
                        }
                        onClick={() => setView("canvas")}
                    >
                        <Box size={14} />
                        {layerDefs.find((l) => l.id === layer)?.label}
                        <span className="tab-dot" />
                    </button>
                    {tabs.slice(-3).map((id) => (
                        <div
                            className={
                                "workspace-tab " +
                                (view === "page" && selected === id
                                    ? "active"
                                    : "")
                            }
                            key={id}
                        >
                            <button
                                onClick={() => {
                                    select(id);
                                    setView("page");
                                }}
                            >
                                <FileText size={13} />
                                {index.get(id)?.name ?? "Unresolved"}
                            </button>
                            <button
                                title="Close page"
                                onClick={() => {
                                    setTabs((ts) => ts.filter((t) => t !== id));
                                    if (view === "page" && selected === id)
                                        setView("canvas");
                                }}
                            >
                                <X size={12} />
                            </button>
                        </div>
                    ))}
                    <button
                        className="tab-plus"
                        title="Open page"
                        onClick={() => setPalette(true)}
                    >
                        <Plus size={16} />
                    </button>
                    <button
                        className="right-toggle"
                        title="Toggle details"
                        onClick={() => setRightOpen((v) => !v)}
                    >
                        <PanelRightClose size={16} />
                    </button>
                </header>
                <div className="workspace-header">
                    <div className="breadcrumb">
                        {snapshot?.project ?? "Project Vault"}
                        <ChevronRight size={12} />
                        {layerDefs.find((l) => l.id === layer)?.label}
                        {workflow && (
                            <>
                                <ChevronRight size={12} />
                                {workflow.name}
                            </>
                        )}
                    </div>
                    <div className="title-row">
                        <div>
                            <div className="eyebrow">
                                A CONNECTED VIEW OF YOUR CODEBASE
                            </div>
                            <h1>
                                {workflow
                                    ? workflow.name
                                    : layer === "architecture"
                                      ? "The bigger picture"
                                      : layerDefs.find((l) => l.id === layer)
                                            ?.label}
                                <span className="title-count">
                                    {workflow
                                        ? workflow.members.length
                                        : candidates.length}
                                </span>
                            </h1>
                            <p>
                                {workflow
                                    ? "Follow the operations, decisions, and boundaries behind this entry point."
                                    : layer === "architecture"
                                      ? "Explore the pieces. Follow the connections. Understand what happens."
                                      : "Source-backed elements, connected to the rest of your project."}
                            </p>
                        </div>
                        <button
                            className={
                                "refresh-button " + (refreshing ? "busy" : "")
                            }
                            onClick={refresh}
                            disabled={refreshing}
                        >
                            <RefreshCw
                                size={14}
                                className={refreshing ? "spin" : ""}
                            />
                            {refreshing
                                ? "Reading your project…"
                                : "Sync project"}
                        </button>
                    </div>
                    <div className="toolbar">
                        <div className="segmented">
                            <button
                                className={mode === "current" ? "active" : ""}
                                onClick={() => setMode("current")}
                            >
                                <Box size={13} />
                                Current
                            </button>
                            <button
                                className={mode === "proposal" ? "active" : ""}
                                onClick={() => {
                                    setMode("proposal");
                                    setDetailTab("proposal");
                                    setRightOpen(true);
                                }}
                            >
                                <Sparkles size={13} />
                                Proposal
                            </button>
                            <button
                                className={mode === "changes" ? "active" : ""}
                                onClick={() => {
                                    setMode("changes");
                                    setRightOpen(true);
                                    setDetailTab("proposal");
                                }}
                            >
                                <GitCompareArrows size={13} />
                                Changes
                            </button>
                        </div>
                        <div className="toolbar-spacer" />
                        {view === "graph" ? (
                            <>
                                <label className="inline-check">
                                    <input
                                        type="checkbox"
                                        checked={localGraph}
                                        onChange={(e) =>
                                            setLocalGraph(e.target.checked)
                                        }
                                    />
                                    Local
                                </label>
                                {localGraph && (
                                    <select
                                        aria-label="Graph depth"
                                        value={depth}
                                        onChange={(e) =>
                                            setDepth(+e.target.value)
                                        }
                                    >
                                        {[1, 2, 3].map((d) => (
                                            <option key={d} value={d}>
                                                {d} hop{d > 1 ? "s" : ""}
                                            </option>
                                        ))}
                                    </select>
                                )}
                            </>
                        ) : (
                            workflow && (
                                <div className="mini-segment">
                                    <button
                                        className={!stateView ? "active" : ""}
                                        onClick={() => setStateView(false)}
                                    >
                                        Call flow
                                    </button>
                                    <button
                                        className={stateView ? "active" : ""}
                                        onClick={() => setStateView(true)}
                                    >
                                        States
                                    </button>
                                </div>
                            )
                        )}
                        <button
                            className={
                                "icon-label " +
                                (view === "graph" ? "selected" : "")
                            }
                            onClick={() =>
                                setView((v) =>
                                    v === "graph" ? "canvas" : "graph",
                                )
                            }
                        >
                            <Network size={15} />
                            {view === "graph" ? "Graph" : "Connections"}
                        </button>
                    </div>
                </div>
                {error && (
                    <div className="error-banner">
                        <TriangleAlert size={15} />
                        <span>{error}</span>
                        <button onClick={() => setError("")}>
                            <X size={14} />
                        </button>
                    </div>
                )}
                {status && !status.fresh && snapshot && (
                    <div className="freshness-banner">
                        <TriangleAlert size={14} />
                        {status.status === "blocked"
                            ? "Synchronization blocked. Last valid map retained."
                            : `${status.changedFiles} source files changed · This map needs a refresh.`}
                    </div>
                )}
                {mode === "proposal" && (
                    <div className="proposal-banner">
                        <Sparkles size={14} />
                        {proposal ? (
                            <>
                                Editing proposal:{" "}
                                <strong>{proposal.title}</strong>
                                <span>Current code remains unchanged</span>
                                <button
                                    onClick={saveProposal}
                                    disabled={!proposalDirty}
                                >
                                    Save proposal{proposalDirty ? " *" : ""}
                                </button>
                            </>
                        ) : (
                            "Create or select a proposal in the right panel to edit the graph."
                        )}
                    </div>
                )}
                {loading ? (
                    <div className="initial-state">
                        <LoaderCircle size={30} className="spin" />
                        <h2>Opening your vault</h2>
                    </div>
                ) : !snapshot ? (
                    <div className="initial-state">
                        <div className="welcome-symbol">
                            <Layers size={44} />
                        </div>
                        <span className="eyebrow">YOUR CODE, CONNECTED</span>
                        <h2>A new perspective on your project.</h2>
                        <p>
                            Build a source-backed map of your architecture,
                            workflows, and the details that connect them.
                        </p>
                        <button
                            className="primary"
                            onClick={refresh}
                            disabled={refreshing}
                        >
                            <RefreshCw
                                size={15}
                                className={refreshing ? "spin" : ""}
                            />
                            {refreshing
                                ? "Indexing repository…"
                                : "Build project map"}
                        </button>
                        <small>
                            Runs locally. Your source stays in your workspace.
                        </small>
                    </div>
                ) : mode === "changes" ? (
                    <div className="changes-page">
                        <h2>From intention to implementation</h2>
                        {proposal && diff ? (
                            <>
                                <p>
                                    Comparing the proposal’s base with the
                                    current snapshot.
                                </p>
                                <div className="diff-stats">
                                    <div>
                                        <strong>{diff.added.length}</strong>
                                        Added
                                    </div>
                                    <div>
                                        <strong>{diff.changed.length}</strong>
                                        Changed
                                    </div>
                                    <div>
                                        <strong>{diff.removed.length}</strong>
                                        Removed
                                    </div>
                                </div>
                                {(["added", "changed", "removed"] as const).map(
                                    (kind) => (
                                        <section key={kind}>
                                            <h3>{kind}</h3>
                                            {diff[kind]
                                                .slice(0, 100)
                                                .map((e) => (
                                                    <button
                                                        className="diff-item"
                                                        key={e.id}
                                                        onClick={() =>
                                                            select(e.id)
                                                        }
                                                    >
                                                        <span>{e.name}</span>
                                                        <Badge kind={kind}>
                                                            {e.kind}
                                                        </Badge>
                                                    </button>
                                                ))}
                                        </section>
                                    ),
                                )}
                                <h3>Relationships</h3>
                                <p>
                                    {diff.addedRelations.length} added ·{" "}
                                    {diff.removedRelations.length} removed
                                </p>
                                <h3>Remaining deviations</h3>
                                <p>
                                    {proposal.deviations ||
                                        "No deviations recorded. Acceptance still requires verification."}
                                </p>
                            </>
                        ) : (
                            <p>
                                Select a proposal to compare its base snapshot
                                with the current code.
                            </p>
                        )}
                    </div>
                ) : view === "page" ? (
                    <article className="linked-page">
                        <div className="page-kicker">
                            <BookOpen size={16} />
                            LINKED PROJECT PAGE
                            <button onClick={() => setView("canvas")}>
                                <Box size={14} />
                                Back to canvas
                            </button>
                        </div>
                        {overview(true)}
                    </article>
                ) : (
                    <>
                        <div className="canvas-filterbar">
                            {scope && (
                                <button
                                    onClick={() =>
                                        setScope(
                                            scope
                                                .split("/")
                                                .slice(0, -1)
                                                .join("/"),
                                        )
                                    }
                                >
                                    <ArrowLeft size={12} />
                                    {scope.split("/").at(-1)}
                                </button>
                            )}
                            <label>
                                <Search size={13} />
                                <input
                                    aria-label="Filter elements"
                                    placeholder="Filter this view…"
                                    value={query}
                                    onChange={(e) => setQuery(e.target.value)}
                                />
                            </label>
                            <select
                                aria-label="Critical operation filter"
                                value={risk}
                                onChange={(e) => setRisk(e.target.value)}
                            >
                                <option value="">All operations</option>
                                <option value="financial">Financial</option>
                                <option value="security">Security</option>
                                <option value="concurrency">Concurrency</option>
                            </select>
                            <span>
                                {workflow
                                    ? workflow.coverage + " coverage"
                                    : `${groupedScene.elements.some((e) => e.kind === "group") ? groupedScene.elements.length + " groups" : Math.min(limit, candidates.length) + " of " + candidates.length + " elements"}`}
                            </span>
                            {!workflow && candidates.length > limit && (
                                <button
                                    onClick={() => setLimit((l) => l + 100)}
                                >
                                    Load more
                                </button>
                            )}
                        </div>
                        <Canvas
                            elements={groupedScene.elements}
                            relations={groupedScene.relations}
                            workflow={view === "canvas" ? workflow : undefined}
                            stateView={stateView}
                            graph={view === "graph"}
                            layout={layout}
                            proposal={
                                mode === "proposal" ? proposal : undefined
                            }
                            selected={selected}
                            onSelect={selectCanvas}
                            onLayout={(positions) =>
                                saveLayout({ ...layout, positions })
                            }
                            onEdit={edit}
                            fitKey={
                                layer +
                                scope +
                                workflowId +
                                localGraph +
                                depth +
                                query +
                                limit
                            }
                        />
                        {workflow && (
                            <details className="coverage-tray">
                                <summary>
                                    <Activity size={13} />
                                    {workflow.members.length} connected elements
                                    · {workflow.gaps.length} coverage notes
                                    <ChevronDown size={13} />
                                </summary>
                                {workflow.gaps.map((g, i) => (
                                    <p key={i}>{g}</p>
                                ))}
                            </details>
                        )}
                    </>
                )}
                <footer className="statusbar">
                    <span>
                        <span
                            className={
                                "small-dot " +
                                (status?.fresh ? "green" : "amber")
                            }
                        />
                        {status?.fresh
                            ? "Synchronized"
                            : (status?.status ?? "Connecting")}
                    </span>
                    <span>
                        {snapshot?.elements.length.toLocaleString() ?? 0}{" "}
                        elements
                    </span>
                    <span>
                        {snapshot?.workflows.length.toLocaleString() ?? 0}{" "}
                        workflows
                    </span>
                    <button onClick={() => setShowDiagnostics(true)}>
                        Coverage & evidence <ArrowUpRight size={11} />
                    </button>
                    <div className="toolbar-spacer" />
                    <span title={snapshot?.id}>{snapshot?.id.slice(0, 8)}</span>
                    <span>
                        <GitBranch size={11} />
                        {status?.branch ?? "local"}
                    </span>
                </footer>
            </main>
            {rightOpen && (
                <aside className="inspector">
                    <div className="inspector-top">
                        <span>
                            {detailTab === "proposal"
                                ? "PROPOSAL WORKSPACE"
                                : "CONTEXT & CONNECTIONS"}
                        </span>
                        <button
                            title="Close details"
                            onClick={() => setRightOpen(false)}
                        >
                            <X size={15} />
                        </button>
                    </div>
                    <div className="inspector-tabs">
                        {["overview", "links", "notes", "proposal"].map(
                            (tab) => (
                                <button
                                    key={tab}
                                    className={
                                        detailTab === tab ? "active" : ""
                                    }
                                    onClick={() => setDetailTab(tab)}
                                >
                                    {tab[0].toUpperCase() + tab.slice(1)}
                                </button>
                            ),
                        )}
                    </div>
                    {detailTab === "overview" ? (
                        <>
                            <div className="inspector-tools">
                                <button
                                    disabled={!selected}
                                    onClick={() =>
                                        selected && bookmark(selected)
                                    }
                                >
                                    <Bookmark
                                        size={14}
                                        fill={
                                            selected &&
                                            layout.bookmarks.includes(selected)
                                                ? "currentColor"
                                                : "none"
                                        }
                                    />
                                    Bookmark
                                </button>
                                <button
                                    disabled={!selected}
                                    onClick={() => setView("page")}
                                >
                                    <Maximize2 size={13} />
                                    Open page
                                </button>
                            </div>
                            <div className="inspector-scroll">{overview()}</div>
                        </>
                    ) : detailTab === "links" ? (
                        <div className="inspector-scroll">
                            <div className="panel-intro">
                                <Link2 size={22} />
                                <h3>Connected to this</h3>
                                <p>
                                    Typed relationships from the shared project
                                    model.
                                </p>
                            </div>
                            {neighborhood.map((r) => {
                                const incoming = r.target === selected;
                                const other = index.get(
                                    incoming ? r.source : r.target,
                                );
                                return (
                                    <button
                                        className="relation-row"
                                        key={r.id}
                                        onClick={() =>
                                            select(
                                                incoming ? r.source : r.target,
                                            )
                                        }
                                        title={other?.summary}
                                    >
                                        <div>
                                            <span className="eyebrow">
                                                {incoming
                                                    ? r.kind === "calls"
                                                        ? "called by"
                                                        : r.kind + " ←"
                                                    : r.kind + " →"}
                                            </span>
                                            <strong>
                                                {other?.name ??
                                                    "Unresolved reference"}
                                            </strong>
                                        </div>
                                        <ChevronRight size={14} />
                                    </button>
                                );
                            })}
                            <h4 className="panel-section">
                                MENTIONED IN NOTES
                            </h4>
                            {details?.mentions.map((id) => (
                                <button
                                    className="relation-row"
                                    key={id}
                                    onClick={() => select(id)}
                                >
                                    {index.get(id)?.name ?? "Unresolved note"}
                                    <ChevronRight size={13} />
                                </button>
                            ))}
                            {!neighborhood.length && (
                                <p className="muted panel-padding">
                                    Select an element to explore its backlinks.
                                </p>
                            )}
                        </div>
                    ) : detailTab === "notes" ? (
                        <div className="notes-panel">
                            <div className="panel-intro">
                                <MessageSquare size={22} />
                                <h3>Your thinking, alongside the facts.</h3>
                                <p>
                                    Personal Markdown notes survive every
                                    refresh.
                                </p>
                            </div>
                            {selected && details ? (
                                <>
                                    <textarea
                                        aria-label="Personal Markdown note"
                                        placeholder={
                                            "What matters here?\n\nLink another element with [[element-id|Label]]."
                                        }
                                        value={note}
                                        onChange={(e) =>
                                            changeNote(e.target.value)
                                        }
                                    />
                                    <div className="note-status">
                                        {noteSaved ? (
                                            <>
                                                <Check size={12} />
                                                Saved locally
                                            </>
                                        ) : (
                                            <>
                                                <LoaderCircle size={12} />
                                                Saving…
                                            </>
                                        )}
                                        <button
                                            onClick={() =>
                                                navigator.clipboard
                                                    .writeText(
                                                        `[[${selected}|${active?.name ?? "Element"}]]`,
                                                    )
                                                    .catch((e) =>
                                                        setError(String(e)),
                                                    )
                                            }
                                        >
                                            Copy link
                                        </button>
                                    </div>
                                    <div className="markdown note-preview">
                                        {renderMarkdown(note)}
                                    </div>
                                </>
                            ) : (
                                <p className="panel-padding muted">
                                    Select an element to attach a note.
                                </p>
                            )}
                        </div>
                    ) : (
                        <div className="inspector-scroll proposal-panel">
                            <div className="panel-intro">
                                <Sparkles size={22} />
                                <h3>Shape what comes next.</h3>
                                <p>
                                    Describe a change, sketch the connections,
                                    then give the proposal to your agent.
                                </p>
                            </div>
                            <label>
                                Saved proposals
                                <select
                                    value={proposalId ?? ""}
                                    onChange={(e) => {
                                        setProposalId(e.target.value);
                                        setProposalDirty(false);
                                    }}
                                >
                                    <option value="">Choose a proposal</option>
                                    {proposals.map((p) => (
                                        <option key={p.id} value={p.id}>
                                            {p.title} · {p.status}
                                        </option>
                                    ))}
                                </select>
                            </label>
                            <div className="new-proposal">
                                <input
                                    aria-label="New proposal title"
                                    placeholder="Name a new proposal…"
                                    value={newTitle}
                                    onChange={(e) =>
                                        setNewTitle(e.target.value)
                                    }
                                />
                                <button
                                    title="Create proposal"
                                    disabled={!newTitle.trim()}
                                    onClick={createProposal}
                                >
                                    <Plus size={17} />
                                </button>
                            </div>
                            {proposal && (
                                <>
                                    <label>
                                        Title
                                        <input
                                            value={proposal.title}
                                            onChange={(e) =>
                                                updateProposal({
                                                    title: e.target.value,
                                                })
                                            }
                                        />
                                    </label>
                                    <label>
                                        Intended behavior
                                        <textarea
                                            value={proposal.narrative}
                                            onChange={(e) =>
                                                updateProposal({
                                                    narrative: e.target.value,
                                                })
                                            }
                                            placeholder="What should happen differently?"
                                        />
                                    </label>
                                    <label>
                                        Acceptance criteria
                                        <textarea
                                            value={proposal.criteria
                                                .map((c) => c.text)
                                                .join("\n")}
                                            onChange={(e) =>
                                                updateProposal({
                                                    criteria: e.target.value
                                                        .split("\n")
                                                        .filter(Boolean)
                                                        .map(
                                                            (text) =>
                                                                proposal.criteria.find(
                                                                    (c) =>
                                                                        c.text ===
                                                                        text,
                                                                ) ?? {
                                                                    text,
                                                                    verified: false,
                                                                    evidence:
                                                                        "",
                                                                },
                                                        ),
                                                })
                                            }
                                            placeholder="One criterion per line"
                                        />
                                    </label>
                                    <div className="new-proposal">
                                        <input
                                            aria-label="Proposed step label"
                                            placeholder="Add a proposed step…"
                                            value={newStep}
                                            onChange={(e) =>
                                                setNewStep(e.target.value)
                                            }
                                        />
                                        <button
                                            title="Add proposed step"
                                            disabled={!newStep.trim()}
                                            onClick={() => {
                                                edit({
                                                    kind: "add-node",
                                                    elementId:
                                                        "proposed-" +
                                                        crypto.randomUUID(),
                                                    label: newStep,
                                                });
                                                setNewStep("");
                                                setMode("proposal");
                                            }}
                                        >
                                            <Plus size={17} />
                                        </button>
                                    </div>
                                    <p className="hint">
                                        In Proposal mode, drag between card
                                        handles to connect steps. Select a card
                                        or edge and press Backspace to propose
                                        removal.
                                    </p>
                                    {proposal.edits.map((e, i) => (
                                        <div className="edit-row" key={i}>
                                            <span>
                                                {e.kind} ·{" "}
                                                {e.label ??
                                                    index.get(
                                                        e.elementId ??
                                                            e.source ??
                                                            "",
                                                    )?.name ??
                                                    "connection"}
                                            </span>
                                            <button
                                                title="Remove edit"
                                                onClick={() =>
                                                    updateProposal({
                                                        edits: proposal.edits.filter(
                                                            (_, j) => i !== j,
                                                        ),
                                                    })
                                                }
                                            >
                                                <X size={12} />
                                            </button>
                                        </div>
                                    ))}
                                    <button
                                        className="primary full-width"
                                        disabled={!proposalDirty}
                                        onClick={saveProposal}
                                    >
                                        <Check size={14} />
                                        Save proposal{proposalDirty ? " *" : ""}
                                    </button>
                                    <button
                                        className="secondary full-width"
                                        onClick={() =>
                                            navigator.clipboard
                                                .writeText(
                                                    `Use the Project Vault plugin to read and implement proposal ${proposal.id} (${proposal.title}). Check its base snapshot and acceptance criteria, refresh the project map after every implementation step, and record verification and deviations.`,
                                                )
                                                .catch((e) =>
                                                    setError(String(e)),
                                                )
                                        }
                                    >
                                        <Command size={14} />
                                        Copy Codex handoff
                                    </button>
                                    {proposal.baseSnapshot !== snapshot?.id && (
                                        <div className="concern">
                                            <Badge kind="warning">
                                                Base snapshot differs
                                            </Badge>
                                            <p>
                                                Review Changes for affected
                                                assumptions before implementing.
                                            </p>
                                        </div>
                                    )}
                                    <h4>Verification</h4>
                                    {proposal.criteria.map((c, i) => (
                                        <div className="criterion" key={i}>
                                            <label className="inline-check">
                                                <input
                                                    type="checkbox"
                                                    checked={c.verified}
                                                    onChange={(e) =>
                                                        updateProposal({
                                                            criteria:
                                                                proposal.criteria.map(
                                                                    (v, j) =>
                                                                        i === j
                                                                            ? {
                                                                                  ...v,
                                                                                  verified:
                                                                                      e
                                                                                          .target
                                                                                          .checked,
                                                                              }
                                                                            : v,
                                                                ),
                                                        })
                                                    }
                                                />
                                                {c.text}
                                            </label>
                                            <input
                                                placeholder="Verification evidence"
                                                value={c.evidence}
                                                onChange={(e) =>
                                                    updateProposal({
                                                        criteria:
                                                            proposal.criteria.map(
                                                                (v, j) =>
                                                                    i === j
                                                                        ? {
                                                                              ...v,
                                                                              evidence:
                                                                                  e
                                                                                      .target
                                                                                      .value,
                                                                          }
                                                                        : v,
                                                            ),
                                                    })
                                                }
                                            />
                                        </div>
                                    ))}
                                    <label>
                                        Remaining deviations
                                        <textarea
                                            value={proposal.deviations}
                                            onChange={(e) =>
                                                updateProposal({
                                                    deviations: e.target.value,
                                                })
                                            }
                                        />
                                    </label>
                                    <label>
                                        Status
                                        <select
                                            value={proposal.status}
                                            onChange={(e) =>
                                                updateProposal({
                                                    status: e.target.value,
                                                    resultSnapshot:
                                                        snapshot?.id,
                                                })
                                            }
                                        >
                                            {[
                                                "draft",
                                                "in-progress",
                                                "partial",
                                                "implemented",
                                            ].map((s) => (
                                                <option key={s}>{s}</option>
                                            ))}
                                        </select>
                                    </label>
                                    <button
                                        className="secondary full-width"
                                        onClick={saveProposal}
                                    >
                                        Save verification
                                    </button>
                                </>
                            )}
                        </div>
                    )}
                </aside>
            )}
            {palette && (
                <div
                    className="modal-backdrop"
                    onClick={() => setPalette(false)}
                >
                    <div
                        className="command-palette"
                        role="dialog"
                        aria-modal="true"
                        aria-label="Quick switcher"
                        onClick={(e) => e.stopPropagation()}
                    >
                        <div className="palette-input">
                            <Search size={19} />
                            <input
                                autoFocus
                                placeholder="Jump to a function, model, workflow…"
                                value={paletteQuery}
                                onChange={(e) =>
                                    setPaletteQuery(e.target.value)
                                }
                                onKeyDown={(e) => {
                                    if (e.key === "Enter" && paletteItems[0])
                                        choosePalette(paletteItems[0]);
                                }}
                            />
                            <kbd>ESC</kbd>
                        </div>
                        <div className="palette-results">
                            <button
                                onClick={() => {
                                    refresh();
                                    setPalette(false);
                                }}
                            >
                                <RefreshCw size={15} />
                                <span>Sync project</span>
                                <small>Command</small>
                            </button>
                            <button
                                onClick={() => {
                                    setView("graph");
                                    setPalette(false);
                                }}
                            >
                                <Network size={15} />
                                <span>Open relationship graph</span>
                                <small>Command</small>
                            </button>
                            {paletteItems.map((e) => (
                                <button
                                    key={e.id}
                                    onClick={() => choosePalette(e)}
                                >
                                    <FileText size={15} />
                                    <span>
                                        {e.name}
                                        <small>{e.group}</small>
                                    </span>
                                    <Badge>{e.kind}</Badge>
                                </button>
                            ))}
                        </div>
                        <div className="palette-footer">
                            Search across every layer{" "}
                            <span>↵ Open first result</span>
                        </div>
                    </div>
                </div>
            )}
            {showDiagnostics && (
                <div
                    className="modal-backdrop"
                    onClick={() => setShowDiagnostics(false)}
                >
                    <div
                        className="diagnostics-modal"
                        role="dialog"
                        aria-modal="true"
                        aria-label="Coverage and evidence"
                        onClick={(e) => e.stopPropagation()}
                    >
                        <button
                            className="close-modal"
                            onClick={() => setShowDiagnostics(false)}
                        >
                            <X size={18} />
                        </button>
                        <Activity size={26} />
                        <h2>Coverage & evidence</h2>
                        <p>
                            Freshness and certainty are separate. A current
                            snapshot can still contain unresolved paths.
                        </p>
                        <div className="diff-stats">
                            <div>
                                <strong>
                                    {snapshot?.workflows.length ?? 0}
                                </strong>
                                Entry points
                            </div>
                            <div>
                                <strong>
                                    {snapshot?.workflows.filter(
                                        (w) => w.coverage === "partial",
                                    ).length ?? 0}
                                </strong>
                                Partial traces
                            </div>
                            <div>
                                <strong>
                                    {Object.keys(snapshot?.files ?? {}).length}
                                </strong>
                                Tracked source files
                            </div>
                        </div>
                        {snapshot?.diagnostics.map((d, i) => (
                            <p className="diagnostic" key={i}>
                                <TriangleAlert size={14} />
                                {d}
                            </p>
                        ))}
                        {status?.error && (
                            <p className="error-banner">{status.error}</p>
                        )}
                    </div>
                </div>
            )}
        </div>
    );
}
export default App;

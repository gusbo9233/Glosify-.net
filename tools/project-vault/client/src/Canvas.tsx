import { memo, useEffect, useMemo, useState, useRef } from "react";
import {
    ReactFlow,
    Background,
    Controls,
    MiniMap,
    Handle,
    Position,
    useNodesState,
    useEdgesState,
    MarkerType,
    type NodeProps,
    type Connection,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import ELK from "elkjs/lib/elk.bundled.js";
import {
    forceSimulation,
    forceLink,
    forceManyBody,
    forceCenter,
    type SimulationNodeDatum,
    type SimulationLinkDatum,
} from "d3-force";
import {
    Box,
    Braces,
    Database,
    Cloud,
    ArrowUpRight,
    ShieldCheck,
    GitBranch,
    TriangleAlert,
    Coins,
    Globe,
    Workflow as WorkflowIcon,
} from "lucide-react";
import type {
    Element,
    Relation,
    Workflow,
    Layout,
    Proposal,
    Edit,
} from "./types";
const icons: Record<string, typeof Box> = {
    model: Database,
    service: Cloud,
    endpoint: Globe,
    ui: ArrowUpRight,
    function: Braces,
    application: Box,
    decision: GitBranch,
    external: Globe,
    concept: Braces,
};
const Card = memo(({ data, selected }: NodeProps) => {
    const d = data as {
        label: string;
        kind: string;
        subtitle: string;
        status: string;
        critical?: string;
        count?: number;
    };
    const Icon = icons[d.kind] ?? Box;
    return (
        <div
            className={`graph-card ${selected ? "selected" : ""} ${d.kind} ${d.status === "unresolved" ? "uncertain" : ""}`}
        >
            <Handle type="target" position={Position.Left} />
            <div className="card-top">
                <span className={`node-icon ${d.kind}`}>
                    <Icon size={17} />
                </span>
                <span className="eyebrow">{d.kind}</span>
                {d.critical && (
                    <span
                        className={"risk-icon " + d.critical}
                        title={d.critical}
                    >
                        {d.critical === "financial" ? (
                            <Coins size={14} />
                        ) : d.critical === "security" ? (
                            <ShieldCheck size={14} />
                        ) : (
                            <TriangleAlert size={14} />
                        )}
                    </span>
                )}
            </div>
            <strong title={d.label}>{d.label}</strong>
            <div className="card-bottom">
                <span>{d.subtitle}</span>
                {d.count !== undefined && <span>{d.count} links</span>}
            </div>
            <Handle type="source" position={Position.Right} />
        </div>
    );
});
const Dot = memo(({ data, selected }: NodeProps) => {
    const d = data as { label: string; kind: string };
    return (
        <div
            className={`graph-dot ${d.kind} ${selected ? "selected" : ""}`}
            title={d.label}
        >
            <Handle type="target" position={Position.Left} />
            <span />
            <label>{d.label}</label>
            <Handle type="source" position={Position.Right} />
        </div>
    );
});
const nodeTypes = { card: Card, dot: Dot };
const elk = new ELK();
type Props = {
    elements: Element[];
    relations: Relation[];
    workflow?: Workflow;
    stateView: boolean;
    graph: boolean;
    layout: Layout;
    proposal?: Proposal;
    selected?: string;
    onSelect: (id: string) => void;
    onLayout: (positions: Layout["positions"]) => void;
    onEdit: (edit: Edit) => void;
    fitKey: string;
};
export default function Canvas({
    elements,
    relations,
    workflow,
    stateView,
    graph,
    layout,
    proposal,
    selected,
    onSelect,
    onLayout,
    onEdit,
    fitKey,
}: Props) {
    const [nodes, setNodes, onNodesChange] = useNodesState<any>([]);
    const [edges, setEdges, onEdgesChange] = useEdgesState<any>([]);
    const [layingOut, setLayingOut] = useState(false);
    const [layoutTick, setLayoutTick] = useState(0);
    const [flowInstance, setFlowInstance] = useState<any>(null);
    const fitted = useRef("");
    const scene = useMemo(() => {
        const index = new Map(elements.map((e) => [e.id, e]));
        const steps = workflow
            ? stateView
                ? workflow.states
                : workflow.steps
            : null;
        const vertices = steps
            ? steps.map((s) => ({
                  id: s.id,
                  elementId: s.elementId,
                  label: s.label,
                  kind: s.kind,
                  subtitle: s.evidence
                      ? `${s.evidence.path.split("/").at(-1)}:${s.evidence.line}`
                      : "",
                  status: s.kind === "unresolved" ? "unresolved" : "extracted",
                  critical: index.get(s.elementId ?? "")?.concerns[0]?.category,
              }))
            : elements.map((e) => ({
                  id: e.id,
                  elementId: e.id,
                  label: e.name,
                  kind: e.kind,
                  subtitle:
                      e.kind === "group"
                          ? e.summary
                          : (e.route ?? e.group.split("/").at(-1) ?? e.status),
                  status: e.status,
                  critical: e.concerns?.[0]?.category,
              }));
        let links = workflow
            ? (stateView ? workflow.transitions : workflow.edges).map(
                  (e, i) => ({
                      id: `flow-${i}`,
                      source: e.source,
                      target: e.target,
                      label: e.label,
                      status: "extracted",
                  }),
              )
            : relations.map((e) => ({
                  id: e.id,
                  source: e.source,
                  target: e.target,
                  label: e.kind,
                  status: e.status,
              }));
        const removed = new Set(
            proposal?.edits
                .filter((e) => e.kind === "remove-node")
                .map((e) => e.elementId),
        );
        let v = vertices.filter((n) => !removed.has(n.elementId ?? n.id));
        for (const edit of proposal?.edits ?? []) {
            if (edit.kind === "add-node" && edit.elementId)
                v.push({
                    id: edit.elementId,
                    elementId: edit.elementId,
                    label: edit.label ?? "Proposed step",
                    kind: "proposal",
                    subtitle: "Proposed addition",
                    status: "proposed",
                    critical: undefined,
                });
            if (edit.kind === "disconnect")
                links = links.filter(
                    (e) =>
                        !(e.source === edit.source && e.target === edit.target),
                );
            if (edit.kind === "connect" && edit.source && edit.target)
                links.push({
                    id: "proposal-" + edit.source + edit.target,
                    source: edit.source,
                    target: edit.target,
                    label: edit.label ?? "proposed",
                    status: "proposed",
                });
        }
        const ids = new Set(v.map((n) => n.id));
        links = links.filter((e) => ids.has(e.source) && ids.has(e.target));
        return { vertices: v, links };
    }, [elements, relations, workflow, stateView, proposal]);
    useEffect(() => {
        let cancelled = false;
        setLayingOut(true);
        const run = async () => {
            let positions: Record<string, { x: number; y: number }> = {};
            if (graph) {
                type P = SimulationNodeDatum & { id: string };
                const points: P[] = scene.vertices.map((n, i) => ({
                    id: n.id,
                    x: Math.cos(i) * 150,
                    y: Math.sin(i) * 150,
                }));
                const links: SimulationLinkDatum<P>[] = scene.links.map(
                    (e) => ({ source: e.source, target: e.target }),
                );
                const sim = forceSimulation(points)
                    .force(
                        "link",
                        forceLink<P, SimulationLinkDatum<P>>(links)
                            .id((d) => d.id)
                            .distance(100),
                    )
                    .force("charge", forceManyBody().strength(-420))
                    .force("center", forceCenter(400, 300))
                    .stop();
                for (let i = 0; i < 160; i++) sim.tick();
                positions = Object.fromEntries(
                    points.map((p) => [p.id, { x: p.x ?? 0, y: p.y ?? 0 }]),
                );
            } else if (scene.vertices.some((n) => n.kind === "group")) {
                const apps = scene.vertices.filter(
                    (n) => n.kind === "application",
                );
                const groups = scene.vertices.filter(
                    (n) => n.kind !== "application",
                );
                positions = Object.fromEntries([
                    ...apps.map((n, i) => [n.id, { x: 150 + i * 320, y: 0 }]),
                    ...groups.map((n, i) => [
                        n.id,
                        {
                            x: (i % 3) * 320,
                            y: apps.length
                                ? 190 + Math.floor(i / 3) * 170
                                : Math.floor(i / 3) * 170,
                        },
                    ]),
                ]);
            } else if (scene.vertices.length) {
                const result = await elk.layout({
                    id: "root",
                    layoutOptions: {
                        "elk.algorithm": "layered",
                        "elk.direction": "RIGHT",
                        "elk.spacing.nodeNode": "42",
                        "elk.layered.spacing.nodeNodeBetweenLayers": "92",
                    },
                    children: scene.vertices.map((n) => ({
                        id: n.id,
                        width: 240,
                        height: 106,
                    })),
                    edges: scene.links.map((e) => ({
                        id: e.id,
                        sources: [e.source],
                        targets: [e.target],
                    })),
                });
                positions = Object.fromEntries(
                    (result.children ?? []).map((n) => [
                        n.id,
                        { x: n.x ?? 0, y: n.y ?? 0 },
                    ]),
                );
            }
            if (cancelled) return;
            setNodes(
                scene.vertices.map((v) => ({
                    id: v.id,
                    type: graph ? "dot" : "card",
                    position:
                        layoutTick === 0 && !graph && layout.positions[v.id]
                            ? layout.positions[v.id]
                            : (positions[v.id] ?? { x: 0, y: 0 }),
                    data: {
                        ...v,
                        count: scene.links.filter(
                            (e) => e.source === v.id || e.target === v.id,
                        ).length,
                    },
                    selected: v.elementId === selected,
                })),
            );
            setEdges(
                scene.links.map((e) => ({
                    ...e,
                    type: "smoothstep",
                    label: graph ? undefined : e.label,
                    animated: e.status === "proposed",
                    style: {
                        stroke:
                            e.status === "proposed"
                                ? "var(--accent)"
                                : e.status === "interpreted"
                                  ? "#9c875b"
                                  : "#52616b",
                        strokeDasharray:
                            e.status === "interpreted" ? "5 4" : undefined,
                        strokeWidth: 1.3,
                    },
                    markerEnd: graph
                        ? undefined
                        : { type: MarkerType.ArrowClosed, color: "#74828c" },
                    labelStyle: { fill: "var(--muted)", fontSize: 10 },
                    labelBgStyle: { fill: "var(--canvas)" },
                    labelBgPadding: [5, 3],
                })),
            );
            setLayingOut(false);
        };
        run().catch(() => {
            if (!cancelled) {
                setNodes(
                    scene.vertices.map((v, i) => ({
                        id: v.id,
                        type: graph ? "dot" : "card",
                        position: {
                            x: (i % 5) * 320,
                            y: Math.floor(i / 5) * 170,
                        },
                        data: v,
                    })),
                );
                setLayingOut(false);
            }
        });
        return () => {
            cancelled = true;
        };
    }, [scene, graph, layoutTick, fitKey]);
    useEffect(() => {
        setNodes((ns) =>
            ns.map((n) => ({ ...n, selected: n.data.elementId === selected })),
        );
    }, [selected, setNodes]);
    useEffect(() => {
        const key = fitKey + graph + stateView + layoutTick;
        if (
            !layingOut &&
            flowInstance &&
            nodes.length &&
            fitted.current !== key
        ) {
            const timer = setTimeout(() => {
                const initialNodes = graph
                    ? nodes
                    : nodes.slice(
                          0,
                          scene.vertices.some((n) => n.kind === "group")
                              ? 3
                              : 6,
                      );
                const options = {
                    nodes: initialNodes,
                    padding: 0.18,
                    maxZoom: 1,
                    duration: 0,
                };
                const stored = localStorage.getItem("vault-viewport:v2:" + key);
                if (stored && layoutTick === 0) {
                    try {
                        const v = JSON.parse(stored);
                        if ([v.x, v.y, v.zoom].every(Number.isFinite))
                            flowInstance.setViewport(v);
                        else flowInstance.fitView(options);
                    } catch {
                        flowInstance.fitView(options);
                    }
                } else flowInstance.fitView(options);
                fitted.current = key;
            }, 80);
            return () => clearTimeout(timer);
        }
    }, [fitKey, layingOut, flowInstance, graph, stateView, layoutTick]);
    const connect = (c: Connection) => {
        if (proposal && c.source && c.target)
            onEdit({
                kind: "connect",
                source: c.source,
                target: c.target,
                label: "proposed connection",
            });
    };
    return (
        <div className={"canvas " + (graph ? "relationship-canvas" : "")}>
            <ReactFlow
                key={fitKey + graph + stateView}
                nodes={nodes}
                edges={edges}
                nodeTypes={nodeTypes}
                onInit={setFlowInstance}
                onMoveEnd={(_, v) =>
                    localStorage.setItem(
                        "vault-viewport:v2:" +
                            fitKey +
                            graph +
                            stateView +
                            layoutTick,
                        JSON.stringify(v),
                    )
                }
                onNodesChange={onNodesChange}
                onEdgesChange={onEdgesChange}
                onNodeClick={(_, node) =>
                    onSelect(node.data.elementId ?? node.id)
                }
                onNodeDoubleClick={(_, node) =>
                    onSelect(node.data.elementId ?? node.id)
                }
                onNodeDragStop={(_, node) =>
                    onLayout({ ...layout.positions, [node.id]: node.position })
                }
                onConnect={connect}
                onEdgesDelete={(es) => {
                    if (proposal)
                        es.forEach((e) =>
                            onEdit({
                                kind: "disconnect",
                                source: e.source,
                                target: e.target,
                            }),
                        );
                }}
                onNodesDelete={(ns) => {
                    if (proposal)
                        ns.forEach((n) =>
                            onEdit({
                                kind: "remove-node",
                                elementId: n.data.elementId ?? n.id,
                            }),
                        );
                }}
                nodesConnectable={!!proposal}
                edgesReconnectable={!!proposal}
                onReconnect={(old, c) => {
                    if (proposal && c.source && c.target) {
                        onEdit({
                            kind: "disconnect",
                            source: old.source,
                            target: old.target,
                        });
                        onEdit({
                            kind: "connect",
                            source: c.source,
                            target: c.target,
                        });
                    }
                }}
                deleteKeyCode={proposal ? "Backspace" : null}
                fitView
                fitViewOptions={{ padding: 0.2, maxZoom: 1 }}
                minZoom={0.06}
                maxZoom={2}
                colorMode="dark"
                onlyRenderVisibleElements
            >
                <Background gap={24} size={1} color="var(--grid)" />
                <Controls showInteractive={false} />
                <MiniMap
                    nodeColor={(n) =>
                        n.data.kind === "service" ? "#7d9dbc" : "#8274bc"
                    }
                    maskColor="var(--minimap-mask)"
                    pannable
                    zoomable
                />
            </ReactFlow>
            <button
                className="canvas-layout"
                onClick={() => setLayoutTick((t) => t + 1)}
            >
                <WorkflowIcon size={14} /> Auto arrange
            </button>
            {layingOut && (
                <span className="layout-progress">Arranging connections…</span>
            )}
            {!scene.vertices.length && (
                <div className="canvas-empty">
                    <GitBranch size={36} />
                    <h3>
                        {stateView
                            ? "No explicit state assignments"
                            : "No elements in this view"}
                    </h3>
                    <p>
                        {stateView
                            ? "Choose call flow to explore this workflow."
                            : "Try another layer or adjust your filters."}
                    </p>
                </div>
            )}
            <div className="canvas-legend">
                <span className="legend-dot" /> Extracted{" "}
                <span className="legend-dot interpreted" /> Interpreted{" "}
                <span className="legend-dot proposed" /> Proposed
            </div>
        </div>
    );
}

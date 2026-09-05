export type Evidence = { path: string; line: number; hash: string };
export type Element = {
    id: string;
    name: string;
    kind: string;
    layer: string;
    group: string;
    summary: string;
    status: string;
    signature?: string;
    route?: string;
    verb?: string;
    async: boolean;
    entryPoint: boolean;
    inputs: { name: string; type: string }[];
    output?: string;
    checks: string[];
    concepts: string[];
    concerns: { category: string; certainty: string; reason: string }[];
    evidence: Evidence[];
};
export type Relation = {
    id: string;
    source: string;
    target: string;
    kind: string;
    status: string;
    order?: number;
};
export type Step = {
    id: string;
    label: string;
    kind: string;
    elementId?: string;
    evidence?: Evidence;
};
export type Workflow = {
    id: string;
    entryId: string;
    name: string;
    coverage: string;
    gaps: string[];
    members: string[];
    steps: Step[];
    edges: { source: string; target: string; label: string }[];
    states: Step[];
    transitions: { source: string; target: string; label: string }[];
};
export type Snapshot = {
    id: string;
    project: string;
    branch: string;
    revision: string;
    worktree: string;
    createdAt: string;
    files: Record<string, string>;
    elements: Element[];
    relations: Relation[];
    workflows: Workflow[];
    diagnostics: string[];
};
export type Freshness = {
    fresh: boolean;
    status: string;
    fingerprint: string;
    branch: string;
    revision: string;
    changedFiles: number;
    error?: string;
};
export type Layout = {
    positions: Record<string, { x: number; y: number }>;
    bookmarks: string[];
};
export type Edit = {
    kind: "add-node" | "remove-node" | "connect" | "disconnect";
    source?: string;
    target?: string;
    elementId?: string;
    label?: string;
};
export type Proposal = {
    id: string;
    title: string;
    baseSnapshot: string;
    affectedIds: string[];
    narrative: string;
    edits: Edit[];
    criteria: { text: string; verified: boolean; evidence: string }[];
    status: string;
    resultSnapshot?: string;
    deviations: string;
    version: number;
};
export async function api<T>(
    path: string,
    method = "GET",
    body?: unknown,
): Promise<T> {
    const response = await fetch("/api/" + path, {
        method,
        headers: {
            "Content-Type": "application/json",
            "X-Project-Vault": "local",
        },
        body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) {
        const error = await response
            .json()
            .catch(() => ({ detail: "Request failed" }));
        throw new Error(error.detail ?? error.title ?? "Request failed");
    }
    return response.status === 204 ? (undefined as T) : response.json();
}

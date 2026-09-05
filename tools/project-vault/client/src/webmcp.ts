// Optional imperative WebMCP surface. The regular UI works without this API.
type Tool = {
    name: string;
    description: string;
    inputSchema: object;
    execute: (input: unknown) => unknown | Promise<unknown>;
    annotations?: { readOnlyHint?: boolean; untrustedContentHint?: boolean };
};
type Context = {
    registerTool: (
        tool: Tool,
        options: { signal: AbortSignal },
    ) => void | Promise<void>;
};
export function registerVaultTools(actions: {
    focus: (id: string) => void;
    refresh: () => Promise<void>;
    search: (query: string) => unknown;
}) {
    const context = (document as Document & { modelContext?: Context })
        .modelContext;
    if (!context) return () => {};
    const lifetime = new AbortController();
    const text = (input: unknown, key: string) => {
        if (
            typeof input !== "object" ||
            input === null ||
            typeof (input as Record<string, unknown>)[key] !== "string"
        )
            throw new Error("Expected " + key + " string");
        return (input as Record<string, string>)[key];
    };
    const tools: Tool[] = [
        {
            name: "project_vault_search",
            description:
                "Search project elements by name and return identities for navigation.",
            inputSchema: {
                type: "object",
                properties: { query: { type: "string" } },
                required: ["query"],
                additionalProperties: false,
            },
            annotations: { readOnlyHint: true, untrustedContentHint: true },
            execute: (input) => actions.search(text(input, "query")),
        },
        {
            name: "project_vault_focus",
            description:
                "Open an element in the visible project vault details panel.",
            inputSchema: {
                type: "object",
                properties: { id: { type: "string" } },
                required: ["id"],
                additionalProperties: false,
            },
            execute: (input) => {
                actions.focus(text(input, "id"));
                return { focused: true };
            },
        },
        {
            name: "project_vault_refresh",
            description:
                "Refresh the source-backed project map after an implementation step. Updates the visible workspace.",
            inputSchema: {
                type: "object",
                properties: {},
                additionalProperties: false,
            },
            execute: async () => {
                await actions.refresh();
                return {
                    message:
                        "Refresh attempted. Check the visible synchronization status for success or failure.",
                };
            },
        },
    ];
    for (const tool of tools)
        Promise.resolve(
            context.registerTool(tool, { signal: lifetime.signal }),
        ).catch(() => {});
    return () => lifetime.abort();
}

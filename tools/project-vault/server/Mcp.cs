using System.Text.Json;
namespace ProjectVault;

public static class Mcp
{
    static readonly object[] Tools = [
        Tool("vault_status","Check optional static-index freshness. Use vault_document_status for authored documentation."),
        Tool("vault_refresh","Refresh optional static reference data. This does not author or review documentation."),
        Tool("vault_element","Read an element, relationships, annotations and workflows.",new{id=new{type="string"}}),
        Tool("vault_workflows","Search the complete workflow inventory. Return up to 100 entries; use offset for pagination.",new{query=new{type="string"},offset=new{type="integer"}}),
        Tool("vault_workflow","Read one complete workflow by its workflow ID.",new{id=new{type="string"}}),
        Tool("vault_search","Search element names and groups; returns at most 50 concise results.",new{query=new{type="string"}}),
        Tool("vault_proposals","Read saved user proposals."),
        Tool("vault_compare","Compare a proposal's base snapshot with the current map.",new{id=new{type="string"}}),
        Tool("vault_explain","Save an agent interpretation with current snapshot ID and source evidence.",new{annotation=new{type="object"}}),
        Tool("vault_update_proposal","Record proposal progress, criteria evidence, result snapshot and deviations.",new{proposal=new{type="object"}})
    ];
    static object Tool(string name, string description, object? properties = null) => new { name, description, inputSchema = new { type = "object", properties = properties ?? new { }, additionalProperties = false } };
    public static async Task Run(Vault vault, Documents docs, Declarations declarations)
    {
        string? line; while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            object? response = null; JsonElement request = default;
            try
            {
                request = JsonDocument.Parse(line).RootElement;
                if (!request.TryGetProperty("id", out var requestId)) continue;
                var method = request.GetProperty("method").GetString();
                object? result = method switch
                {
                    "initialize" => new { protocolVersion = "2024-11-05", capabilities = new { tools = new { } }, serverInfo = new { name = "project-vault", version = "0.1.0" } },
                    "ping" => new { },
                    "tools/list" => new { tools = DocumentTools.Definitions.Concat(Tools) },
                    "tools/call" => await Call(vault, docs, declarations, request.GetProperty("params")),
                    _ => null
                };
                response = result is null ? new { jsonrpc = "2.0", id = requestId, error = new { code = -32601, message = "Unknown method" } } : (object)new { jsonrpc = "2.0", id = requestId, result };
            }
            catch (Exception ex) { response = new { jsonrpc = "2.0", id = request.ValueKind == JsonValueKind.Object && request.TryGetProperty("id", out var id) ? (object)id : null, error = new { code = -32602, message = ex.Message } }; }
            Console.WriteLine(JsonSerializer.Serialize(response));
        }
    }
    static async Task<object> Call(Vault vault, Documents docs, Declarations declarations, JsonElement p)
    {
        try
        {
            var name = p.GetProperty("name").GetString(); var a = p.TryGetProperty("arguments", out var args) ? args : JsonSerializer.SerializeToElement(new { });
            var id = a.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            var authored = DocumentTools.Definitions.Any(t => JsonSerializer.SerializeToElement(t).GetProperty("name").GetString() == name);
            object? value = authored ? await DocumentTools.Call(docs, declarations, name!, a) : name switch
            {
                "vault_status" => await vault.Status(),
                "vault_refresh" => await Refresh(vault),
                "vault_element" => await vault.Element(id),
                "vault_workflows" => (await vault.Current())?.Workflows.Where(w => !a.TryGetProperty("query", out var q) || w.Name.Contains(q.GetString() ?? "", StringComparison.OrdinalIgnoreCase)).Skip(a.TryGetProperty("offset", out var o) ? Math.Max(0, o.GetInt32()) : 0).Take(100).Select(w=>new {w.Id,w.EntryId,w.Name,w.Coverage,memberCount=w.Members.Count,gapCount=w.Gaps.Count}),
                "vault_workflow" => (await vault.Current())?.Workflows.FirstOrDefault(w=>w.Id==id),
                "vault_search" => (await vault.Current())?.Elements.Where(e=>e.Kind!="external" && (e.Name+" "+e.Group).Contains(a.TryGetProperty("query",out var search)?search.GetString()??"":"",StringComparison.OrdinalIgnoreCase)).Take(50).Select(e=>new{e.Id,e.Name,e.Kind,e.Layer,e.Summary}),
                "vault_proposals" => await vault.Proposals(),
                "vault_compare" => await vault.Compare(id),
                "vault_explain" => await Explain(vault, a.GetProperty("annotation")),
                "vault_update_proposal" => await vault.SaveProposal(a.GetProperty("proposal").Deserialize<Proposal>(Format.Json)!),
                _ => throw new ArgumentException("Unknown tool")
            };
            return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(value, Format.Json) } } };
        }
        catch (Exception ex) { return new { isError = true, content = new[] { new { type = "text", text = ex.Message } } }; }
    }
    static async Task<object> Refresh(Vault vault) { var s = await vault.Refresh(); return new { s.Id, elements = s.Elements.Count, workflows = s.Workflows.Count, diagnostics = s.Diagnostics }; }
    static async Task<object> Explain(Vault vault, JsonElement value) { await vault.SaveInterpretation(value.Deserialize<Annotation>(Format.Json)!); return new { saved = true }; }
}

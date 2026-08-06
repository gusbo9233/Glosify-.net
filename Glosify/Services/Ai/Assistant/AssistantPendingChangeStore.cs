using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant;

/// <summary>
/// Durable home for changes proposed by tool calls that run outside an orchestrator turn,
/// which is every call made by an agent in Foundry.
/// </summary>
public interface IAssistantPendingChangeStore
{
    Task<int> AppendAsync(
        Guid conversationId,
        string userId,
        Guid? contextQuizId,
        PendingChange change,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PendingChange>> ListPendingAsync(
        Guid conversationId,
        string userId,
        CancellationToken cancellationToken = default);

    /// <summary>Ties a conversation's queued changes to the response that produced them.</summary>
    Task<int> AttachToMessageAsync(
        Guid conversationId,
        string userId,
        Guid messageId,
        CancellationToken cancellationToken = default);

    Task<int> SettleAsync(
        Guid messageId,
        string userId,
        string status,
        CancellationToken cancellationToken = default);
}

public sealed class AssistantPendingChangeStore : IAssistantPendingChangeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly GlosifyContext _context;

    public AssistantPendingChangeStore(GlosifyContext context)
    {
        _context = context;
    }

    public async Task<int> AppendAsync(
        Guid conversationId,
        string userId,
        Guid? contextQuizId,
        PendingChange change,
        CancellationToken cancellationToken = default)
    {
        // Tool calls within one response arrive sequentially, and Apply depends on the
        // order they were made in, so the sequence continues from what is already stored.
        var nextSequence = await _context.AssistantPendingChanges
            .Where(row => row.ConversationId == conversationId)
            .Select(row => (int?)row.Sequence)
            .MaxAsync(cancellationToken) is { } highest
            ? highest + 1
            : 0;

        _context.AssistantPendingChanges.Add(new AssistantPendingChange
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            UserId = userId,
            ContextQuizId = contextQuizId,
            Sequence = nextSequence,
            Kind = change.Kind,
            PayloadJson = JsonSerializer.Serialize(change.Payload, JsonOptions),
            Status = AssistantPendingChangeStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync(cancellationToken);
        return nextSequence;
    }

    public async Task<IReadOnlyList<PendingChange>> ListPendingAsync(
        Guid conversationId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var rows = await _context.AssistantPendingChanges
            .AsNoTracking()
            .Where(row => row.ConversationId == conversationId
                && row.UserId == userId
                && row.Status == AssistantPendingChangeStatus.Pending)
            .OrderBy(row => row.Sequence)
            .ToListAsync(cancellationToken);

        return rows.Select(Deserialize).OfType<PendingChange>().ToList();
    }

    public async Task<int> AttachToMessageAsync(
        Guid conversationId,
        string userId,
        Guid messageId,
        CancellationToken cancellationToken = default)
    {
        return await _context.AssistantPendingChanges
            .Where(row => row.ConversationId == conversationId
                && row.UserId == userId
                && row.MessageId == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.MessageId, messageId),
                cancellationToken);
    }

    public async Task<int> SettleAsync(
        Guid messageId,
        string userId,
        string status,
        CancellationToken cancellationToken = default)
    {
        return await _context.AssistantPendingChanges
            .Where(row => row.MessageId == messageId
                && row.UserId == userId
                && row.Status == AssistantPendingChangeStatus.Pending)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(row => row.Status, status),
                cancellationToken);
    }

    private static PendingChange? Deserialize(AssistantPendingChange row)
    {
        try
        {
            using var document = JsonDocument.Parse(row.PayloadJson);
            return new PendingChange(row.Kind, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            // A payload that cannot be read must not block the rest of the conversation
            // from applying; the change is simply dropped.
            return null;
        }
    }
}

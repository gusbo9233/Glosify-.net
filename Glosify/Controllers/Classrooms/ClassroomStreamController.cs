using Glosify.Extensions;
using Glosify.Services.Classrooms;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers.Classrooms;

/// <summary>
/// The stream tab: the announcement board, and the chat history the SignalR client pages
/// through.
/// </summary>
public sealed class ClassroomStreamController : ClassroomControllerBase
{
    private const string Tab = "stream";

    private readonly IClassroomConversation _conversation;

    public ClassroomStreamController(
        IClassroomConversation conversation,
        ILogger<ClassroomStreamController> logger)
        : base(logger)
    {
        _conversation = conversation;
    }

    [HttpGet]
    public async Task<IActionResult> ChatHistory(Guid id, DateTimeOffset? before, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        try
        {
            var messages = await _conversation.GetChatMessagesAsync(id, userId, before, take: 50, cancellationToken);
            if (!before.HasValue)
            {
                await _conversation.MarkChatReadAsync(id, userId, cancellationToken);
            }

            return Json(new
            {
                currentUserId = userId,
                messages = messages.Select(m => new
                {
                    id = m.Id,
                    userId = m.UserId,
                    authorName = m.AuthorName,
                    body = m.Body,
                    createdAt = m.CreatedAt
                })
            });
        }
        catch (ClassroomAccessDeniedException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public Task<IActionResult> PostAnnouncement(Guid id, string body, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, Tab, () => _conversation.PostAnnouncementAsync(id, User.GetUserId(), body ?? string.Empty, cancellationToken));

    [HttpPost]
    public Task<IActionResult> DeleteMessage(Guid id, Guid messageId, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, Tab, () => _conversation.DeleteMessageAsync(id, User.GetUserId(), messageId, cancellationToken));

    [HttpPost]
    public Task<IActionResult> Pin(Guid id, Guid messageId, bool pinned, CancellationToken cancellationToken)
        => RunAndReturnAsync(id, Tab, () => _conversation.SetPinnedAsync(id, User.GetUserId(), messageId, pinned, cancellationToken));
}

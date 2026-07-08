using System.Collections.Concurrent;
using Glosify.Services.Classrooms;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Glosify.Hubs;

[Authorize]
public class ClassroomChatHub : Hub
{
    private const int MaxMessagesPerWindow = 10;
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    // Hub messages bypass the HTTP rate limiter, so keep a small in-process
    // send throttle per user (single-instance deployment assumption).
    private static readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> RecentSends = new();
    private static long _lastSweepTicks = DateTimeOffset.UtcNow.UtcTicks;

    // Which classroom group each connection joined, so a removed member's open
    // connections can be evicted from the group immediately (same
    // single-instance assumption as the throttle).
    private static readonly ConcurrentDictionary<string, (string UserId, Guid ClassroomId)> Connections = new();

    // Who is currently in each classroom's video call, keyed by the hub
    // connection that reported it so a dropped tab leaves the roster on
    // disconnect (same single-instance assumption as above).
    private static readonly ConcurrentDictionary<string, (string UserId, Guid ClassroomId)> CallConnections = new();

    private readonly IClassroomService _classrooms;

    public ClassroomChatHub(IClassroomService classrooms)
    {
        _classrooms = classrooms;
    }

    public async Task JoinClassroom(Guid classroomId)
    {
        var userId = Context.User!.GetUserId();
        await _classrooms.RequireMemberAsync(classroomId, userId, Context.ConnectionAborted);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(classroomId), Context.ConnectionAborted);
        Connections[Context.ConnectionId] = (userId, classroomId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        Connections.TryRemove(Context.ConnectionId, out _);
        if (CallConnections.TryRemove(Context.ConnectionId, out var callInfo))
        {
            await BroadcastCallChangedAsync(Clients.Group(GroupName(callInfo.ClassroomId)), callInfo.ClassroomId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Number of distinct members currently reported in a classroom's video
    /// call. Zero means no call is in progress.
    /// </summary>
    public static int GetCallParticipantCount(Guid classroomId)
    {
        var users = new HashSet<string>();
        foreach (var info in CallConnections.Values)
        {
            if (info.ClassroomId == classroomId)
            {
                users.Add(info.UserId);
            }
        }
        return users.Count;
    }

    /// <summary>
    /// Reports that the caller has joined the classroom's video call. The
    /// roster entry is dropped automatically when the connection closes.
    /// </summary>
    public async Task JoinCall(Guid classroomId)
    {
        var userId = Context.User!.GetUserId();
        await _classrooms.RequireMemberAsync(classroomId, userId, Context.ConnectionAborted);
        CallConnections[Context.ConnectionId] = (userId, classroomId);
        await BroadcastCallChangedAsync(Clients.Group(GroupName(classroomId)), classroomId);
    }

    /// <summary>
    /// Reports that the caller has left the classroom's video call.
    /// </summary>
    public async Task LeaveCall()
    {
        if (CallConnections.TryRemove(Context.ConnectionId, out var info))
        {
            await BroadcastCallChangedAsync(Clients.Group(GroupName(info.ClassroomId)), info.ClassroomId);
        }
    }

    private static Task BroadcastCallChangedAsync(IClientProxy group, Guid classroomId, CancellationToken cancellationToken = default)
        => group.SendAsync("callChanged", new { participantCount = GetCallParticipantCount(classroomId) }, cancellationToken);

    /// <summary>
    /// Drops a user's live connections out of a classroom's chat group, e.g.
    /// after they are removed from the classroom.
    /// </summary>
    public static async Task EvictFromGroupAsync(IHubContext<ClassroomChatHub> hubContext, Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        foreach (var (connectionId, info) in Connections)
        {
            if (info.UserId == userId && info.ClassroomId == classroomId)
            {
                Connections.TryRemove(connectionId, out _);
                await hubContext.Groups.RemoveFromGroupAsync(connectionId, GroupName(classroomId), cancellationToken);
            }
        }

        var leftCall = false;
        foreach (var (connectionId, info) in CallConnections)
        {
            if (info.UserId == userId && info.ClassroomId == classroomId)
            {
                leftCall |= CallConnections.TryRemove(connectionId, out _);
            }
        }
        if (leftCall)
        {
            await BroadcastCallChangedAsync(hubContext.Clients.Group(GroupName(classroomId)), classroomId, cancellationToken);
        }
    }

    public async Task SendMessage(Guid classroomId, string body)
    {
        var userId = Context.User!.GetUserId();

        if (!TryRecordSend(userId))
        {
            throw new HubException("You're sending messages too quickly. Wait a moment.");
        }

        ClassroomChatMessage message;
        try
        {
            message = await _classrooms.PostChatMessageAsync(classroomId, userId, body ?? string.Empty, Context.ConnectionAborted);
        }
        catch (Exception ex) when (ex is ClassroomAccessDeniedException or ArgumentException)
        {
            throw new HubException(ex.Message);
        }

        // The message is persisted at this point, so deliver it to the group
        // even if the sender's own connection drops mid-broadcast.
        await Clients.Group(GroupName(classroomId)).SendAsync("messageReceived", new
        {
            id = message.Id,
            userId = message.UserId,
            authorName = message.AuthorName,
            body = message.Body,
            createdAt = message.CreatedAt
        });
    }

    private static string GroupName(Guid classroomId) => $"classroom:{classroomId}";

    private static bool TryRecordSend(string userId)
    {
        var now = DateTimeOffset.UtcNow;
        SweepStaleThrottles(now);
        var window = RecentSends.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());

        lock (window)
        {
            while (window.Count > 0 && now - window.Peek() > ThrottleWindow)
            {
                window.Dequeue();
            }

            if (window.Count >= MaxMessagesPerWindow)
            {
                return false;
            }

            window.Enqueue(now);
            return true;
        }
    }

    private static void SweepStaleThrottles(DateTimeOffset now)
    {
        var lastSweep = Interlocked.Read(ref _lastSweepTicks);
        if (now.UtcTicks - lastSweep < SweepInterval.Ticks
            || Interlocked.CompareExchange(ref _lastSweepTicks, now.UtcTicks, lastSweep) != lastSweep)
        {
            return;
        }

        foreach (var (userId, window) in RecentSends)
        {
            lock (window)
            {
                while (window.Count > 0 && now - window.Peek() > ThrottleWindow)
                {
                    window.Dequeue();
                }

                if (window.Count == 0)
                {
                    // A concurrent sender may re-add the user right after this;
                    // at worst one send slips past the throttle window.
                    RecentSends.TryRemove(userId, out _);
                }
            }
        }
    }
}

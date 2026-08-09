using Glosify.Services.Classrooms;

namespace Glosify.Hubs;

/// <summary>
/// Reads call presence from <see cref="ClassroomChatHub"/>'s in-process connection map.
/// </summary>
/// <remarks>
/// Single-instance only, like the hub itself: with a second App Service instance the two
/// maps would not see each other. Adding a SignalR backplane is what fixes that, and this
/// adapter is where the replacement would go.
/// </remarks>
public sealed class HubClassroomCallPresence : IClassroomCallPresence
{
    public int GetParticipantCount(Guid classroomId) =>
        ClassroomChatHub.GetCallParticipantCount(classroomId);
}

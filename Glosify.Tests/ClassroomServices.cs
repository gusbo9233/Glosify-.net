using Glosify.Data;
using Glosify.Services.Classrooms;

namespace Glosify.Tests;

/// <summary>
/// The seven classroom services wired over one context, the way DI wires them.
/// </summary>
/// <remarks>
/// The classroom code used to be a single 41-method IClassroomService, and these tests
/// called it as one object. Keeping them calling one object — with the capability named at
/// the call site — is what makes the split verifiable: every assertion in
/// <see cref="ClassroomServiceTests"/> is untouched, so a passing run means the behaviour
/// moved rather than changed.
/// </remarks>
internal sealed class ClassroomServices
{
    public ClassroomServices(GlosifyContext context)
    {
        var queries = new ClassroomQueries(context);
        Access = new ClassroomAccess(context);
        Directory = new ClassroomDirectory(context, Access, queries);
        Roster = new ClassroomRoster(context, Access, queries);
        Library = new ClassroomLibrary(context, Access, queries);
        Conversation = new ClassroomConversation(context, Access, queries);
        Planner = new ClassroomPlanner(context, Access, queries);
        Results = new ClassroomResults(Access, queries);
    }

    public IClassroomAccess Access { get; }
    public IClassroomDirectory Directory { get; }
    public IClassroomRoster Roster { get; }
    public IClassroomLibrary Library { get; }
    public IClassroomConversation Conversation { get; }
    public IClassroomPlanner Planner { get; }
    public IClassroomResults Results { get; }

    public IClassroomCall Call(IClassroomCallPresence presence) =>
        new ClassroomCall(Access, Directory, presence);
}

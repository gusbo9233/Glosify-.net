using Glosify.Services.Classrooms;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// The classroom actions moved from one controller to six. The URLs must not have moved
/// with them: they are what browsers have bookmarked, what every form posts to, and what
/// the two hard-coded fetches in classroom-chat.js and classroom-call.js call.
/// </summary>
public sealed class ClassroomRoutingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ClassroomRoutingTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Every_classroom_url_is_where_it_was_before_the_controller_split()
    {
        // Exactly the actions the single ClassroomController used to expose, at exactly
        // the paths the default {controller}/{action}/{id?} route gave them.
        string[] expected =
        [
            "Classroom",
            "Classroom/AcceptInvitation/{id?}",
            "Classroom/Call/{id?}",
            "Classroom/CallToken/{id?}",
            "Classroom/ChangeRole/{id?}",
            "Classroom/ChatHistory/{id?}",
            "Classroom/ContentPdf/{id?}",
            "Classroom/CopyQuiz/{id?}",
            "Classroom/Create/{id?}",
            "Classroom/CreateAssignment/{id?}",
            "Classroom/CreateLesson/{id?}",
            "Classroom/DeclineInvitation/{id?}",
            "Classroom/Delete/{id?}",
            "Classroom/DeleteAssignment/{id?}",
            "Classroom/DeleteLesson/{id?}",
            "Classroom/DeleteMessage/{id?}",
            "Classroom/Details/{id?}",
            "Classroom/Index",
            "Classroom/Invite/{id?}",
            "Classroom/Join/{id?}",
            "Classroom/Leave/{id?}",
            "Classroom/MemberResults/{id?}",
            "Classroom/Pin/{id?}",
            "Classroom/PostAnnouncement/{id?}",
            "Classroom/RegenerateJoinCode/{id?}",
            "Classroom/RemoveMember/{id?}",
            "Classroom/ShareBook/{id?}",
            "Classroom/ShareQuiz/{id?}",
            "Classroom/ToggleJoinCode/{id?}",
            "Classroom/Unshare/{id?}",
        ];

        var patterns = _factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .Where(pattern => pattern.StartsWith("Classroom", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(pattern => pattern, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, patterns);
    }

    /// <summary>
    /// The endpoint-pattern assertion above only describes the new attribute routes. This
    /// one drives the URLs for real: anonymous requests must reach a matched, authorized
    /// endpoint and be redirected to sign in, never 404. It passes unchanged against the
    /// pre-split single controller, which is what makes it a preservation check rather
    /// than a restatement of the new routing.
    /// </summary>
    [Theory]
    [InlineData("/Classroom")]
    [InlineData("/Classroom/Index")]
    [InlineData("/Classroom/Details/9f3c1c0e-0000-4000-8000-000000000001")]
    [InlineData("/Classroom/Details/9f3c1c0e-0000-4000-8000-000000000001?tab=members")]
    [InlineData("/Classroom/Call/9f3c1c0e-0000-4000-8000-000000000001")]
    [InlineData("/Classroom/MemberResults/9f3c1c0e-0000-4000-8000-000000000001?memberId=x")]
    [InlineData("/Classroom/ChatHistory?id=9f3c1c0e-0000-4000-8000-000000000001")]
    [InlineData("/Classroom/ContentPdf/9f3c1c0e-0000-4000-8000-000000000001?bookId=9f3c1c0e-0000-4000-8000-000000000002")]
    public async Task Classroom_get_urls_still_match_an_endpoint(string url)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.GetAsync(url);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location ?? throw new InvalidOperationException("No redirect location.");
        Assert.Equal("/login", location.AbsolutePath);
        // The sign-in round trip returns to the exact URL that was asked for.
        Assert.Equal(url, Uri.UnescapeDataString(location.Query["?ReturnUrl=".Length..]));
    }

    [Fact]
    public async Task Only_a_teacher_may_open_a_call_nobody_is_in()
    {
        var classroomId = Guid.NewGuid();

        var studentJoiningEmptyCall = new ClassroomCall(
            new StubAccess(ClassroomRole.Student),
            new StubDirectory(classroomId),
            new StubPresence(0));
        await Assert.ThrowsAsync<ClassroomCallNotStartedException>(
            () => studentJoiningEmptyCall.RequireJoinableAsync(classroomId, "student-1"));

        var studentJoiningLiveCall = new ClassroomCall(
            new StubAccess(ClassroomRole.Student),
            new StubDirectory(classroomId),
            new StubPresence(1));
        Assert.Equal(
            1,
            (await studentJoiningLiveCall.RequireJoinableAsync(classroomId, "student-1")).ActiveParticipants);

        var teacherStartingCall = new ClassroomCall(
            new StubAccess(ClassroomRole.Teacher),
            new StubDirectory(classroomId),
            new StubPresence(0));
        Assert.True((await teacherStartingCall.RequireJoinableAsync(classroomId, "teacher-1")).IsTeacher);
    }

    private sealed class StubPresence(int count) : IClassroomCallPresence
    {
        public int GetParticipantCount(Guid classroomId) => count;
    }

    private sealed class StubAccess(ClassroomRole role) : IClassroomAccess
    {
        public Task<ClassroomMembership> RequireMemberAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClassroomMembership { ClassroomId = classroomId, UserId = userId, Role = role });

        public Task<ClassroomMembership> RequireTeacherAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ClassroomMembership> RequireOwnerAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> IsMemberAsync(Guid classroomId, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class StubDirectory(Guid classroomId) : IClassroomDirectory
    {
        public Task<ClassroomHeader> GetDetailsAsync(Guid id, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClassroomHeader(classroomId, "Spanish 101", null, "Spanish", "ABCD2345", true));

        public Task<Guid> GetGroupCallIdAsync(Guid id, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Guid.NewGuid());

        public Task<ClassroomHeader> CreateAsync(string ownerUserId, string name, string? description, string? language = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ClassroomSummary>> GetForUserAsync(string userId, string? language = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClassroomDetailsPage> GetDetailsPageAsync(Guid id, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteClassroomAsync(Guid id, string ownerUserId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ClassroomHeader?> JoinByCodeAsync(string userId, string code, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> RegenerateJoinCodeAsync(Guid id, string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetJoinCodeEnabledAsync(Guid id, string userId, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

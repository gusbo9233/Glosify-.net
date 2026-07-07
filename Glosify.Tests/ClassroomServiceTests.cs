using Glosify.Data;
using Glosify.Services.Classrooms;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class ClassroomServiceTests
{
    private const string OwnerId = "owner-1";
    private const string StudentId = "student-1";
    private const string OutsiderId = "outsider-1";

    [Fact]
    public async Task CreateAsync_AddsOwnerMembershipAndJoinCode()
    {
        await using var context = CreateContext();
        var service = new ClassroomService(context);

        var classroom = await service.CreateAsync(OwnerId, "Spanish 101", "Intro course");

        Assert.Equal(8, classroom.JoinCode.Length);
        Assert.True(classroom.JoinCodeEnabled);
        Assert.NotEqual(Guid.Empty, classroom.GroupCallId);

        var membership = Assert.Single(context.ClassroomMemberships.ToList());
        Assert.Equal(OwnerId, membership.UserId);
        Assert.Equal(ClassroomRole.Owner, membership.Role);
    }

    [Fact]
    public async Task JoinByCodeAsync_AddsStudentAndIsIdempotent()
    {
        await using var context = CreateContext();
        var service = new ClassroomService(context);
        var classroom = await service.CreateAsync(OwnerId, "Spanish 101", null);

        var joined = await service.JoinByCodeAsync(StudentId, classroom.JoinCode.ToLowerInvariant());
        var joinedAgain = await service.JoinByCodeAsync(StudentId, classroom.JoinCode);

        Assert.NotNull(joined);
        Assert.NotNull(joinedAgain);
        var membership = Assert.Single(context.ClassroomMemberships.Where(m => m.UserId == StudentId).ToList());
        Assert.Equal(ClassroomRole.Student, membership.Role);
    }

    [Fact]
    public async Task JoinByCodeAsync_RespectsDisabledCode()
    {
        await using var context = CreateContext();
        var service = new ClassroomService(context);
        var classroom = await service.CreateAsync(OwnerId, "Spanish 101", null);
        await service.SetJoinCodeEnabledAsync(classroom.Id, OwnerId, enabled: false);

        var joined = await service.JoinByCodeAsync(StudentId, classroom.JoinCode);

        Assert.Null(joined);
    }

    [Fact]
    public async Task RequireTeacherAsync_RejectsStudentsAndOutsiders()
    {
        await using var context = CreateContext();
        var service = new ClassroomService(context);
        var classroom = await service.CreateAsync(OwnerId, "Spanish 101", null);
        await service.JoinByCodeAsync(StudentId, classroom.JoinCode);

        await Assert.ThrowsAsync<ClassroomAccessDeniedException>(
            () => service.RequireTeacherAsync(classroom.Id, StudentId));
        await Assert.ThrowsAsync<ClassroomAccessDeniedException>(
            () => service.RequireMemberAsync(classroom.Id, OutsiderId));
        Assert.Equal(ClassroomRole.Owner, (await service.RequireTeacherAsync(classroom.Id, OwnerId)).Role);
    }

    [Fact]
    public async Task PostAnnouncementAsync_RejectsStudents()
    {
        await using var context = CreateContext();
        AddUser(context, OwnerId, "owner@example.test");
        await context.SaveChangesAsync();
        var service = new ClassroomService(context);
        var classroom = await service.CreateAsync(OwnerId, "Spanish 101", null);
        await service.JoinByCodeAsync(StudentId, classroom.JoinCode);

        await Assert.ThrowsAsync<ClassroomAccessDeniedException>(
            () => service.PostAnnouncementAsync(classroom.Id, StudentId, "Hi"));

        await service.PostAnnouncementAsync(classroom.Id, OwnerId, "Welcome!");
        var board = await service.GetBoardAsync(classroom.Id, StudentId);
        Assert.Single(board);
        Assert.Equal("Welcome!", board[0].Body);
    }

    [Fact]
    public async Task InviteFlow_MatchesEmailAndGrantsRole()
    {
        await using var context = CreateContext();
        AddUser(context, OwnerId, "owner@example.test");
        AddUser(context, StudentId, "student@example.test");
        await context.SaveChangesAsync();
        var service = new ClassroomService(context);
        var classroom = await service.CreateAsync(OwnerId, "Spanish 101", null);

        await service.InviteByEmailAsync(classroom.Id, OwnerId, "Student@Example.test", ClassroomRole.Teacher);

        var pending = await service.GetPendingInvitationsForUserAsync(StudentId);
        var invitation = Assert.Single(pending);
        Assert.Equal(classroom.Id, invitation.ClassroomId);
        Assert.Equal(ClassroomRole.Teacher, invitation.Role);

        var accepted = await service.AcceptInvitationAsync(invitation.Id, StudentId);

        Assert.NotNull(accepted);
        var membership = Assert.Single(context.ClassroomMemberships.Where(m => m.UserId == StudentId).ToList());
        Assert.Equal(ClassroomRole.Teacher, membership.Role);
        Assert.Empty(await service.GetPendingInvitationsForUserAsync(StudentId));
    }

    [Fact]
    public async Task AcceptInvitationAsync_RejectsWrongUser()
    {
        await using var context = CreateContext();
        AddUser(context, StudentId, "student@example.test");
        AddUser(context, OutsiderId, "outsider@example.test");
        await context.SaveChangesAsync();
        var service = new ClassroomService(context);
        var classroom = await service.CreateAsync(OwnerId, "Spanish 101", null);
        await service.InviteByEmailAsync(classroom.Id, OwnerId, "student@example.test", ClassroomRole.Student);
        var invitationId = context.ClassroomInvitations.Single().Id;

        var accepted = await service.AcceptInvitationAsync(invitationId, OutsiderId);

        Assert.Null(accepted);
        Assert.Empty(context.ClassroomMemberships.Where(m => m.UserId == OutsiderId).ToList());
    }

    [Fact]
    public async Task ShareQuizAsync_RequiresOwnershipOfQuiz()
    {
        await using var context = CreateContext();
        var service = new ClassroomService(context);
        var classroom = await service.CreateAsync(OwnerId, "Spanish 101", null);
        var foreignQuiz = AddQuiz(context, OutsiderId);
        var ownQuiz = AddQuiz(context, OwnerId);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<ClassroomAccessDeniedException>(
            () => service.ShareQuizAsync(classroom.Id, OwnerId, foreignQuiz.Id));

        await service.ShareQuizAsync(classroom.Id, OwnerId, ownQuiz.Id);
        await service.ShareQuizAsync(classroom.Id, OwnerId, ownQuiz.Id); // idempotent

        var link = Assert.Single(context.ClassroomContents.ToList());
        Assert.Equal(ownQuiz.Id, link.QuizId);
    }

    [Fact]
    public async Task RemoveMemberAsync_ProtectsOwner()
    {
        await using var context = CreateContext();
        var service = new ClassroomService(context);
        var classroom = await service.CreateAsync(OwnerId, "Spanish 101", null);
        await service.JoinByCodeAsync(StudentId, classroom.JoinCode);

        await Assert.ThrowsAsync<ClassroomAccessDeniedException>(
            () => service.RemoveMemberAsync(classroom.Id, StudentId, OwnerId));

        await service.RemoveMemberAsync(classroom.Id, OwnerId, StudentId);
        Assert.Empty(context.ClassroomMemberships.Where(m => m.UserId == StudentId).ToList());
    }

    [Fact]
    public async Task DeleteClassroomAsync_DetachesAttemptsAndRequiresOwner()
    {
        await using var context = CreateContext();
        var service = new ClassroomService(context);
        var classroom = await service.CreateAsync(OwnerId, "Spanish 101", null);
        await service.JoinByCodeAsync(StudentId, classroom.JoinCode);
        var quiz = AddQuiz(context, OwnerId);
        context.QuizAttempts.Add(new QuizAttempt
        {
            Id = Guid.NewGuid(),
            QuizId = quiz.Id,
            UserId = StudentId,
            ClassroomId = classroom.Id,
            Mode = "typing",
            TotalItems = 5,
            CorrectCount = 4,
            IncorrectCount = 1,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<ClassroomAccessDeniedException>(
            () => service.DeleteClassroomAsync(classroom.Id, StudentId));

        await service.DeleteClassroomAsync(classroom.Id, OwnerId);

        Assert.Empty(context.Classrooms.ToList());
        var attempt = Assert.Single(context.QuizAttempts.ToList());
        Assert.Null(attempt.ClassroomId);
    }

    [Fact]
    public async Task GetClassroomResultsAsync_TeacherOnlyAndScopedToClassroom()
    {
        await using var context = CreateContext();
        AddUser(context, StudentId, "student@example.test");
        await context.SaveChangesAsync();
        var service = new ClassroomService(context);
        var classroom = await service.CreateAsync(OwnerId, "Spanish 101", null);
        await service.JoinByCodeAsync(StudentId, classroom.JoinCode);
        var quiz = AddQuiz(context, OwnerId);
        context.QuizAttempts.AddRange(
            new QuizAttempt
            {
                Id = Guid.NewGuid(),
                QuizId = quiz.Id,
                UserId = StudentId,
                ClassroomId = classroom.Id,
                Mode = "flashcards",
                TotalItems = 10,
                CorrectCount = 8,
                IncorrectCount = 2,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            },
            new QuizAttempt
            {
                Id = Guid.NewGuid(),
                QuizId = quiz.Id,
                UserId = StudentId,
                ClassroomId = null, // personal practice stays private
                Mode = "flashcards",
                TotalItems = 10,
                CorrectCount = 5,
                IncorrectCount = 5,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow
            });
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<ClassroomAccessDeniedException>(
            () => service.GetClassroomResultsAsync(classroom.Id, StudentId));

        var results = await service.GetClassroomResultsAsync(classroom.Id, OwnerId);
        var row = Assert.Single(results);
        Assert.Equal(8, row.CorrectCount);

        // A student can read their own classroom results but not another member's.
        var own = await service.GetMemberResultsAsync(classroom.Id, StudentId, StudentId);
        Assert.Single(own);
        await Assert.ThrowsAsync<ClassroomAccessDeniedException>(
            () => service.GetMemberResultsAsync(classroom.Id, StudentId, OwnerId));
    }

    private static void AddUser(GlosifyContext context, string id, string email)
    {
        context.Users.Add(new ApplicationUser
        {
            Id = id,
            Email = email,
            UserName = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant()
        });
    }

    private static Quiz AddQuiz(GlosifyContext context, string userId)
    {
        var quiz = new Quiz
        {
            Id = Guid.NewGuid(),
            Name = "Verbs",
            UserId = userId,
            SourceLanguage = "English",
            TargetLanguage = "Spanish",
            Language = "Spanish",
            ProcessingStatus = "Ready",
            CreatedAt = DateTimeOffset.UtcNow
        };
        context.Quizzes.Add(quiz);
        return quiz;
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }
}

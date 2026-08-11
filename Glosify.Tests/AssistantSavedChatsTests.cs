using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Ai.Llm;
using Glosify.Services.Books;
using Glosify.Services.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public class AssistantSavedChatsTests
{
    [Fact]
    public async Task CreateChat_StoresGlobalThreadWithContext()
    {
        await using var context = CreateContext();
        var quizId = Guid.NewGuid();
        context.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context);

        var chat = await orchestrator.CreateChatAsync("user-1", quizId);

        var thread = await context.AssistantThreads.SingleAsync(t => t.Id == chat.Id);
        Assert.Null(thread.QuizId);
        Assert.Equal(quizId, thread.ContextQuizId);
        Assert.Equal("New chat", chat.Title);
    }

    [Fact]
    public async Task GetGlobalHistory_PersistsItsNewDefaultThread()
    {
        await using var context = CreateContext();
        var orchestrator = CreateOrchestrator(context);

        var history = await orchestrator.GetGlobalHistoryAsync("user-1");
        context.ChangeTracker.Clear();

        Assert.True(await context.AssistantThreads.AnyAsync(thread => thread.Id == history.ThreadId));
    }

    [Fact]
    public async Task ListChats_ReturnsOnlyCurrentUsersGlobalChats()
    {
        await using var context = CreateContext();
        context.AssistantThreads.AddRange(
            CreateThread("user-1", title: "Mine"),
            CreateThread("user-2", title: "Other user"),
            CreateThread("user-1", title: "Legacy quiz thread", quizId: Guid.NewGuid()));
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context);

        var chats = await orchestrator.ListChatsAsync("user-1");

        var chat = Assert.Single(chats);
        Assert.Equal("Mine", chat.Title);
    }

    [Fact]
    public async Task CreateChat_StampsTheSelectedLanguage()
    {
        await using var context = CreateContext();
        var orchestrator = CreateOrchestrator(context, languageContext: new StaticLanguageContext("German"));

        var chat = await orchestrator.CreateChatAsync("user-1");

        var thread = await context.AssistantThreads.SingleAsync(t => t.Id == chat.Id);
        Assert.Equal("German", thread.Language);
    }

    [Fact]
    public async Task ListChats_ReturnsOnlyTheSelectedLanguagesChats()
    {
        await using var context = CreateContext();
        context.AssistantThreads.AddRange(
            CreateThread("user-1", title: "Polish chat"),
            CreateThread("user-1", title: "German chat", language: "German"));
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context, languageContext: new StaticLanguageContext("German"));

        var chats = await orchestrator.ListChatsAsync("user-1");

        var chat = Assert.Single(chats);
        Assert.Equal("German chat", chat.Title);
    }

    // Without a selected language there is nothing to scope to, so hiding every chat
    // would look like the history had been lost.
    [Fact]
    public async Task ListChats_ReturnsEveryChatWhenNoLanguageIsSelected()
    {
        await using var context = CreateContext();
        context.AssistantThreads.AddRange(
            CreateThread("user-1", title: "Polish chat"),
            CreateThread("user-1", title: "German chat", language: "German"));
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context, languageContext: new StaticLanguageContext(null));

        var chats = await orchestrator.ListChatsAsync("user-1");

        Assert.Equal(2, chats.Count);
    }

    // The panel keeps the open chat id across a language switch; the switch has to end
    // that conversation rather than let the next message land in the old language's chat.
    [Fact]
    public async Task SendChatMessage_RefusesAChatFromAnotherLanguage()
    {
        await using var context = CreateContext();
        var languageContext = new StaticLanguageContext("Polish");
        var orchestrator = CreateOrchestrator(context, languageContext: languageContext);
        var chat = await orchestrator.CreateChatAsync("user-1");

        languageContext.TrySetLanguage("German");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.SendChatMessageAsync(chat.Id, "user-1", "Continue where we left off"));
        Assert.Contains("another language", error.Message);
    }

    [Fact]
    public async Task SendGlobalMessage_StartsAFreshThreadAfterALanguageSwitch()
    {
        await using var context = CreateContext();
        var languageContext = new StaticLanguageContext("Polish");
        var orchestrator = CreateOrchestrator(context, languageContext: languageContext);
        var polish = await orchestrator.SendGlobalMessageAsync("user-1", "Explain the instrumental case");

        languageContext.TrySetLanguage("German");
        var german = await orchestrator.SendGlobalMessageAsync("user-1", "Explain the dative case");

        Assert.NotEqual(polish.ThreadId, german.ThreadId);
        Assert.Equal(
            "German",
            (await context.AssistantThreads.SingleAsync(t => t.Id == german.ThreadId)).Language);
        // The German turn starts clean: only its own question and answer.
        Assert.Equal(2, await context.AssistantMessages.CountAsync(m => m.ThreadId == german.ThreadId));
    }

    [Fact]
    public async Task SendChatMessage_AutoTitlesAndPersistsMessages()
    {
        await using var context = CreateContext();
        var quizId = Guid.NewGuid();
        context.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context, generativeAi: new StaticGenerativeAiClient("Queued those words."));
        var chat = await orchestrator.CreateChatAsync("user-1", quizId);

        var response = await orchestrator.SendChatMessageAsync(
            chat.Id,
            "user-1",
            "Create a Polish verbs quiz",
            contextQuizId: quizId);

        var thread = await context.AssistantThreads.SingleAsync(t => t.Id == chat.Id);
        Assert.Equal("Create a Polish verbs quiz", thread.Title);
        Assert.Equal(quizId, thread.ContextQuizId);
        Assert.Equal(chat.Id, response.ThreadId);
        Assert.Equal(2, await context.AssistantMessages.CountAsync(m => m.ThreadId == chat.Id));
    }

    [Fact]
    public async Task Failed_turn_does_not_persist_buffered_model_or_tool_history()
    {
        await using var context = CreateContext();
        var orchestrator = CreateOrchestrator(
            context,
            generativeAi: new ToolThenThrowingGenerativeAiClient(),
            tools: new SavingMutationAssistantTools(context));
        var chat = await orchestrator.CreateChatAsync("user-1");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            orchestrator.SendChatMessageAsync(chat.Id, "user-1", "Create a travel quiz."));

        context.ChangeTracker.Clear();
        var message = Assert.Single(await context.AssistantMessages
            .Where(candidate => candidate.ThreadId == chat.Id)
            .ToListAsync());
        Assert.Equal(AssistantMessageRole.User, message.Role);
        Assert.Null(message.PendingChangesJson);
        var turn = await context.AssistantTurns.SingleAsync();
        Assert.Equal(AssistantTurnStatus.Failed, turn.Status);
        Assert.Equal(message.TurnId, (Guid?)turn.Id);
        Assert.Equal(2, await context.AssistantModelInvocations.CountAsync());
        Assert.All(
            await context.AssistantModelInvocations.OrderBy(invocation => invocation.Sequence).ToListAsync(),
            invocation => Assert.Equal(turn.Id, invocation.TurnId));
        Assert.Equal(
            AssistantInvocationStatus.Failed,
            (await context.AssistantModelInvocations.SingleAsync(invocation => invocation.Sequence == 1)).Status);
        var toolExecution = await context.AssistantToolExecutions.SingleAsync();
        Assert.Equal(AssistantInvocationStatus.Completed, toolExecution.Status);
        Assert.Equal(1, toolExecution.ProposedChangeCount);
    }

    [Fact]
    public async Task Context_resolution_failure_still_persists_input_and_finalizes_turn()
    {
        await using var context = CreateContext();
        var quizId = Guid.NewGuid();
        context.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context);
        var chat = await orchestrator.CreateChatAsync("user-1", quizId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.SendChatMessageAsync(
                chat.Id,
                "user-1",
                "Build this custom quiz.",
                contextQuizId: quizId,
                customQuizId: Guid.NewGuid()));

        context.ChangeTracker.Clear();
        var message = Assert.Single(await context.AssistantMessages.ToListAsync());
        var turn = await context.AssistantTurns.SingleAsync();
        Assert.Equal(AssistantMessageRole.User, message.Role);
        Assert.Contains("Build this custom quiz.", message.ContentJson);
        Assert.Equal(turn.Id, message.TurnId);
        Assert.Equal(AssistantTurnStatus.Failed, turn.Status);
        Assert.Equal("unhandled_error", turn.ErrorCategory);
        Assert.NotNull(turn.CompletedAt);
        Assert.Empty(context.AssistantModelInvocations);
    }

    [Fact]
    public async Task Final_message_save_failure_detaches_pending_output_and_finalizes_turn_as_failed()
    {
        var interceptor = new FailFinalMessageSaveOnceInterceptor();
        await using var context = CreateContext(saveChangesInterceptor: interceptor);
        var orchestrator = CreateOrchestrator(context);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            orchestrator.SendGlobalMessageAsync("user-1", "Retain this input."));

        context.ChangeTracker.Clear();
        var turn = await context.AssistantTurns.SingleAsync();
        var message = Assert.Single(await context.AssistantMessages.ToListAsync());
        Assert.True(interceptor.FailedFinalMessageSave);
        Assert.Equal(AssistantTurnStatus.Failed, turn.Status);
        Assert.Equal("unhandled_error", turn.ErrorCategory);
        Assert.NotNull(turn.CompletedAt);
        Assert.Equal(AssistantMessageRole.User, message.Role);
        Assert.Contains("Retain this input.", message.ContentJson);
        Assert.Equal(turn.Id, message.TurnId);
    }

    [Fact]
    public async Task Cancelled_provider_call_retains_input_and_marks_turn_cancelled()
    {
        await using var context = CreateContext();
        var orchestrator = CreateOrchestrator(
            context,
            generativeAi: new CancellingGenerativeAiClient());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.SendGlobalMessageAsync("user-1", "Keep this even if I leave."));

        context.ChangeTracker.Clear();
        var message = Assert.Single(await context.AssistantMessages.ToListAsync());
        var turn = await context.AssistantTurns.SingleAsync();
        var invocation = await context.AssistantModelInvocations.SingleAsync();
        Assert.Equal(turn.Id, message.TurnId);
        Assert.Equal(AssistantTurnStatus.Cancelled, turn.Status);
        Assert.Equal("cancelled", turn.ErrorCategory);
        Assert.Equal(AssistantInvocationStatus.Cancelled, invocation.Status);
        Assert.NotNull(turn.CompletedAt);
        Assert.NotNull(invocation.CompletedAt);
    }

    [Fact]
    public async Task Multi_call_turn_uses_one_turn_id_and_distinct_invocation_ids()
    {
        await using var context = CreateContext();
        var generativeAi = new OneToolThenAnswerGenerativeAiClient();
        var orchestrator = CreateOrchestrator(
            context,
            generativeAi: generativeAi,
            tools: new LoopAssistantTools());

        var result = await orchestrator.SendGlobalMessageAsync("user-1", "Look this up and answer.");

        var turn = await context.AssistantTurns.SingleAsync();
        var invocations = await context.AssistantModelInvocations
            .OrderBy(invocation => invocation.Sequence)
            .ToListAsync();
        var execution = await context.AssistantToolExecutions.SingleAsync();
        Assert.Equal(result.TurnId, turn.Id);
        Assert.Equal(AssistantTurnStatus.Completed, turn.Status);
        Assert.Equal(result.AssistantMessageId, turn.FinalMessageId);
        Assert.Equal(2, invocations.Count);
        Assert.Equal(2, invocations.Select(invocation => invocation.Id).Distinct().Count());
        Assert.All(invocations, invocation => Assert.Equal(turn.Id, invocation.TurnId));
        Assert.Equal(invocations[0].Id, execution.InvocationId);
        Assert.Equal(
            invocations.Select(invocation => invocation.Id),
            generativeAi.UsageContexts.Select(usage => usage.OperationId));
        Assert.All(generativeAi.UsageContexts, usage => Assert.Equal(turn.Id, usage.AssistantTurnId));
        Assert.All(
            await context.AssistantMessages.Where(message => message.ThreadId == result.ThreadId).ToListAsync(),
            message => Assert.Equal(turn.Id, message.TurnId));
    }

    [Fact]
    public async Task Feedback_is_idempotent_owned_and_only_attached_to_the_final_message()
    {
        await using var context = CreateContext();
        var orchestrator = CreateOrchestrator(context);
        var result = await orchestrator.SendGlobalMessageAsync("user-1", "Help me.");

        await orchestrator.SaveFeedbackAsync(
            result.TurnId,
            "user-1",
            AssistantFeedbackRating.Up,
            ["helpful", "clear"],
            "Nice answer");
        var updatedFeedback = await orchestrator.SaveFeedbackAsync(
            result.TurnId,
            "user-1",
            AssistantFeedbackRating.Up,
            ["clear", "saved_time"],
            "Even better");
        await orchestrator.RecordClientDurationAsync(result.TurnId, "user-1", 1234.5);

        var feedback = await context.AssistantFeedback.Include(item => item.Reasons).SingleAsync();
        Assert.Equal("Even better", feedback.Comment);
        Assert.Equal(["clear", "saved_time"], feedback.Reasons.Select(reason => reason.ReasonCode).Order());
        Assert.Equal(["clear", "saved_time"], updatedFeedback.ReasonCodes);
        Assert.Equal(1234.5, (await context.AssistantTurns.SingleAsync()).ClientDurationMs);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            orchestrator.SaveFeedbackAsync(result.TurnId, "user-2", "up", [], null));

        var history = await orchestrator.GetGlobalHistoryAsync("user-1");
        var final = Assert.Single(history.Messages, message => message.CanRate);
        Assert.Equal(result.AssistantMessageId, final.Id);
        Assert.Equal("up", final.Feedback?.Rating);

        await orchestrator.DeleteFeedbackAsync(result.TurnId, "user-1");
        await orchestrator.DeleteFeedbackAsync(result.TurnId, "user-1");
        Assert.Empty(context.AssistantFeedback);
    }

    [Fact]
    public async Task SendGlobalMessage_IncludesBookPageContext()
    {
        await using var context = CreateContext();
        var documentId = Guid.NewGuid();
        var books = await SeedBookAsync(context, CreateBookPage(documentId, "user-1", "Pan Tadeusz opens with a longing for Lithuania."));
        var generativeAi = new CapturingGenerativeAiClient("Queued a quiz from the page.");
        var orchestrator = CreateOrchestrator(context, generativeAi: generativeAi, books: books);

        await orchestrator.SendGlobalMessageAsync(
            "user-1",
            "Make a quiz from this page",
            documentContext: new AssistantDocumentContext(documentId, 3));

        Assert.NotNull(generativeAi.LastAgentRequest);
        Assert.Contains("Current book page context", generativeAi.LastAgentRequest.SystemInstruction);
        Assert.Contains("Page: 3", generativeAi.LastAgentRequest.SystemInstruction);
        Assert.Contains("Pan Tadeusz opens with a longing for Lithuania.", generativeAi.LastAgentRequest.SystemInstruction);
    }

    [Fact]
    public async Task CreateChat_StoresTheSelectedBook()
    {
        await using var context = CreateContext();
        var documentId = Guid.NewGuid();
        var books = await SeedBookAsync(context, CreateBookPage(documentId, "user-1", "Rozdział pierwszy."));
        var orchestrator = CreateOrchestrator(context, books: books);

        var chat = await orchestrator.CreateChatAsync("user-1", contextBookDocumentId: documentId);

        var thread = await context.AssistantThreads.SingleAsync(t => t.Id == chat.Id);
        Assert.Equal(documentId, thread.ContextBookDocumentId);
        Assert.Equal(documentId, chat.ContextBookDocumentId);
        Assert.Equal("Polish Reader", chat.ContextBookTitle);
    }

    [Fact]
    public async Task CreateChat_RejectsAnotherUsersBook()
    {
        await using var context = CreateContext();
        var documentId = Guid.NewGuid();
        var books = await SeedBookAsync(context, CreateBookPage(documentId, "owner", "Rozdział pierwszy."));
        var orchestrator = CreateOrchestrator(context, books: books);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.CreateChatAsync("intruder", contextBookDocumentId: documentId));

        Assert.Equal("That book was not found.", error.Message);
    }

    // The picker sets the book once; later turns send no book id at all, so the chat has
    // to supply it or the assistant would forget the book after the first message.
    [Fact]
    public async Task SendChatMessage_FallsBackToTheThreadsBookContext()
    {
        await using var context = CreateContext();
        var documentId = Guid.NewGuid();
        var books = await SeedBookAsync(context, CreateBookPage(documentId, "user-1", "Rozdział pierwszy."));
        var generativeAi = new CapturingGenerativeAiClient("Read it.");
        var orchestrator = CreateOrchestrator(context, generativeAi: generativeAi, books: books);
        var chat = await orchestrator.CreateChatAsync("user-1", contextBookDocumentId: documentId);

        await orchestrator.SendChatMessageAsync(chat.Id, "user-1", "What is this book about?");

        Assert.NotNull(generativeAi.LastAgentRequest);
        Assert.Contains("Selected book context", generativeAi.LastAgentRequest.ContextInstruction);
        Assert.Contains("Polish Reader", generativeAi.LastAgentRequest.ContextInstruction);
    }

    // Authored agents receive only the context block, so the book has to appear there and
    // not merely in the fallback system instruction.
    [Fact]
    public async Task SendChatMessage_PutsTheSelectedBookInTheAgentContextBlockWithoutItsText()
    {
        await using var context = CreateContext();
        var documentId = Guid.NewGuid();
        var books = await SeedBookAsync(context, CreateBookPage(documentId, "user-1", "Pan Tadeusz opens with a longing for Lithuania."));
        var generativeAi = new CapturingGenerativeAiClient("Read it.");
        var orchestrator = CreateOrchestrator(context, generativeAi: generativeAi, books: books);
        var chat = await orchestrator.CreateChatAsync("user-1", contextBookDocumentId: documentId);

        await orchestrator.SendChatMessageAsync(chat.Id, "user-1", "Summarise it.");

        Assert.NotNull(generativeAi.LastAgentRequest);
        Assert.Contains("get_book_pages", generativeAi.LastAgentRequest.ContextInstruction);
        // Selecting a book must not inline it: only the current page the user is reading
        // is ever pasted in, and that is a separate block.
        Assert.DoesNotContain(
            "Pan Tadeusz opens with a longing for Lithuania.",
            generativeAi.LastAgentRequest.ContextInstruction);
        Assert.DoesNotContain(
            "Pan Tadeusz opens with a longing for Lithuania.",
            generativeAi.LastAgentRequest.SystemInstruction);
    }

    // The transcript twin of the book fallback. Both are thread-level context that the
    // client stops resending after the first turn.
    [Fact]
    public async Task SendChatMessage_FallsBackToTheThreadsTranscriptContext()
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        context.Users.Add(new ApplicationUser { Id = "user-1", SelectedQuizLanguageCode = "pl" });
        context.RealtimeTranslationTranscripts.Add(new RealtimeTranslationTranscript
        {
            Id = transcriptId,
            UserId = "user-1",
            Title = "Netflix evening",
            TargetLanguage = "pl",
            Stream = RealtimeTranslationTranscriptStreams.Source,
        });
        context.RealtimeTranslationTranscriptSegments.Add(new RealtimeTranslationTranscriptSegment
        {
            Id = Guid.NewGuid(),
            TranscriptId = transcriptId,
            SessionId = Guid.NewGuid(),
            Sequence = 0,
            Stream = RealtimeTranslationTranscriptStreams.Source,
            Text = "Dzień dobry.",
        });
        await context.SaveChangesAsync();
        var generativeAi = new CapturingGenerativeAiClient("Read it.");
        var orchestrator = CreateOrchestrator(context, generativeAi: generativeAi);
        var chat = await orchestrator.CreateChatAsync("user-1", contextTranscriptId: transcriptId);

        await orchestrator.SendChatMessageAsync(chat.Id, "user-1", "What did they say?");

        Assert.NotNull(generativeAi.LastAgentRequest);
        Assert.Contains("Netflix evening", generativeAi.LastAgentRequest.ContextInstruction);
    }

    [Fact]
    public async Task SendQuizMessage_PreservesTheDefaultThreadsTranscriptContext()
    {
        await using var context = CreateContext();
        var quizId = Guid.NewGuid();
        var transcriptId = Guid.NewGuid();
        context.Users.Add(new ApplicationUser { Id = "user-1", SelectedQuizLanguageCode = "pl" });
        context.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        context.RealtimeTranslationTranscripts.Add(new RealtimeTranslationTranscript
        {
            Id = transcriptId,
            UserId = "user-1",
            Title = "Saved lesson",
            TargetLanguage = "pl",
            Stream = RealtimeTranslationTranscriptStreams.Source,
        });
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context);
        var chat = await orchestrator.CreateChatAsync(
            "user-1",
            contextQuizId: quizId,
            contextTranscriptId: transcriptId);

        await orchestrator.SendMessageAsync(quizId, "user-1", "Explain this quiz.");

        context.ChangeTracker.Clear();
        var thread = await context.AssistantThreads.SingleAsync(candidate => candidate.Id == chat.Id);
        Assert.Equal(transcriptId, thread.ContextTranscriptId);
    }

    [Fact]
    public async Task UpdateChat_ClearsBookContextWhenUpdateContextIsSet()
    {
        await using var context = CreateContext();
        var documentId = Guid.NewGuid();
        var books = await SeedBookAsync(context, CreateBookPage(documentId, "user-1", "Rozdział pierwszy."));
        var orchestrator = CreateOrchestrator(context, books: books);
        var chat = await orchestrator.CreateChatAsync("user-1", contextBookDocumentId: documentId);

        await orchestrator.UpdateChatAsync(chat.Id, "user-1", updateContext: true);

        var thread = await context.AssistantThreads.SingleAsync(t => t.Id == chat.Id);
        Assert.Null(thread.ContextBookDocumentId);
    }

    // Renaming leaves updateContext false, which must not disturb the bound material.
    [Fact]
    public async Task UpdateChat_KeepsBookContextWhenOnlyRenaming()
    {
        await using var context = CreateContext();
        var documentId = Guid.NewGuid();
        var books = await SeedBookAsync(context, CreateBookPage(documentId, "user-1", "Rozdział pierwszy."));
        var orchestrator = CreateOrchestrator(context, books: books);
        var chat = await orchestrator.CreateChatAsync("user-1", contextBookDocumentId: documentId);

        await orchestrator.UpdateChatAsync(chat.Id, "user-1", title: "Reading notes");

        var thread = await context.AssistantThreads.SingleAsync(t => t.Id == chat.Id);
        Assert.Equal(documentId, thread.ContextBookDocumentId);
        Assert.Equal("Reading notes", thread.Title);
    }

    [Fact]
    public async Task SendChatMessage_InstructsModelToExtractEveryNonNameWord()
    {
        await using var context = CreateContext();
        var quizId = Guid.NewGuid();
        context.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await context.SaveChangesAsync();
        var generativeAi = new CapturingGenerativeAiClient("Queued the words.");
        var orchestrator = CreateOrchestrator(context, generativeAi: generativeAi);
        var chat = await orchestrator.CreateChatAsync("user-1", quizId);

        await orchestrator.SendChatMessageAsync(
            chat.Id,
            "user-1",
            "Extract vocabulary from this text.",
            contextQuizId: quizId);

        Assert.NotNull(generativeAi.LastAgentRequest);
        Assert.Contains(
            "every unique word except proper names",
            generativeAi.LastAgentRequest.SystemInstruction);
        Assert.Contains(
            "including closed-class words",
            generativeAi.LastAgentRequest.SystemInstruction);
    }

    // Opening the creator routes the turn to the quiz-builder agent, whose tool surface
    // cannot create a second custom quiz.
    [Fact]
    public async Task SendChatMessage_RoutesTheOpenCustomQuizToTheBuilderProfile()
    {
        await using var context = CreateContext();
        var quizId = Guid.NewGuid();
        var customQuizId = Guid.NewGuid();
        context.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        context.CustomQuizzes.Add(CreateCustomQuiz(customQuizId, quizId, "Verb drills"));
        await context.SaveChangesAsync();
        var generativeAi = new CapturingGenerativeAiClient("Added the rows.");
        var orchestrator = CreateOrchestrator(
            context,
            generativeAi: generativeAi,
            tools: AssistantToolFactory.Create(context));
        var chat = await orchestrator.CreateChatAsync("user-1", quizId);

        await orchestrator.SendChatMessageAsync(
            chat.Id,
            "user-1",
            "Generate ten conjugation exercises.",
            contextQuizId: quizId,
            customQuizId: customQuizId);

        var request = generativeAi.LastAgentRequest;
        Assert.NotNull(request);
        Assert.Equal(AssistantAgentProfile.CustomQuizBuilder, request.Profile);
        Assert.Contains("Verb drills", request.ContextInstruction);
        Assert.Contains(customQuizId.ToString(), request.ContextInstruction);
        var toolNames = request.Tools.Select(tool => tool.Name).ToList();
        Assert.DoesNotContain("create_custom_quiz", toolNames);
        Assert.Contains("add_text_input", toolNames);
    }

    [Fact]
    public async Task SendChatMessage_RoutesAQuizPageToTheQuizAssistantProfile()
    {
        await using var context = CreateContext();
        var quizId = Guid.NewGuid();
        context.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await context.SaveChangesAsync();
        var generativeAi = new CapturingGenerativeAiClient("Queued the words.");
        var orchestrator = CreateOrchestrator(
            context,
            generativeAi: generativeAi,
            tools: AssistantToolFactory.Create(context));
        var chat = await orchestrator.CreateChatAsync("user-1", quizId);

        await orchestrator.SendChatMessageAsync(
            chat.Id,
            "user-1",
            "Add ten words about the kitchen.",
            contextQuizId: quizId);

        var request = generativeAi.LastAgentRequest;
        Assert.NotNull(request);
        Assert.Equal(AssistantAgentProfile.QuizAssistant, request.Profile);
        Assert.Contains(quizId.ToString(), request.ContextInstruction);
        var toolNames = request.Tools.Select(tool => tool.Name).ToList();
        Assert.Contains("add_words", toolNames);
        Assert.Contains("create_custom_quiz", toolNames);
        // Library management belongs to the librarian, not to the quiz being worked in.
        Assert.DoesNotContain("move_quiz", toolNames);
        Assert.DoesNotContain("create_collection", toolNames);
    }

    [Fact]
    public async Task SendGlobalMessage_RoutesToTheLibrarianProfile()
    {
        await using var context = CreateContext();
        var generativeAi = new CapturingGenerativeAiClient("Here is your library.");
        var orchestrator = CreateOrchestrator(
            context,
            generativeAi: generativeAi,
            tools: AssistantToolFactory.Create(context));

        await orchestrator.SendGlobalMessageAsync("user-1", "Organise my collections.");

        var request = generativeAi.LastAgentRequest;
        Assert.NotNull(request);
        Assert.Equal(AssistantAgentProfile.Librarian, request.Profile);
        var toolNames = request.Tools.Select(tool => tool.Name).ToList();
        Assert.Contains("list_collections", toolNames);
        Assert.Contains("move_quiz", toolNames);
        // Word and sentence tools need a quiz in context to act on.
        Assert.DoesNotContain("add_words", toolNames);
        Assert.DoesNotContain("list_sentences", toolNames);
    }

    // An authored agent receives only the context block, so page text must be in it or
    // "add words from this page" silently stops working.
    [Fact]
    public async Task SendGlobalMessage_PutsTheBookPageInTheAgentContextBlock()
    {
        await using var context = CreateContext();
        var documentId = Guid.NewGuid();
        var books = await SeedBookAsync(context, CreateBookPage(documentId, "user-1", "Pan Tadeusz opens with a longing for Lithuania."));
        var generativeAi = new CapturingGenerativeAiClient("Queued a quiz from the page.");
        var orchestrator = CreateOrchestrator(context, generativeAi: generativeAi, books: books);

        await orchestrator.SendGlobalMessageAsync(
            "user-1",
            "Make a quiz from this page",
            documentContext: new AssistantDocumentContext(documentId, 3));

        Assert.Contains(
            "Pan Tadeusz opens with a longing for Lithuania.",
            generativeAi.LastAgentRequest!.ContextInstruction);
    }

    [Fact]
    public async Task ApplyPendingChanges_UsesSavedMessageContextQuiz()
    {
        await using var context = CreateContext();
        var quizId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var thread = CreateThread("user-1");
        context.AssistantThreads.Add(thread);
        context.AssistantMessages.Add(new AssistantMessage
        {
            Id = messageId,
            ThreadId = thread.Id,
            ContextQuizId = quizId,
            Sequence = 0,
            Role = AssistantMessageRole.Model,
            ContentJson = StoredText("Ready."),
            PendingChangesJson = JsonSerializer.Serialize(new[]
            {
                new PendingChange(PendingChangeKinds.AddWord, JsonSerializer.SerializeToElement(new
                {
                    word = "iść",
                    translation = "to go",
                })),
            }),
            Status = AssistantMessageStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
        var applier = new CapturingChangeApplier();
        var orchestrator = CreateOrchestrator(context, applier: applier);

        var result = await orchestrator.ApplyGlobalPendingChangesAsync(messageId, "user-1");

        Assert.Equal(1, result.Applied);
        Assert.Equal(quizId, applier.QuizId);
        Assert.Equal(AssistantMessageStatus.Applied, (await context.AssistantMessages.SingleAsync(m => m.Id == messageId)).Status);
    }

    [Fact]
    public async Task ApplyPendingChanges_SecondApplyIsANoOp()
    {
        await using var context = CreateContext();
        var messageId = Guid.NewGuid();
        var thread = CreateThread("user-1");
        context.AssistantThreads.Add(thread);
        context.AssistantMessages.Add(CreateActiveMessageWithPendingChange(messageId, thread.Id));
        await context.SaveChangesAsync();
        var applier = new CountingChangeApplier();
        var orchestrator = CreateOrchestrator(context, applier: applier);

        var first = await orchestrator.ApplyGlobalPendingChangesAsync(messageId, "user-1");
        var second = await orchestrator.ApplyGlobalPendingChangesAsync(messageId, "user-1");

        Assert.Equal(1, first.Applied);
        Assert.Equal(0, second.Applied);
        Assert.Equal(1, applier.Calls);
    }

    [Fact]
    public async Task AssistantMessageStatus_RejectsAStaleConcurrentClaim()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var seed = CreateContext(databaseName, databaseRoot);
        var thread = CreateThread("user-1");
        var messageId = Guid.NewGuid();
        seed.AssistantThreads.Add(thread);
        seed.AssistantMessages.Add(CreateActiveMessageWithPendingChange(messageId, thread.Id));
        await seed.SaveChangesAsync();

        await using var firstContext = CreateContext(databaseName, databaseRoot);
        await using var secondContext = CreateContext(databaseName, databaseRoot);
        var first = await firstContext.AssistantMessages.SingleAsync(message => message.Id == messageId);
        var second = await secondContext.AssistantMessages.SingleAsync(message => message.Id == messageId);

        first.Status = AssistantMessageStatus.Applied;
        await firstContext.SaveChangesAsync();
        second.Status = AssistantMessageStatus.Applied;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task ApplyPendingChanges_RevertsClaimWhenApplyFails()
    {
        await using var context = CreateContext();
        var messageId = Guid.NewGuid();
        var thread = CreateThread("user-1");
        context.AssistantThreads.Add(thread);
        context.AssistantMessages.Add(CreateActiveMessageWithPendingChange(messageId, thread.Id));
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context, applier: new ThrowingChangeApplier());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => orchestrator.ApplyGlobalPendingChangesAsync(messageId, "user-1"));

        Assert.Equal(
            AssistantMessageStatus.Active,
            (await context.AssistantMessages.SingleAsync(m => m.Id == messageId)).Status);
    }

    [Fact]
    public async Task ApplyPendingChanges_DoesNotReopenAConcurrentRejectionWhenApplyFails()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = new InMemoryDatabaseRoot();
        await using var context = CreateContext(databaseName, databaseRoot);
        var messageId = Guid.NewGuid();
        var thread = CreateThread("user-1");
        context.AssistantThreads.Add(thread);
        context.AssistantMessages.Add(CreateActiveMessageWithPendingChange(messageId, thread.Id));
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(
            context,
            applier: new RejectingThenThrowingChangeApplier(
                () => CreateContext(databaseName, databaseRoot),
                messageId));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => orchestrator.ApplyGlobalPendingChangesAsync(messageId, "user-1"));

        context.ChangeTracker.Clear();
        Assert.Equal(
            AssistantMessageStatus.Rejected,
            (await context.AssistantMessages.SingleAsync(message => message.Id == messageId)).Status);
    }

    [Fact]
    public async Task DeleteChat_RemovesMessagesAndBlocksLaterHistory()
    {
        await using var context = CreateContext();
        var thread = CreateThread("user-1");
        context.AssistantThreads.Add(thread);
        context.AssistantMessages.Add(new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Sequence = 0,
            Role = AssistantMessageRole.User,
            ContentJson = StoredText("Hello"),
            Status = AssistantMessageStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context);

        await orchestrator.DeleteChatAsync(thread.Id, "user-1");

        Assert.Empty(context.AssistantMessages);
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.GetChatHistoryAsync(thread.Id, "user-1"));
    }

    [Fact]
    public async Task DeleteChat_queues_trace_purge_before_removing_analytics()
    {
        await using var context = CreateContext();
        var orchestrator = CreateOrchestrator(context);
        var result = await orchestrator.SendGlobalMessageAsync("user-1", "Hello");
        var turn = await context.AssistantTurns.SingleAsync(candidate => candidate.Id == result.TurnId);
        turn.TraceId = "0123456789abcdef0123456789abcdef";
        await context.SaveChangesAsync();

        await orchestrator.DeleteChatAsync(result.ThreadId, "user-1");

        Assert.Empty(context.AssistantTurns);
        var deletions = await context.AssistantTelemetryDeletionRequests.ToListAsync();
        Assert.Equal(6, deletions.Count);
        Assert.Equal(
            ["AppDependencies", "AppEvents", "AppExceptions", "AppGenAIContent", "AppRequests", "AppTraces"],
            deletions.Select(deletion => deletion.TableName).Order());
        Assert.All(deletions, deletion =>
        {
            Assert.Equal(
                deletion.TableName == "AppGenAIContent" ? "TraceId" : "OperationId",
                deletion.DimensionName);
            Assert.Equal(turn.TraceId, deletion.DimensionValue);
            Assert.Equal(AssistantTelemetryDeletionStatus.Pending, deletion.Status);
        });
    }

    [Fact]
    public async Task Assistant_stops_after_twenty_four_model_tool_turns()
    {
        await using var context = CreateContext();
        var generativeAi = new LoopingGenerativeAiClient();
        var tools = new LoopAssistantTools();
        var orchestrator = CreateOrchestrator(
            context,
            generativeAi: generativeAi,
            tools: tools);

        var result = await orchestrator.SendGlobalMessageAsync(
            "user-1",
            "Keep looking things up.");

        Assert.Equal(24, generativeAi.Calls);
        Assert.Equal(24, tools.Calls);
        Assert.Contains("tool-call limit", result.AssistantText);
        Assert.Equal(
            "tool_limit_reached",
            (await context.AssistantTurns.SingleAsync(turn => turn.Id == result.TurnId)).ErrorCategory);
        Assert.Equal(
            50,
            await context.AssistantMessages.CountAsync(message => message.ThreadId == result.ThreadId));
    }

    private static GlosifyContext CreateContext(
        string? databaseName = null,
        InMemoryDatabaseRoot? databaseRoot = null,
        SaveChangesInterceptor? saveChangesInterceptor = null)
    {
        var builder = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(
                databaseName ?? Guid.NewGuid().ToString("N"),
                databaseRoot ?? new InMemoryDatabaseRoot());
        if (saveChangesInterceptor is not null)
        {
            builder.AddInterceptors(saveChangesInterceptor);
        }
        var options = builder.Options;
        return new FactoryBackedGlosifyContext(options);
    }

    private static IAssistantOrchestrator CreateOrchestrator(
        GlosifyContext context,
        IGenerativeAiClient? generativeAi = null,
        IChangeApplier? applier = null,
        IBookDocumentService? books = null,
        IAssistantTools? tools = null,
        ILanguageContext? languageContext = null)
    {
        var languagePreferences = new QuizLanguagePreferenceService(context);
        var contextResolver = new AssistantContextResolver(
            context,
            books ?? new NoopBookDocumentService(),
            languageContext ?? new StaticLanguageContext(),
            languagePreferences);
        var presenter = new AssistantMessagePresenter();
        var timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
        var threadStore = new AssistantThreadStore(
            context,
            contextResolver,
            presenter,
            new AssistantTelemetryDeletionQueue(
                context,
                timeProvider,
                Options.Create(new AssistantAnalyticsOptions())));
        var analytics = new AssistantAnalyticsStore(new TestDbContextFactory(context), timeProvider);
        var turnRunner = new AssistantTurnRunner(
            context,
            generativeAi ?? new StaticGenerativeAiClient("Done."),
            CreateModelResolver(),
            tools ?? new NoopAssistantTools(),
            threadStore,
            contextResolver,
            presenter,
            new AssistantPromptBuilder(),
            new NoopAssistantTurnLeaseService(),
            analytics,
            timeProvider,
            NullLogger<AssistantTurnRunner>.Instance);
        return new AssistantOrchestrator(
            threadStore,
            turnRunner,
            new AssistantChangeWorkflow(
                context,
                applier ?? new CapturingChangeApplier(),
                presenter,
                threadStore,
                timeProvider),
            new AssistantFeedbackService(context, timeProvider));
    }

    private static IGenerativeAiModelResolver CreateModelResolver() =>
        new GenerativeAiModelResolver(
            Options.Create(new GenerativeAiOptions
            {
                Provider = GenerativeAiOptions.FoundryProvider,
                Foundry = new FoundryGenerativeAiOptions
                {
                    ProjectEndpoint = "https://example.services.ai.azure.com/api/projects/test",
                    AssistantDeployment = "test-model",
                    StructuredDeployment = "test-model",
                    VisionDeployment = "test-model",
                    AllowedAssistantDeployments = ["test-model"],
                    AssistantModels =
                    [
                        new AssistantModelOptions
                        {
                            Deployment = "test-model",
                            DisplayName = "Test Model",
                            Provider = "Test",
                            SpeedTier = "Test",
                            CostTier = "Test",
                            CreditMultiplier = 1m,
                        },
                    ],
                },
            }),
            Options.Create(new GeminiOptions
            {
                Model = "test-model",
                AssistantModel = "test-model",
                StructuredModel = "test-model",
            }));

    private static Quiz CreateQuiz(Guid id, string userId) => new()
    {
        Id = id,
        UserId = userId,
        Name = "Polish",
        SourceLanguage = "English",
        TargetLanguage = "Polish",
        Language = "Polish",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static CustomQuiz CreateCustomQuiz(Guid id, Guid quizId, string name) => new()
    {
        Id = id,
        QuizId = quizId,
        Name = name,
        DefinitionJson = """{"schemaVersion":1,"stylePreset":"editorial","blocks":[]}""",
        SchemaVersion = 1,
        IsPlayable = false,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static BookPage CreateBookPage(Guid documentId, string userId, string text)
    {
        var document = new BookDocument
        {
            Id = documentId,
            UserId = userId,
            Title = "Polish Reader",
            OriginalFileName = "polish-reader.pdf",
            BlobName = "books/polish-reader.pdf",
            PageCount = 5,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        return new BookPage
        {
            Id = Guid.NewGuid(),
            BookDocumentId = documentId,
            PageNumber = 3,
            Text = text,
            BookDocument = document,
        };
    }

    /// <summary>
    /// The orchestrator checks ownership through IBookDocumentService but batches chat
    /// titles straight off the DbContext, so a book has to be present in both for a test
    /// to match how production behaves.
    /// </summary>
    private static async Task<StaticBookDocumentService> SeedBookAsync(GlosifyContext context, BookPage page)
    {
        context.BookDocuments.Add(page.BookDocument);
        context.BookPages.Add(page);
        await context.SaveChangesAsync();
        return new StaticBookDocumentService(page);
    }

    private static AssistantThread CreateThread(
        string userId,
        string title = "New chat",
        Guid? quizId = null,
        string? language = "Polish") => new()
        {
            Id = Guid.NewGuid(),
            QuizId = quizId,
            UserId = userId,
            Language = language,
            Title = title,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private static string StoredText(string text) =>
        JsonSerializer.Serialize(new
        {
            parts = new[]
            {
                new { kind = "text", text },
            },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class StaticGenerativeAiClient(string text) : IGenerativeAiClient
    {
        public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext usageContext, string? model = null, CancellationToken cancellationToken = default) =>
            Task.FromException<T>(new NotSupportedException());

        public Task<string> ExtractTextFromImageAsync(byte[] imageBytes, string contentType, string prompt, AiUsageContext usageContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext usageContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentTurnResult(text, []));
    }

    private sealed class CapturingGenerativeAiClient(string text) : IGenerativeAiClient
    {
        public AgentRequest? LastAgentRequest { get; private set; }

        public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext usageContext, string? model = null, CancellationToken cancellationToken = default) =>
            Task.FromException<T>(new NotSupportedException());

        public Task<string> ExtractTextFromImageAsync(byte[] imageBytes, string contentType, string prompt, AiUsageContext usageContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext usageContext, CancellationToken cancellationToken = default)
        {
            LastAgentRequest = request;
            return Task.FromResult(new AgentTurnResult(text, []));
        }
    }

    private sealed class LoopingGenerativeAiClient : IGenerativeAiClient
    {
        public int Calls { get; private set; }

        public Task<T> GenerateStructuredAsync<T>(
            string prompt,
            AiUsageContext usageContext,
            string? model = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<T>(new NotSupportedException());

        public Task<string> ExtractTextFromImageAsync(
            byte[] imageBytes,
            string contentType,
            string prompt,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<AgentTurnResult> RunAgentTurnAsync(
            AgentRequest request,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AgentTurnResult(
                string.Empty,
                [
                    new AgentFunctionCall("loop", "{}")
                    {
                        CallId = $"call-{Calls}",
                    },
                ]));
        }
    }

    private sealed class OneToolThenAnswerGenerativeAiClient : IGenerativeAiClient
    {
        private int _calls;
        public List<AiUsageContext> UsageContexts { get; } = [];

        public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext usageContext, string? model = null, CancellationToken cancellationToken = default) =>
            Task.FromException<T>(new NotSupportedException());

        public Task<string> ExtractTextFromImageAsync(byte[] imageBytes, string contentType, string prompt, AiUsageContext usageContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<AgentTurnResult> RunAgentTurnAsync(
            AgentRequest request,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default)
        {
            UsageContexts.Add(usageContext);
            _calls++;
            var result = _calls == 1
                ? new AgentTurnResult(string.Empty,
                [
                    new AgentFunctionCall("loop", "{}") { CallId = "lookup-1" },
                ])
                : new AgentTurnResult("Done.", []);
            return Task.FromResult(result with
            {
                Metadata = new AgentInvocationMetadata(
                    "foundry",
                    "test-model",
                    $"response-{_calls}",
                    new AiTokenUsage(10, 5, 0, 0, 15),
                    "glosify-librarian",
                    "3",
                    JsonSerializer.Serialize(request)),
            });
        }
    }

    private sealed class ToolThenThrowingGenerativeAiClient : IGenerativeAiClient
    {
        private int _calls;

        public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext usageContext, string? model = null, CancellationToken cancellationToken = default) =>
            Task.FromException<T>(new NotSupportedException());

        public Task<string> ExtractTextFromImageAsync(byte[] imageBytes, string contentType, string prompt, AiUsageContext usageContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext usageContext, CancellationToken cancellationToken = default)
        {
            _calls++;
            return _calls == 1
                ? Task.FromResult(new AgentTurnResult(string.Empty,
                [
                    new AgentFunctionCall("queue_change", "{}") { CallId = "call-1" },
                ]))
                : Task.FromException<AgentTurnResult>(new InvalidDataException("Simulated upstream failure."));
        }
    }

    private sealed class CancellingGenerativeAiClient : IGenerativeAiClient
    {
        public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext usageContext, string? model = null, CancellationToken cancellationToken = default) =>
            Task.FromException<T>(new NotSupportedException());

        public Task<string> ExtractTextFromImageAsync(byte[] imageBytes, string contentType, string prompt, AiUsageContext usageContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<AgentTurnResult> RunAgentTurnAsync(
            AgentRequest request,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentTurnResult>(new OperationCanceledException("Simulated caller cancellation."));
    }

    private sealed class SavingMutationAssistantTools(GlosifyContext context) : IAssistantTools
    {
        private static readonly AgentToolDeclaration Declaration = new(
            "queue_change",
            "Queues a test change.",
            new { type = "object", properties = new { } });

        public IReadOnlyList<AgentToolDeclaration> Declarations { get; } = [Declaration];
        public IReadOnlyList<AgentToolDeclaration> GlobalDeclarations { get; } = [Declaration];
        public IReadOnlyList<AgentToolDeclaration> CustomQuizBuilderDeclarations { get; } = [Declaration];
        public IReadOnlyList<AgentToolDeclaration> QuizAssistantDeclarations { get; } = [Declaration];
        public IReadOnlyList<AgentToolDeclaration> LibrarianDeclarations { get; } = [Declaration];

        public async Task<object> ExecuteAsync(
            string name,
            string argsJson,
            AgentToolContext toolContext,
            CancellationToken cancellationToken)
        {
            toolContext.PendingChanges.Add(new PendingChange(
                PendingChangeKinds.CreateCollection,
                JsonSerializer.SerializeToElement(new { name = "Travel", language = "Polish" })));
            // This represents a credit/accounting save on the runner's shared context.
            await context.SaveChangesAsync(cancellationToken);
            return new { queued = true };
        }
    }

    private sealed class NoopAssistantTools : IAssistantTools
    {
        public IReadOnlyList<AgentToolDeclaration> Declarations { get; } = [];
        public IReadOnlyList<AgentToolDeclaration> GlobalDeclarations { get; } = [];
        public IReadOnlyList<AgentToolDeclaration> CustomQuizBuilderDeclarations { get; } = [];
        public IReadOnlyList<AgentToolDeclaration> QuizAssistantDeclarations { get; } = [];
        public IReadOnlyList<AgentToolDeclaration> LibrarianDeclarations { get; } = [];

        public Task<object> ExecuteAsync(string name, string argsJson, AgentToolContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoopAssistantTurnLeaseService : IAssistantTurnLeaseService
    {
        public Task<Guid?> TryAcquireAsync(Guid threadId, string userId, CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(Guid.NewGuid());

        public Task<bool> RenewAsync(Guid threadId, Guid leaseId, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task ReleaseAsync(Guid threadId, Guid leaseId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FailFinalMessageSaveOnceInterceptor : SaveChangesInterceptor
    {
        public bool FailedFinalMessageSave { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!FailedFinalMessageSave
                && eventData.Context?.ChangeTracker.Entries<AssistantMessage>().Any(entry =>
                    entry.State == EntityState.Added
                    && entry.Entity.Role == AssistantMessageRole.Model) == true)
            {
                FailedFinalMessageSave = true;
                throw new DbUpdateException("Simulated final assistant message persistence failure.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class LoopAssistantTools : IAssistantTools
    {
        public int Calls { get; private set; }
        public IReadOnlyList<AgentToolDeclaration> Declarations { get; } = [];
        public IReadOnlyList<AgentToolDeclaration> CustomQuizBuilderDeclarations { get; } = [];
        public IReadOnlyList<AgentToolDeclaration> QuizAssistantDeclarations { get; } = [];
        public IReadOnlyList<AgentToolDeclaration> GlobalDeclarations { get; } = [];
        public IReadOnlyList<AgentToolDeclaration> LibrarianDeclarations { get; } =
        [
            new AgentToolDeclaration(
                "loop",
                "Continues the test loop.",
                new { type = "object", properties = new { } }),
        ];

        public Task<object> ExecuteAsync(
            string name,
            string argsJson,
            AgentToolContext context,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<object>(new { ok = true });
        }
    }

    private sealed class CapturingChangeApplier : IChangeApplier
    {
        public Guid? QuizId { get; private set; }

        public Task<AssistantApplyResult> ApplyAsync(Guid? quizId, string userId, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken)
        {
            QuizId = quizId;
            return Task.FromResult(new AssistantApplyResult(changes.Count));
        }
    }

    private sealed class CountingChangeApplier : IChangeApplier
    {
        public int Calls { get; private set; }

        public Task<AssistantApplyResult> ApplyAsync(Guid? quizId, string userId, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AssistantApplyResult(changes.Count));
        }
    }

    private sealed class ThrowingChangeApplier : IChangeApplier
    {
        public Task<AssistantApplyResult> ApplyAsync(Guid? quizId, string userId, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken) =>
            throw new InvalidDataException("Simulated apply failure.");
    }

    private sealed class RejectingThenThrowingChangeApplier(
        Func<GlosifyContext> createContext,
        Guid messageId) : IChangeApplier
    {
        public async Task<AssistantApplyResult> ApplyAsync(
            Guid? quizId,
            string userId,
            IReadOnlyList<PendingChange> changes,
            CancellationToken cancellationToken)
        {
            await using var concurrentContext = createContext();
            var message = await concurrentContext.AssistantMessages
                .SingleAsync(candidate => candidate.Id == messageId, cancellationToken);
            message.Status = AssistantMessageStatus.Rejected;
            await concurrentContext.SaveChangesAsync(cancellationToken);
            throw new InvalidDataException("Simulated apply failure after rejection.");
        }
    }

    private static AssistantMessage CreateActiveMessageWithPendingChange(Guid messageId, Guid threadId) => new()
    {
        Id = messageId,
        ThreadId = threadId,
        Sequence = 0,
        Role = AssistantMessageRole.Model,
        ContentJson = StoredText("Ready."),
        PendingChangesJson = JsonSerializer.Serialize(new[]
        {
            new PendingChange(PendingChangeKinds.CreateCollection, JsonSerializer.SerializeToElement(new
            {
                name = "Food",
                language = "Polish",
            })),
        }),
        Status = AssistantMessageStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class NoopBookDocumentService : IBookDocumentService
    {
        public Task<bool> DeleteAsync(Guid documentId, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<BookDocument>> GetUserBooksAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BookDocument>>([]);

        public Task<BookDocument> UploadAsync(string userId, IFormFile file, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BookDocument?> GetOwnedDocumentAsync(Guid id, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BookDocument?>(null);

        public Task<BookPage?> GetOwnedPageAsync(Guid documentId, int pageNumber, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BookPage?>(null);

        public Task<Stream> OpenOwnedPdfAsync(Guid documentId, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenPdfUncheckedAsync(Guid documentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StaticBookDocumentService(BookPage page) : IBookDocumentService
    {
        public Task<bool> DeleteAsync(Guid documentId, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<BookDocument>> GetUserBooksAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BookDocument>>([page.BookDocument]);

        public Task<BookDocument> UploadAsync(string userId, IFormFile file, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BookDocument?> GetOwnedDocumentAsync(Guid id, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BookDocument?>(page.BookDocument.Id == id && page.BookDocument.UserId == userId ? page.BookDocument : null);

        public Task<BookPage?> GetOwnedPageAsync(Guid documentId, int pageNumber, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BookPage?>(
                page.BookDocumentId == documentId && page.PageNumber == pageNumber && page.BookDocument.UserId == userId
                    ? page
                    : null);

        public Task<Stream> OpenOwnedPdfAsync(Guid documentId, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenPdfUncheckedAsync(Guid documentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StaticLanguageContext(string? language = "Polish") : ILanguageContext
    {
        public string? CurrentLanguage { get; private set; } = language;
        public bool HasLanguage => CurrentLanguage != null;
        public IReadOnlyList<string> SupportedLanguages { get; } = ["Polish", "German"];

        public bool TrySetLanguage(string language)
        {
            CurrentLanguage = language;
            return true;
        }

        public void Clear() => CurrentLanguage = null;
    }
}

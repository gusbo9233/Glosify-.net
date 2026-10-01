using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Ai.Assistant.Tools;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Anki;
using Glosify.Services.Books;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.RealtimeTranslation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// The assistant runtime over a real SQLite or isolated SQL Server database and the app's own service registrations,
/// with a scripted model. Tests drive the worker one step at a time, so restarts, races, and
/// commands can land between any two checkpoints.
/// </summary>
internal sealed class AssistantHarness : IAsyncDisposable
{
    private readonly SqliteConnection? _connection;
    private readonly DbContextOptions<GlosifyContext> _options;
    private ServiceProvider _services;

    public const string UserId = "user";

    public ScriptedModel Model { get; } = new();
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    public AssistantRuntimeOptions Options { get; } = new();
    public AssistantAnalyticsOptions Analytics { get; } = new();
    public JevOptions Jev { get; } = new();
    public StaticLanguage Language { get; } = new("Polish");
    public Guid QuizId { get; } = Guid.NewGuid();
    public Guid ThreadId { get; } = Guid.NewGuid();
    public List<IInterceptor> Interceptors { get; } = [];

    /// <summary>Replaces a registration after the app's own, for fault injection.</summary>
    public Action<IServiceCollection>? Configure { get; set; }

    private AssistantHarness(DbContextOptions<GlosifyContext> options, SqliteConnection? connection = null)
    {
        _connection = connection;
        _options = options;
        _services = Build();
    }

    public static async Task<AssistantHarness> CreateAsync(Action<GlosifyContext>? seed = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var harness = new AssistantHarness(new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options, connection);
        return await SeedAsync(harness, seed);
    }

    /// <summary>The caller owns the isolated SQL Server database and its cleanup.</summary>
    public static Task<AssistantHarness> CreateSqlServerAsync(string connectionString, Action<GlosifyContext>? seed = null) =>
        SeedAsync(new AssistantHarness(new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure()).Options), seed);

    private static async Task<AssistantHarness> SeedAsync(AssistantHarness harness, Action<GlosifyContext>? seed)
    {
        await using var db = harness.Db();
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new ApplicationUser { Id = UserId, UserName = UserId, SelectedQuizLanguageCode = "pl", PreferredSourceLanguage = "English" });
        db.Users.Add(new ApplicationUser { Id = "other", UserName = "other" });
        db.Quizzes.Add(new Quiz
        {
            Id = harness.QuizId,
            UserId = UserId,
            Name = "Polish basics",
            SourceLanguage = "English",
            TargetLanguage = "Polish",
            Language = "Polish",
            ProcessingStatus = "Ready",
            CreatedAt = harness.Clock.GetUtcNow(),
        });
        db.AssistantThreads.Add(new AssistantThread
        {
            Id = harness.ThreadId,
            UserId = UserId,
            Language = "Polish",
            ConversationLanguage = "English",
            Title = AssistantThreadDefaults.NewChatTitle,
            ContextQuizId = harness.QuizId,
            CreatedAt = harness.Clock.GetUtcNow(),
            UpdatedAt = harness.Clock.GetUtcNow(),
        });
        seed?.Invoke(db);
        // Seeds cannot know the harness ids in advance; an empty id means the harness quiz or thread.
        foreach (var word in db.ChangeTracker.Entries<Word>().Where(entry => entry.Entity.QuizId == Guid.Empty))
        {
            word.Entity.QuizId = harness.QuizId;
        }

        foreach (var thread in db.ChangeTracker.Entries<AssistantThread>().Where(entry => entry.Entity.QuizId == Guid.Empty))
        {
            thread.Entity.QuizId = harness.QuizId;
        }

        foreach (var message in db.ChangeTracker.Entries<AssistantMessage>().Where(entry => entry.Entity.ThreadId == Guid.Empty))
        {
            message.Entity.ThreadId = harness.ThreadId;
        }

        await db.SaveChangesAsync();
        return harness;
    }

    /// <summary>A fresh context on the same database, as another request would use.</summary>
    public GlosifyContext Db()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>(_options).AddInterceptors(Interceptors).Options;
        return _connection is null ? new GlosifyContext(options) : new SqliteTestContext(options);
    }

    /// <summary>Everything a request showed the model, decoded: text, call arguments, tool results, notes.</summary>
    public static string Transcript(AgentRequest request) =>
        string.Join("\n", request.History.SelectMany(turn => Decode(turn.ContentJson)).Append(request.TrailingInstruction ?? string.Empty));

    private static IEnumerable<string> Decode(string contentJson)
    {
        var root = JsonDocument.Parse(contentJson).RootElement;
        if (root.TryGetProperty("parts", out var parts))
        {
            foreach (var part in parts.EnumerateArray())
            {
                foreach (var name in new[] { "text", "argsJson", "responseJson" })
                {
                    if (part.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        yield return value.GetString()!;
                    }
                }
            }
        }

        if (root.TryGetProperty("outputItemsJson", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                yield return item.GetString()!;
            }
        }
    }

    /// <summary>Discards every scoped service, as a process restart would.</summary>
    public void Restart()
    {
        _services.Dispose();
        _services = Build();
    }

    public AsyncServiceScope Scope() => _services.CreateAsyncScope();

    public async Task<AssistantRunView> StartAsync(
        string message,
        Guid? quizId = null,
        string mode = AssistantRunModes.Interactive,
        Guid? threadId = null,
        string? key = null,
        AssistantRunInput? input = null)
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<AssistantRunStore>().StartAsync(
            threadId ?? ThreadId,
            UserId,
            new AssistantRunStartInput(key ?? Guid.NewGuid().ToString("N"), input ?? new AssistantRunInput(message, quizId == Guid.Empty ? null : quizId ?? QuizId)),
            CancellationToken.None,
            mode);
    }

    /// <summary>Claims and runs one step. False when nothing was claimable.</summary>
    public async Task<bool> StepAsync()
    {
        await using var scope = Scope();
        var claim = await scope.ServiceProvider.GetRequiredService<AssistantRunStore>().ClaimAsync(CancellationToken.None);
        if (claim is null)
        {
            return false;
        }

        await scope.ServiceProvider.GetRequiredService<AssistantRunExecutor>().StepAsync(claim.Value.Id, claim.Value.Lease, CancellationToken.None);
        return true;
    }

    public async Task DrainAsync(int maxSteps = 60)
    {
        for (var step = 0; step < maxSteps; step++)
        {
            if (!await StepAsync())
            {
                return;
            }
        }

        Assert.Fail("The run did not settle within the expected number of steps.");
    }

    public async Task<AssistantRunView> RunAsync(string message, Guid? quizId = null, string mode = AssistantRunModes.Interactive)
    {
        var run = await StartAsync(message, quizId, mode);
        await DrainAsync();
        return await ViewAsync(run.Id);
    }

    public async Task<AssistantRunView> ViewAsync(Guid runId)
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<AssistantRunStore>().ViewAsync(runId, UserId, CancellationToken.None);
    }

    public async Task<AssistantRunView> CommandAsync(Guid runId, string command, string? message = null, bool always = false, long? revision = null)
    {
        var current = await ViewAsync(runId);
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<AssistantRunStore>().CommandAsync(
            runId, UserId, command, new AssistantRunCommand(revision ?? current.Revision, message, always), CancellationToken.None);
    }

    public async Task<AssistantUndoResult> UndoAsync(Guid runId)
    {
        await using var scope = Scope();
        return await scope.ServiceProvider.GetRequiredService<AssistantUndoService>().UndoAsync(runId, UserId, CancellationToken.None);
    }

    public Task<T> OrchestrateAsync<T>(Func<IAssistantOrchestrator, Task<T>> action) =>
        WithAsync(services => action(services.GetRequiredService<IAssistantOrchestrator>()));

    public async Task<T> WithAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Scope();
        return await action(scope.ServiceProvider);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        if (_connection is not null) await _connection.DisposeAsync();
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddMemoryCache();
        services.AddSingleton<TimeProvider>(Clock);
        services.AddScoped(_ => Db());
        services.AddSingleton<IGenerativeAiClient>(Model);
        services.AddSingleton<ILanguageContext>(Language);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Analytics));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Jev));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new AiUsageOptions()));
        services.AddScoped<IQuizLanguagePreferenceService, QuizLanguagePreferenceService>();
        services.AddScoped<IAnkiCollectionService, AnkiCollectionService>();
        services.AddScoped<IQuizService, QuizService>();
        services.AddScoped<ICollectionService, CollectionService>();
        services.AddScoped<IBookDocumentService>(provider => new BookDocumentService(
            provider.GetRequiredService<GlosifyContext>(), null!, null!, Language, NullLogger<BookDocumentService>.Instance));
        services.AddScoped<IRealtimeTranslationTranscriptService, RealtimeTranslationTranscriptService>();
        services.AddScoped<IChangeApplier, ChangeApplier>();
        services.AddScoped<AssistantContextResolver>();
        services.AddScoped<AssistantMessagePresenter>();
        services.AddSingleton<AssistantIntentResolver>();
        services.AddScoped<AssistantTelemetryDeletionQueue>();
        services.AddScoped<AssistantThreadStore>();
        services.AddScoped<AssistantChangeWorkflow>();
        services.AddScoped<AssistantFeedbackService>();
        services.AddSingleton<IAssistantAnalyticsBatchWriter, NullBatchWriter>();
        services.AddScoped<AssistantAnalyticsStore>();
        services.AddSingleton<AssistantRunSignals>();
        services.AddScoped<AssistantRunStore>();
        services.AddScoped<AssistantRunContext>();
        services.AddScoped<AssistantConversation>();
        services.AddScoped<AssistantRunExecutor>();
        services.AddScoped<AssistantSyncAdapter>();
        // Request-reply callers are served inline: each wait runs the worker's next step.
        services.AddSingleton<IAssistantRunWaiter>(new InlineWaiter(this));
        services.AddScoped<AssistantUndoService>();
        services.AddScoped<IAssistantRunService, AssistantRunService>();
        services.AddScoped<IAssistantOrchestrator, AssistantOrchestrator>();
        services.AddSingleton<IToolUseEvaluator, NullEvaluator>();
        services.AddAssistantTools();
        Configure?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class InlineWaiter(AssistantHarness harness) : IAssistantRunWaiter
    {
        public async Task WaitAsync(Guid runId, CancellationToken cancellationToken)
        {
            if (!await harness.StepAsync())
            {
                throw new InvalidOperationException("A request-reply caller is waiting on a run nothing can advance.");
            }
        }
    }

    private sealed class NullBatchWriter : IAssistantAnalyticsBatchWriter
    {
        public ValueTask SubmitAsync(IReadOnlyCollection<AssistantModelInvocation> invocations, IReadOnlyCollection<AssistantToolExecution> executions, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private sealed class NullEvaluator : IToolUseEvaluator
    {
        public Task<ToolUseEvaluation> EvaluateAsync(ToolDecisionSnapshot snapshot, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolUseEvaluation("unavailable", "test", "test", []));
    }
}

/// <summary>
/// SQLite cannot order by <see cref="DateTimeOffset"/>, which the chat list does, so tests store
/// it as a sortable number. SQL Server keeps its native type.
/// </summary>
internal sealed class SqliteTestContext(DbContextOptions<GlosifyContext> options) : GlosifyContext(options)
{
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
    }
}

internal sealed class StaticLanguage(string? language) : ILanguageContext
{
    public string? CurrentLanguage { get; set; } = language;
    public IReadOnlyList<string> SupportedLanguages => ["Polish", "Freestyle"];
    public bool TrySetLanguage(string language) { CurrentLanguage = language; return true; }
    public void Clear() => CurrentLanguage = null;
}

/// <summary>
/// A model whose replies are scripted per call. Each reply is text, tool calls, or both, and
/// every request is kept for assertions about what the model was shown.
/// </summary>
internal sealed class ScriptedModel : IGenerativeAiClient
{
    private readonly Queue<Func<AgentRequest, AgentTurnResult>> _replies = new();

    public List<AgentRequest> Requests { get; } = [];
    public List<AiUsageContext> Usage { get; } = [];
    public Func<AgentRequest, AgentTurnResult>? Fallback { get; set; } = _ => Reply("Done.");
    public Exception? Failure { get; set; }
    public int Calls => Requests.Count;

    public ScriptedModel Then(Func<AgentRequest, AgentTurnResult> reply)
    {
        _replies.Enqueue(reply);
        return this;
    }

    public ScriptedModel ThenText(string text) => Then(_ => Reply(text));

    public ScriptedModel ThenCall(string tool, object args, string? text = null) => ThenCalls(text, (tool, args));

    public ScriptedModel ThenCalls(string? text, params (string Tool, object Args)[] calls) =>
        Then(_ => Reply(text ?? string.Empty, calls));

    public static AgentTurnResult Reply(string text, params (string Tool, object Args)[] calls)
    {
        var callId = 0;
        var functionCalls = calls
            .Select(call => new AgentFunctionCall(call.Tool, call.Args as string ?? JsonSerializer.Serialize(call.Args))
            {
                CallId = $"call-{Guid.NewGuid():N}-{callId++}",
            })
            .ToList();
        var items = functionCalls
            .Select(call => JsonSerializer.Serialize(new { type = "function_call", call_id = call.CallId, name = call.Name, arguments = call.ArgsJson }))
            .Prepend(JsonSerializer.Serialize(new { type = "reasoning", id = "rs_" + Guid.NewGuid().ToString("N"), encrypted_content = "opaque", summary = Array.Empty<object>() }))
            .ToList();
        return new AgentTurnResult(text, functionCalls)
        {
            OutputItemsJson = items,
            Metadata = new AgentInvocationMetadata("openai", OpenAiModels.Luna, "resp-" + Guid.NewGuid().ToString("N"), new AiTokenUsage(1000, 50, 10, 0, 1050, 600)),
        };
    }

    public Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext usageContext, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        Usage.Add(usageContext);
        if (Failure is not null)
        {
            return Task.FromException<AgentTurnResult>(Failure);
        }

        var reply = _replies.Count > 0 ? _replies.Dequeue() : Fallback ?? throw new InvalidOperationException("No scripted reply left.");
        return Task.FromResult(reply(request));
    }

    public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext usageContext, string? model = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<string> ExtractTextFromImageAsync(byte[] imageBytes, string contentType, string prompt, AiUsageContext usageContext, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>Runs one tool by name, the way the runtime would, for tool-level tests.</summary>
internal sealed class AssistantTestTools(AssistantToolbox toolbox)
{
    public AssistantToolbox Toolbox => toolbox;

    public async Task<ToolResult> RunAsync(string name, string argsJson, ToolContext context, CancellationToken cancellationToken = default)
    {
        var tool = toolbox.Find(context.Mode, name) ?? throw new InvalidOperationException($"No {name} tool in {context.Mode} mode.");
        return await tool.ExecuteAsync(argsJson, context, cancellationToken);
    }

    /// <summary>The tool's output as the model would receive it.</summary>
    public async Task<object> ExecuteAsync(string name, string argsJson, ToolContext context, CancellationToken cancellationToken = default) =>
        (await RunAsync(name, argsJson, context, cancellationToken)).Output ?? new { ok = true };
}

internal static class AssistantToolFactory
{
    /// <summary>The app's tool registrations over one context.</summary>
    public static AssistantTestTools Create(GlosifyContext context) =>
        new(new ServiceCollection()
            .AddSingleton(context)
            .AddSingleton<AssistantMessagePresenter>()
            .AddMemoryCache()
            .AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)))
            .AddSingleton<IRealtimeTranslationTranscriptService, RealtimeTranslationTranscriptService>()
            .AddAssistantTools()
            .BuildServiceProvider()
            .GetRequiredService<AssistantToolbox>());
}

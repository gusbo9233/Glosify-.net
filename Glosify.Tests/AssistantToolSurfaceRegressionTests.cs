using Glosify.Data;
using Glosify.Services.Ai.Assistant;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// Pins the tool surface across the split of AssistantTools into one class per tool.
/// </summary>
/// <remarks>
/// The expected lists below were generated from the pre-split file's five hand-maintained
/// declaration arrays, in their original order, and pasted in unedited. The order is part
/// of the prompt the model sees, so a reordering is a behaviour change even when the set
/// is the same — hence Assert.Equal on sequences rather than set comparison.
/// </remarks>
public sealed class AssistantToolSurfaceRegressionTests
{
    [Fact]
    public void Quiz_page_surface_is_unchanged() =>
        AssertSurface(
        [
            "list_words",
            "search_words",
            "get_word",
            "get_quiz_summary",
            "list_sentences",
            "add_word",
            "add_words",
            "add_sentence",
            "add_sentences",
            "edit_word",
            "edit_words",
            "edit_sentence",
            "edit_sentences",
            "delete_word",
            "repair_sentence",
            "delete_sentence",
            // Standard creation is reachable from a selected quiz as of the intent-routing
            // change: "create another normal quiz" had no path but create_custom_quiz before.
            "create_vocabulary_quiz",
            "create_custom_quiz",
            "list_saved_transcripts",
            "get_saved_transcript",
            "list_books",
            "get_book_pages",
            "list_custom_quizzes",
            "list_custom_quiz_templates",
            "get_custom_quiz",
            "add_label",
            "add_text_input",
            "add_checkbox",
            "add_choice",
            "add_word_bank",
            "add_submit_button",
            "add_feedback_message",
            "add_custom_quiz_element",
            "configure_custom_quiz_element",
            "remove_custom_quiz_element",
        ],
            tools => tools.QuizAssistantDeclarations);

    [Fact]
    public void Librarian_surface_is_unchanged() =>
        AssertSurface(
        [
            "list_collections",
            "list_quizzes",
            "create_collection",
            "create_vocabulary_quiz",
            "move_quiz",
            "rename_collection",
            "move_collection",
            "list_saved_transcripts",
            "get_saved_transcript",
            "list_books",
            "get_book_pages",
            "create_custom_quiz_from_content",
            "list_custom_quizzes",
            "list_custom_quiz_templates",
            "get_custom_quiz",
            "add_label",
            "add_text_input",
            "add_checkbox",
            "add_choice",
            "add_word_bank",
            "add_submit_button",
            "add_feedback_message",
            "add_custom_quiz_element",
            "configure_custom_quiz_element",
            "remove_custom_quiz_element",
        ],
            tools => tools.LibrarianDeclarations);

    [Fact]
    public void Custom_quiz_builder_surface_is_unchanged() =>
        AssertSurface(
        [
            "get_custom_quiz",
            "list_custom_quiz_templates",
            "list_words",
            "search_words",
            "list_books",
            "get_book_pages",
            "add_label",
            "add_text_input",
            "add_checkbox",
            "add_choice",
            "add_word_bank",
            "add_submit_button",
            "add_feedback_message",
            "add_custom_quiz_element",
            "configure_custom_quiz_element",
            "remove_custom_quiz_element",
        ],
            tools => tools.CustomQuizBuilderDeclarations);

    [Fact]
    public void Global_surface_is_unchanged() =>
        AssertSurface(
        [
            "list_collections",
            "list_quizzes",
            "create_collection",
            "create_vocabulary_quiz",
            "list_saved_transcripts",
            "get_saved_transcript",
            "list_books",
            "get_book_pages",
            "move_quiz",
            "rename_collection",
            "move_collection",
            "list_custom_quizzes",
            "list_custom_quiz_templates",
            "get_custom_quiz",
            "create_custom_quiz",
            "create_custom_quiz_from_content",
            "add_label",
            "add_text_input",
            "add_checkbox",
            "add_choice",
            "add_word_bank",
            "add_submit_button",
            "add_feedback_message",
            "add_custom_quiz_element",
            "configure_custom_quiz_element",
            "remove_custom_quiz_element",
        ],
            tools => tools.GlobalDeclarations);

    [Fact]
    public void Quiz_only_surface_is_unchanged() =>
        AssertSurface(
        [
            "list_words",
            "search_words",
            "get_word",
            "get_quiz_summary",
            "list_sentences",
            "add_word",
            "add_words",
            "add_sentence",
            "add_sentences",
            "edit_word",
            "edit_words",
            "edit_sentence",
            "edit_sentences",
            "delete_word",
            "repair_sentence",
            "delete_sentence",
        ],
            tools => tools.Declarations);

    /// <summary>
    /// Every declared tool resolves to something that can run. The old dispatcher was a
    /// switch with no link to the declaration lists, so a tool could be offered to the
    /// model and then answer "Unknown tool"; this is the check that could not exist then.
    /// </summary>
    [Fact]
    public async Task Every_declared_tool_is_dispatchable()
    {
        await using var context = CreateContext();
        var tools = AssistantToolFactory.Create(context);

        var declared = tools.GlobalDeclarations
            .Concat(tools.QuizAssistantDeclarations)
            .Concat(tools.LibrarianDeclarations)
            .Concat(tools.CustomQuizBuilderDeclarations)
            .Concat(tools.Declarations)
            .Select(declaration => declaration.Name)
            .Distinct(StringComparer.Ordinal);

        foreach (var name in declared)
        {
            var result = await tools.ExecuteAsync(
                name,
                "{}",
                new AgentToolContext { UserId = "user-1" },
                CancellationToken.None);

            Assert.DoesNotContain("Unknown tool", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The transcript reader and the model page the same transcript, so the tool has to
    /// offer the page coordinate the user names — and at_time, the only coordinate the
    /// source and translation streams share.
    /// </summary>
    [Fact]
    public void Get_saved_transcript_accepts_page_and_time_coordinates()
    {
        using var context = CreateContext();
        var declaration = Assert.Single(
            AssistantToolFactory.Create(context).GlobalDeclarations,
            item => item.Name == "get_saved_transcript");

        var properties = System.Text.Json.JsonSerializer
            .SerializeToElement(declaration.ParametersJsonSchema)
            .GetProperty("properties");

        Assert.True(properties.TryGetProperty("page", out _));
        Assert.True(properties.TryGetProperty("at_time", out _));
        Assert.True(properties.TryGetProperty("offset", out _));
        Assert.True(properties.TryGetProperty("stream", out _));
    }

    private static void AssertSurface(
        string[] expected,
        Func<IAssistantTools, IReadOnlyList<Glosify.Services.Ai.Generation.AgentToolDeclaration>> surface)
    {
        using var context = CreateContext();
        var actual = surface(AssistantToolFactory.Create(context))
            .Select(declaration => declaration.Name)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    private static GlosifyContext CreateContext() =>
        new(new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}

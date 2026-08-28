using Glosify.Services.Ai.Assistant.Tools;

namespace Glosify.Services.Ai.Assistant;

/// <summary>
/// Which tools each assistant profile is offered, and in which order.
/// </summary>
internal static class AssistantToolSurfaces
{
    /// <summary>The tools that operate on a selected language-learning quiz.</summary>
    public static readonly Type[] Quiz =
    [
        typeof(ListWordsTool),
        typeof(SearchWordsTool),
        typeof(GetWordTool),
        typeof(GetQuizSummaryTool),
        typeof(ListSentencesTool),
        typeof(AddWordTool),
        typeof(AddWordsTool),
        typeof(AddSentenceTool),
        typeof(AddSentencesTool),
        typeof(EditWordTool),
        typeof(EditWordsTool),
        typeof(EditSentenceTool),
        typeof(EditSentencesTool),
        typeof(DeleteWordTool),
        typeof(DeleteSentenceTool),
    ];

    /// <summary>The tools available with no quiz in context.</summary>
    public static readonly Type[] Global =
    [
        typeof(ListCollectionsTool),
        typeof(ListQuizzesTool),
        typeof(CreateCollectionTool),
        typeof(CreateQuizTool),
        typeof(ListSavedTranscriptsTool),
        typeof(GetSavedTranscriptTool),
        typeof(ListBooksTool),
        typeof(GetBookPagesTool),
        typeof(SearchBookPagesTool),
        typeof(MoveQuizTool),
        typeof(RenameCollectionTool),
        typeof(MoveCollectionTool),
    ];

    /// <summary>
    /// The tools offered on a quiz page: its words and sentences, creation of another
    /// standard quiz, and read-only source-material tools.
    /// </summary>
    public static readonly Type[] QuizAssistant =
    [
        .. Quiz,
        typeof(CreateQuizTool),
        typeof(ListSavedTranscriptsTool),
        typeof(GetSavedTranscriptTool),
        typeof(ListBooksTool),
        typeof(GetBookPagesTool),
        typeof(SearchBookPagesTool),
    ];

    /// <summary>The tools offered with no quiz selected.</summary>
    public static readonly Type[] Librarian =
    [
        typeof(ListCollectionsTool),
        typeof(ListQuizzesTool),
        typeof(CreateCollectionTool),
        typeof(CreateQuizTool),
        typeof(MoveQuizTool),
        typeof(RenameCollectionTool),
        typeof(MoveCollectionTool),
        typeof(ListSavedTranscriptsTool),
        typeof(GetSavedTranscriptTool),
        typeof(ListBooksTool),
        typeof(GetBookPagesTool),
        typeof(SearchBookPagesTool),
    ];

    public static readonly Type[] FreestyleQuizAssistant =
    [
        typeof(ListWordsTool),
        typeof(SearchWordsTool),
        typeof(GetWordTool),
        typeof(GetQuizSummaryTool),
        typeof(AddWordTool),
        typeof(AddWordsTool),
        typeof(EditWordTool),
        typeof(EditWordsTool),
        typeof(DeleteWordTool),
        typeof(CreateQuizTool),
        typeof(ListBooksTool),
        typeof(GetBookPagesTool),
        typeof(SearchBookPagesTool),
    ];

    public static readonly Type[] FreestyleLibrarian =
    [
        typeof(ListCollectionsTool),
        typeof(ListQuizzesTool),
        typeof(CreateCollectionTool),
        typeof(CreateQuizTool),
        typeof(MoveQuizTool),
        typeof(RenameCollectionTool),
        typeof(MoveCollectionTool),
        typeof(ListBooksTool),
        typeof(GetBookPagesTool),
        typeof(SearchBookPagesTool),
    ];
}

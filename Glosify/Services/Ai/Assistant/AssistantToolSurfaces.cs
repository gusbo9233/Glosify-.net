using Glosify.Services.Ai.Assistant.Tools;

namespace Glosify.Services.Ai.Assistant;

/// <summary>
/// Which tools each assistant profile is offered, and in which order.
/// </summary>
/// <remarks>
/// The order is deliberate and part of the prompt, so these stay curated rather than
/// derived. Naming types instead of strings is what stops them drifting from the code:
/// a tool that is renamed or deleted breaks the build here.
/// </remarks>
internal static class AssistantToolSurfaces
{
    /// <summary>
    /// Everything a custom quiz needs: inspect it, look up bindable words, and build or
    /// change its elements. Shared by the profiles that can reach a custom quiz.
    /// </summary>
    private static readonly Type[] CustomQuizElements =
    [
        typeof(ListCustomQuizzesTool),
        typeof(ListCustomQuizTemplatesTool),
        typeof(GetCustomQuizTool),
        typeof(AddLabelTool),
        typeof(AddTextInputTool),
        typeof(AddCheckboxTool),
        typeof(AddChoiceTool),
        typeof(AddWordBankTool),
        typeof(AddSubmitButtonTool),
        typeof(AddFeedbackMessageTool),
        typeof(AddCustomQuizElementTool),
        typeof(ConfigureCustomQuizElementTool),
        typeof(RemoveCustomQuizElementTool),
    ];

    /// <summary>The quiz tools alone, without the library or custom quiz surface.</summary>
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
        typeof(RepairSentenceTool),
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
        typeof(MoveQuizTool),
        typeof(RenameCollectionTool),
        typeof(MoveCollectionTool),
        typeof(ListCustomQuizzesTool),
        typeof(ListCustomQuizTemplatesTool),
        typeof(GetCustomQuizTool),
        typeof(CreateCustomQuizTool),
        typeof(CreateCustomQuizFromContentTool),
        typeof(AddLabelTool),
        typeof(AddTextInputTool),
        typeof(AddCheckboxTool),
        typeof(AddChoiceTool),
        typeof(AddWordBankTool),
        typeof(AddSubmitButtonTool),
        typeof(AddFeedbackMessageTool),
        typeof(AddCustomQuizElementTool),
        typeof(ConfigureCustomQuizElementTool),
        typeof(RemoveCustomQuizElementTool),
    ];

    /// <summary>
    /// The tools offered on a quiz page: its words and sentences, plus everything needed
    /// to start and fill a custom quiz for it. Collection and library management is
    /// absent — moving and renaming quizzes belongs to the library context, not to the
    /// quiz the user is working inside.
    /// </summary>
    public static readonly Type[] QuizAssistant =
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
        typeof(RepairSentenceTool),
        typeof(DeleteSentenceTool),
        typeof(CreateCustomQuizTool),
        typeof(ListSavedTranscriptsTool),
        typeof(GetSavedTranscriptTool),
        typeof(ListBooksTool),
        typeof(GetBookPagesTool),
        .. CustomQuizElements,
    ];

    /// <summary>
    /// The tools offered with no quiz selected: organising the library, creating quizzes,
    /// and building a custom quiz from source material. The per-word and per-sentence
    /// tools are absent because they need a quiz in context to act on.
    /// </summary>
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
        typeof(CreateCustomQuizFromContentTool),
        .. CustomQuizElements,
    ];

    /// <summary>
    /// The tools offered inside the custom quiz creator. The quiz-creation tools are
    /// deliberately absent: with a custom quiz open on screen, creating another one can
    /// only strand the editor the user is looking at. Everything here either inspects the
    /// open document or edits it.
    /// </summary>
    public static readonly Type[] CustomQuizBuilder =
    [
        typeof(GetCustomQuizTool),
        typeof(ListCustomQuizTemplatesTool),
        typeof(ListWordsTool),
        typeof(SearchWordsTool),
        // Reading the selected book is safe here and is how a drill gets built from a
        // textbook page; nothing in this pair can create or strand a quiz.
        typeof(ListBooksTool),
        typeof(GetBookPagesTool),
        typeof(AddLabelTool),
        typeof(AddTextInputTool),
        typeof(AddCheckboxTool),
        typeof(AddChoiceTool),
        typeof(AddWordBankTool),
        typeof(AddSubmitButtonTool),
        typeof(AddFeedbackMessageTool),
        typeof(AddCustomQuizElementTool),
        typeof(ConfigureCustomQuizElementTool),
        typeof(RemoveCustomQuizElementTool),
    ];
}

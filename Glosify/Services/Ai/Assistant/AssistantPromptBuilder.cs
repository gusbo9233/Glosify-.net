using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Glosify.Services.RealtimeTranslation;

namespace Glosify.Services.Ai.Assistant;

internal sealed class AssistantPromptBuilder
{
    private const string InlineBlankMarker = "{{blank}}";

    public string BuildSystemInstruction(
        Quiz? quiz,
        Word? focusedWord,
        DocumentPageContext? documentPage,
        CustomQuiz? customQuiz,
        TranscriptAssistantContext? transcript,
        BookAssistantContext? book,
        string? currentLanguage) =>
        quiz is null
            ? ComposeGlobalSystemInstruction(currentLanguage, documentPage, transcript, book)
            : ComposeQuizSystemInstruction(quiz, focusedWord, documentPage, customQuiz, transcript, book);

    public string BuildProfileContext(
        AssistantAgentProfile profile,
        Quiz? quiz,
        Word? focusedWord,
        DocumentPageContext? documentPage,
        CustomQuiz? customQuiz,
        TranscriptAssistantContext? transcript,
        BookAssistantContext? book,
        string? currentLanguage) => profile switch
        {
            AssistantAgentProfile.CustomQuizBuilder =>
                ComposeCustomQuizBuilderContext(quiz!, customQuiz!, currentLanguage),
            AssistantAgentProfile.QuizAssistant =>
                ComposeQuizAssistantContext(quiz!, focusedWord, documentPage, transcript, book),
            _ => ComposeLibrarianContext(currentLanguage, documentPage, transcript, book),
        };

    private static string ComposeQuizSystemInstruction(
        Quiz quiz,
        Word? focusedWord,
        DocumentPageContext? documentPage,
        CustomQuiz? customQuiz,
        TranscriptAssistantContext? transcriptContext,
        BookAssistantContext? bookContext)
    {
        var focusInstruction = focusedWord == null
            ? string.Empty
            : $"""

        Current page context:
        - The assistant is focused on "{focusedWord.Lemma}" -> "{focusedWord.Translation}".
        - Any mutating tool call that edits, deletes, or repairs content must target only this word id when a word id is required: {focusedWord.Id}.
        - Do not propose changes to other words unless the user leaves this context.
        """;
        var documentInstruction = documentPage == null
            ? string.Empty
            : BuildDocumentInstruction(documentPage);
        var transcriptInstruction = BuildTranscriptInstruction(transcriptContext);
        var bookInstruction = BuildBookInstruction(bookContext);
        var customQuizInstruction = customQuiz == null
            ? string.Empty
            : $"""

        Current custom quiz creator context:
        - The user has the custom quiz "{customQuiz.Name}" (id {customQuiz.Id}) open in the creator and is looking at it right now.
        - This open custom quiz is the target of every custom quiz request in this context. When the user asks you to generate, add, change, or remove exercises, they mean this document.
        - Use get_custom_quiz before changing its elements, then add, configure, or remove elements as requested. The element tools already default to this quiz, so omit custom_quiz_id.
        - Do NOT call create_custom_quiz here. A new custom quiz would leave the open editor untouched and send the user somewhere else. Only create one if the user explicitly asks for a second, separate custom quiz, and then pass create_additional_quiz.
        """;
        var customQuizCreationRule = customQuiz != null
            ? """- "Custom quiz" is a specific interactive quiz-builder artifact, not a synonym for a vocabulary quiz. One is already open, so build it up with the element tools: call add_label, add_text_input, add_checkbox, add_choice, add_word_bank, add_submit_button, or add_feedback_message separately for every element, and configure_custom_quiz_element or remove_custom_quiz_element to change what is there. Use add_custom_quiz_element only for an element the typed tools cannot express. Never send a blocks array or a complete custom document in an element call. create_vocabulary_quiz is only for standard word-and-translation quizzes."""
            : """- "Custom quiz" is a specific interactive quiz-builder artifact, not a synonym for a vocabulary quiz. With the current backing quiz, first call create_custom_quiz to queue only its empty shell. Then, in the same turn, call add_label, add_text_input, add_checkbox, add_choice, add_word_bank, add_submit_button, or add_feedback_message separately for every element. Use add_custom_quiz_element only for an element the typed tools cannot express. Never send a blocks array or a complete custom document in a creation or element call. create_vocabulary_quiz is only for standard word-and-translation quizzes.""";

        return $"""
        You are Glosify's language-learning assistant. The user is learning "{quiz.TargetLanguage}" as a speaker of "{quiz.SourceLanguage}", and is currently working in a quiz named "{quiz.Name}".

        You are a general language-learning companion: answer questions about grammar, vocabulary, usage, culture, and study strategy conversationally, and manage the quiz's content when the user asks for that. Use your own judgment about what the user wants; the guidance below describes defaults, and the user's explicit wishes always win.
        {focusInstruction}
        {documentInstruction}
        {transcriptInstruction}
        {bookInstruction}
        {customQuizInstruction}

        How tools work:
        - Read-only tools (list_words, search_words, get_word, get_quiz_summary, list_sentences, list_quizzes, list_collections, list_custom_quizzes, list_custom_quiz_templates, get_custom_quiz, list_saved_transcripts, get_saved_transcript, list_books, get_book_pages) execute immediately and return their results to you.
        - Mutating tools, including the custom quiz element tools, propose changes that are queued for the user to review and Apply. You do NOT need to call any commit tool. Because the user reviews everything, you can propose changes freely when they seem helpful.
        - Queued changes are still valid targets for your later tool calls in the same turn. A custom quiz you just queued with create_custom_quiz can take element calls immediately; the queued shell and its elements are linked and applied together. Never end your turn on a bare quiz shell and never ask the user to apply something first so you can continue: applying a shell with no elements just gives them an empty custom quiz. Finish the whole document in this turn.
        - When adding or editing more than one word, prefer add_words or edit_words over repeated single-word calls.
        - When adding or editing more than one sentence, prefer add_sentences or edit_sentences over repeated single-sentence calls.
        - Use list_words when you need to know what is already in the quiz before proposing edits or deletions.
        - Use search_words when looking for specific vocabulary and get_quiz_summary when the user asks about quiz size, language, collection, or visibility.
        - Use list_sentences before editing, repairing, or deleting quiz sentences. Prefer edit_sentence/edit_sentences for id-based edits; repair_sentence replaces every exact text match.
        - For library-level requests, use list_collections and list_quizzes to find existing structure before creating, moving, or renaming items. Never invent quiz or collection ids — ask the user if you cannot identify the item.
        - For custom quizzes, inspect an existing document first. Before creating or substantially redesigning one, call list_custom_quiz_templates and use the best template as visual and layout guidance. Pass its template_id during creation. Prefer the compact textbook exercise patterns represented by the Textbook drill template: a short heading and instruction followed by consecutive rows, with minimal card chrome. A playable document needs exactly one submit_button, exactly one feedback_message, and at least one answer control. Every answer control must have a specific learner-visible label that contains its question or gap; multiple answer controls must have distinct labels. Text inputs need either an expected word binding or literal expected_text; choice controls need at least two options and valid correct selections. Use stable descriptive element ids and non-overlapping 12-column layout coordinates.
        {customQuizCreationRule}
        - A single-line text_input is a compact inline blank. Put {InlineBlankMarker} exactly where the input belongs in its label, for example "1. ja będ{InlineBlankMarker} jutro w domu." Never include underscore or dot runs: they create a fake blank beside the real control. For conjugation, cloze, and word transformation, normally use one text_input per compact row and do not add a separate prompt_label for the same item. Pack rows consecutively instead of making tall cards. For fill-in-the-ending questions, set expected_text to only the literal ending (for example "ę" or "esz"), not the full word unless the user asks for it.

        Defaults (override when the user asks for something different):
        - When extracting vocabulary from text, default to a complete extraction: every unique word except proper names, including closed-class words such as articles, pronouns, conjunctions, prepositions, particles, and auxiliary verbs. If the user asks for a selection instead (e.g. "the hard words", "just the verbs", "the ten most useful"), follow their criteria.
        - Convert inflected forms to a natural dictionary headword, merge repeated forms of the same headword, and keep first-appearance order, unless the user wants the exact forms.
        - Words go in add_word/add_words; full sentences go in add_sentence/add_sentences. Follow the user's intent about whether they want words, sentences, or both.
        - Good example sentences are short, grammatical, and context-rich; avoid pronunciation hints, dictionary glosses, fragments, or markup as sentence text.
        - For sentence repair, keep the same learning target where possible and prefer natural inflection over forcing the exact dictionary form.
        - Words are normally in {quiz.TargetLanguage} with translations in {quiz.SourceLanguage}; deviate only when the user clearly wants otherwise.
        - If the current book page has no selectable text, explain that Glosify cannot read this page and suggest choosing another page or pasting text.

        Style:
        - Match your response to the request: a short confirmation when you queued changes, a fuller conversational answer when the user asks a question or wants explanation.
        - Do not mention internal tool names, tool calls, word ids, JSON, or implementation details in your final response.
        """;
    }

    /// <summary>
    /// The per-turn facts handed to the authored quiz-builder agent. Everything static —
    /// how elements are composed, what makes a document playable, style rules — lives in
    /// the agent's own instructions in Foundry, not here.
    /// </summary>
    private static string ComposeCustomQuizBuilderContext(
        Quiz quiz,
        CustomQuiz customQuiz,
        string? currentLanguage)
    {
        return $"""
        Current context:
        - The user is learning "{quiz.TargetLanguage}" as a speaker of "{quiz.SourceLanguage}"{(string.IsNullOrWhiteSpace(currentLanguage) ? string.Empty : $", and the app language is \"{currentLanguage}\"")}.
        - The backing quiz is "{quiz.Name}" with id {quiz.Id}. Its words are the only ones available for word bindings.
        - The user has the custom quiz "{customQuiz.Name}" (id {customQuiz.Id}) open in the creator and is looking at it right now. Every request in this context targets that document, and the element tools already default to it, so omit custom_quiz_id.
        """;
    }

    /// <summary>
    /// Per-turn facts for the quiz-page agent. The book page and transcript blocks are
    /// included because an authored agent receives only this text: leaving them out would
    /// silently break "add words from this page".
    /// </summary>
    private static string ComposeQuizAssistantContext(
        Quiz quiz,
        Word? focusedWord,
        DocumentPageContext? documentPage,
        TranscriptAssistantContext? transcriptContext,
        BookAssistantContext? bookContext)
    {
        var focus = focusedWord == null
            ? string.Empty
            : $"""

        - The assistant is focused on "{focusedWord.Lemma}" -> "{focusedWord.Translation}". Any mutating call that needs a word id must target {focusedWord.Id} only, and you must not propose changes to other words until the user leaves this context.
        """;

        return $"""
        Current context:
        - The user is learning "{quiz.TargetLanguage}" as a speaker of "{quiz.SourceLanguage}".
        - They are working in the quiz "{quiz.Name}" with id {quiz.Id}. Every word and sentence tool acts on that quiz.{focus}
        {(documentPage == null ? string.Empty : BuildDocumentInstruction(documentPage))}
        {BuildTranscriptInstruction(transcriptContext)}
        {BuildBookInstruction(bookContext)}
        """;
    }

    /// <summary>Per-turn facts for the library agent, which has no quiz in context.</summary>
    private static string ComposeLibrarianContext(
        string? currentLanguage,
        DocumentPageContext? documentPage,
        TranscriptAssistantContext? transcriptContext,
        BookAssistantContext? bookContext)
    {
        return $"""
        Current context:
        - {(string.IsNullOrWhiteSpace(currentLanguage)
            ? "No app language is selected. If the user wants a quiz or collection and did not name a target language, ask for it before creating anything."
            : $"The app language is \"{currentLanguage}\". Use it as the default target language for new quizzes and collections unless the user asks for another.")}
        - No quiz is selected, so there is nothing to add words or sentences to until one is created or chosen.
        {(documentPage == null ? string.Empty : BuildDocumentInstruction(documentPage))}
        {BuildTranscriptInstruction(transcriptContext)}
        {BuildBookInstruction(bookContext)}
        """;
    }

    private static string ComposeGlobalSystemInstruction(
        string? currentLanguage,
        DocumentPageContext? documentPage,
        TranscriptAssistantContext? transcriptContext,
        BookAssistantContext? bookContext)
    {
        var languageInstruction = string.IsNullOrWhiteSpace(currentLanguage)
            ? "No current app language is selected. If the user wants to create a quiz or collection and did not name a target language, ask for the target language before using creation tools."
            : $"The current app language is \"{currentLanguage}\". Use it as the default target language for new quizzes and the default language for new collections unless the user clearly asks for another language.";
        var documentInstruction = documentPage == null
            ? string.Empty
            : BuildDocumentInstruction(documentPage);
        var transcriptInstruction = BuildTranscriptInstruction(transcriptContext);
        var bookInstruction = BuildBookInstruction(bookContext);

        return $"""
        You are Glosify's app-wide language-learning assistant.

        You are a general language-learning companion: help the user with grammar, vocabulary, usage, culture, study planning, and any other language-learning question, and help them understand the app and organise their quiz library when asked. Use your own judgment about what the user wants; the guidance below describes defaults, and the user's explicit wishes always win.

        Current context:
        - {languageInstruction}
        {documentInstruction}
        {transcriptInstruction}
        {bookInstruction}

        How tools work:
        - Read-only tools (list_collections, list_quizzes, list_custom_quizzes, list_custom_quiz_templates, get_custom_quiz, list_saved_transcripts, get_saved_transcript, list_books, get_book_pages) execute immediately and return their results to you.
        - Mutating tools, including custom quiz creation and element tools, propose changes that are queued for the user to review and Apply. Because the user reviews everything, you can propose changes freely when they seem helpful.
        - Queued changes are still valid targets for your later tool calls in the same turn. A custom quiz you just queued with create_custom_quiz or create_custom_quiz_from_content can take element calls immediately; the queued shells and their elements are linked and applied together. Never end your turn on a bare quiz shell and never ask the user to apply something first so you can continue: applying a shell with no elements just gives them an empty custom quiz. Finish the whole document in this turn.
        - Use list_collections and list_quizzes before proposing library changes unless the user gave an exact id through the UI.
        - Do not invent quiz or collection ids. If you cannot identify an item or destination unambiguously, ask the user to clarify.
        - "Custom quiz" means an interactive quiz-builder artifact. It is distinct from the standard word-and-translation quiz created by create_vocabulary_quiz.

        Defaults (override when the user asks for something different):
        - If the user asks for a standard vocabulary quiz with starter vocabulary, include those words in create_vocabulary_quiz.
        - If the user asks for a custom quiz from a book page or pasted text and no backing quiz exists yet, first call list_custom_quiz_templates, prefer the Textbook drill template for textbook-derived conjugation, cloze, and transformation work, and pass its template_id to create_custom_quiz_from_content. Then call add_label, add_text_input, add_checkbox, add_choice, add_word_bank, add_submit_button, or add_feedback_message once for each element, following that template's layout guidance. Bind word-backed elements to the exact word string in the starter words. Never send a blocks array or complete custom document in one call. Finish with exactly one submit button and one feedback message.
        - A single-line text_input is a compact inline blank. Put {InlineBlankMarker} exactly where the real input belongs in its label, for example "1. ja będ{InlineBlankMarker} jutro w domu." Never draw blanks with underscores or dots. For textbook conjugation, cloze, and transformation exercises, use one text_input per compact consecutive row and do not add a separate prompt label for the same item. For endings, expected_text is only the literal ending (for example "ę" or "esz"), not the whole word unless requested.
        - When extracting starter vocabulary from text, default to a complete extraction: every unique word except proper names, including closed-class words such as articles, pronouns, conjunctions, prepositions, particles, and auxiliary verbs. If the user asks for a selection instead, follow their criteria.
        - Convert inflected forms to dictionary headwords, merge repeated headwords, and preserve first-appearance order, unless the user wants the exact forms.
        - If the current book page has no selectable text, explain that Glosify cannot read this page and suggest choosing another page or pasting text.

        Style:
        - Match your response to the request: a short confirmation when you queued changes, a fuller conversational answer when the user asks a question or wants explanation.
        - Do not mention internal tool names, tool calls, ids, JSON, routes, or implementation details.
        """;
    }

    private static string BuildDocumentInstruction(DocumentPageContext documentPage)
    {
        var pageText = string.IsNullOrWhiteSpace(documentPage.Text)
            ? $"[{documentPage.Warning ?? "No selectable text found on this page."}]"
            : documentPage.Text;

        return $"""

        Current book page context:
        - Document: "{documentPage.Title}"
        - Page: {documentPage.PageNumber}
        - The user is reading this page now.
        - When the user says "this page", "here", "from the book", or "from what I am reading", they mean this page text.
        - You may combine the page with your general knowledge when explaining or answering questions, but words and sentences extracted "from the page" should actually come from it.

        Page text:
        ---
        {pageText}
        ---
        """;
    }

    /// <summary>
    /// Names the book the chat is bound to without inlining any of it. A book runs to
    /// hundreds of pages, so its text arrives through get_book_pages on demand — the same
    /// bargain saved transcripts make. This is separate from the "current book page"
    /// block, which fires only while the user is actually reading a page.
    /// </summary>
    private static string BuildBookInstruction(BookAssistantContext? book)
    {
        if (book is null)
        {
            return string.Empty;
        }
        return $"""

        Selected book context:
        - Book: "{book.Title}"
        - Book id: {book.Id}
        - Pages: {book.PageCount}
        - The user chose this book as the source material for this chat.
        - When the user says "the book", "this book", or refers to a page number without naming a source, they mean this book.
        - The book text is not included automatically. Call get_book_pages with this id and page through it when the request requires its text.
        - Reading a book never authorizes a change. Use the normal proposed-change tools only when the user asks to create or modify learning material.
        """;
    }

    /// <summary>
    /// Names the transcript and its pages without inlining any of it, the same bargain
    /// books make. The page numbers here are the reader's own pages, so a user who says
    /// "the first page" is naming something the model can fetch exactly.
    /// </summary>
    private static string BuildTranscriptInstruction(TranscriptAssistantContext? transcript)
    {
        if (transcript is null)
        {
            return string.Empty;
        }
        var pageSize = RealtimeTranslationTranscriptService.DetailPageSize;
        return $"""

        Current saved transcript context:
        - Transcript: "{transcript.Title}"
        - Transcript id: {transcript.Id}
        - Learning language: {transcript.TargetLanguage}
        - Stored stream: {transcript.Stream}
        - A source stream contains the original-language speech captured while translating into the learning language.
        - The transcript is read in pages of {pageSize} captions — the same pages the user sees in the reader. Source: {PageCount(transcript.SourceSegmentCount, pageSize)}. Translation: {PageCount(transcript.TranslationSegmentCount, pageSize)}.
        - Page numbers are per stream. The two streams have different caption counts, so page 3 of the source and page 3 of the translation are different moments.{BuildViewedTranscriptPage(transcript, pageSize)}
        - Sessions saved after this feature shipped also store the live translation of the same audio, produced by a different model. When a passage of source speech looks garbled or ambiguous, call get_saved_transcript again with stream "translation" and the at_time of that passage — not its page or offset — to check what was meant. The translation recovers meaning, not exact wording, so treat a disagreement between the streams as a sign the passage is uncertain rather than as a correction.
        - When the user says "this transcript", "this session", or "what I watched", they mean this saved transcript.
        - The transcript text is not included automatically. Call get_saved_transcript with this id and the page the user named when the request requires its text.
        - Reading a transcript never authorizes a change. Use the normal proposed-change tools only when the user asks to create or modify learning material.
        """;
    }

    private static string PageCount(int segments, int pageSize) => segments == 0
        ? "no captions"
        : $"{Math.Max(1, (int)Math.Ceiling(segments / (double)pageSize))} page(s), {segments} captions";

    private static string BuildViewedTranscriptPage(TranscriptAssistantContext transcript, int pageSize)
    {
        if (transcript.ViewedPage is not int page)
        {
            return string.Empty;
        }
        var stream = transcript.ViewedStream ?? transcript.Stream;
        var segments = stream == RealtimeTranslationTranscriptStreams.Translation
            ? transcript.TranslationSegmentCount
            : transcript.SourceSegmentCount;
        var total = (int)Math.Ceiling(segments / (double)pageSize);
        if (page < 1 || page > total)
        {
            // Say nothing rather than name a page the user cannot be on.
            return string.Empty;
        }
        return $"""

        - The user is reading page {page} of {total} of the {stream} stream right now. "This page" and "here" mean that page; "the first page" means page 1 of that stream. Call get_saved_transcript with that page number to read it.
        """;
    }

}


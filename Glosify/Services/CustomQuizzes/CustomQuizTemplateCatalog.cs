using Glosify.Models.CustomQuizzes;
using Glosify.Models.Entities;

namespace Glosify.Services.CustomQuizzes;

public interface ICustomQuizTemplateCatalog
{
    IReadOnlyList<CustomQuizTemplateSummary> List();
    IReadOnlyList<CustomQuizTemplateDto> Build(IReadOnlyList<Word> words);
}

public sealed class CustomQuizTemplateCatalog : ICustomQuizTemplateCatalog
{
    private static readonly CustomQuizTemplateSummary[] Templates =
    [
        new("editorial_flow", "Editorial flow", "Quiet hierarchy, generous spacing, and one clear task per row.",
            CustomQuizStylePresets.Editorial, "format_align_left", "Translations, grammar prompts, focused recall",
            "Use a full-width heading and instruction. Pair a 4-column live prompt with a 6-column answer on the same row, leaving deliberate breathing room between them. Keep actions together on the final row."),
        new("aurora_cards", "Aurora cards", "Soft color, paired practice cards, and an energetic classroom feel.",
            CustomQuizStylePresets.Aurora, "auto_awesome", "Short vocabulary sets and visual scanning",
            "Use two 6-column question cards per band. Put each live prompt above its answer in the same half of the grid. Limit the first screen to four questions."),
        new("paper_choices", "Textbook drill", "Compact numbered rows modelled on the photographed workbook exercises.",
            CustomQuizStylePresets.Paper, "description", "Conjugation, transformations, cloze, and formal exercises",
            "Follow a textbook exercise: one short heading, one instruction, then consecutive compact rows. For conjugation, cloze, or transformation, use one text_input per item with its complete stem in the label and put {{blank}} exactly where the compact answer belongs. Never type underscores or dots to draw a blank, and do not add a separate prompt label above the same answer. A live bound cue may sit in 4 columns with its inline answer in the adjacent 6 columns. Number rows when it helps scanning."),
        new("word_bank_lab", "Word-bank lab", "A bold word bank leads the exercise and keeps recall playful.",
            CustomQuizStylePresets.WordLab, "science", "Guided recall and beginner vocabulary",
            "Place the word bank full width directly after the instruction. Pair 4-column prompts with 6-column targeted inputs below it. Keep the bank visually dominant."),
    ];

    public IReadOnlyList<CustomQuizTemplateSummary> List() => Templates;

    public IReadOnlyList<CustomQuizTemplateDto> Build(IReadOnlyList<Word> words) =>
    [
        ToDto(Templates[0], Editorial(words.Take(6).ToList())),
        ToDto(Templates[1], Aurora(words.Take(4).ToList())),
        ToDto(Templates[2], Paper(words.Take(5).ToList())),
        ToDto(Templates[3], WordLab(words.Take(6).ToList())),
    ];

    private static CustomQuizTemplateDto ToDto(CustomQuizTemplateSummary template, CustomQuizDocumentV1 document) =>
        new(template.Id, template.Name, template.Description, template.StylePreset, template.Icon,
            template.BestFor, template.LayoutGuidance, document);

    private static CustomQuizDocumentV1 Editorial(IReadOnlyList<Word> words)
    {
        var blocks = Structure("editorial", "Vocabulary, clearly", "Read the cue, then write the matching word.");
        var row = 3;
        for (var index = 0; index < words.Count; index++, row++)
        {
            blocks.Add(Prompt("editorial", index, words[index], row, 1, 4));
            blocks.Add(Input("editorial", index, words[index], row, 7, 6, $"Answer {index + 1}"));
        }
        AddActions(blocks, "editorial", row);
        return Document(CustomQuizStylePresets.Editorial, blocks);
    }

    private static CustomQuizDocumentV1 Aurora(IReadOnlyList<Word> words)
    {
        var blocks = Structure("aurora", "Make the connection", "Translate each card. Trust your first answer.");
        for (var index = 0; index < words.Count; index++)
        {
            var column = index % 2 == 0 ? 1 : 7;
            var promptRow = 3 + index / 2 * 2;
            blocks.Add(Prompt("aurora", index, words[index], promptRow, column, 6));
            blocks.Add(Input("aurora", index, words[index], promptRow + 1, column, 6, $"Card {index + 1} answer"));
        }
        var actionRow = 3 + (int)Math.Ceiling(words.Count / 2d) * 2;
        AddActions(blocks, "aurora", actionRow);
        return Document(CustomQuizStylePresets.Aurora, blocks);
    }

    private static CustomQuizDocumentV1 Paper(IReadOnlyList<Word> words)
    {
        var blocks = Structure("paper", "Exercise", "Write the matching word on each line.");
        var row = 3;
        for (var index = 0; index < words.Count; index++, row++)
        {
            blocks.Add(Prompt("paper", index, words[index], row, 1, 4));
            blocks.Add(Input("paper", index, words[index], row, 5, 6, $"{index + 1}. — {{{{blank}}}}"));
        }
        AddActions(blocks, "paper", row);
        return Document(CustomQuizStylePresets.Paper, blocks);
    }

    private static CustomQuizDocumentV1 WordLab(IReadOnlyList<Word> words)
    {
        var blocks = Structure("lab", "Build the words", "Use the bank as a hint, then complete every answer.");
        var inputIds = words.Select((_, index) => $"lab-input-{index + 1}").ToList();
        blocks.Add(new CustomQuizBlockV1
        {
            Id = "lab-bank", Type = CustomQuizBlockTypes.WordBank, GridRow = 3, GridColumn = 1, ColumnSpan = 12,
            Label = "Your word bank", TargetInputIds = inputIds,
            Options = words.Select((word, index) => new CustomQuizOptionV1
            {
                Id = $"lab-bank-option-{index + 1}", Binding = Bind(word, CustomQuizWordFields.Lemma),
            }).ToList(),
        });
        var row = 4;
        for (var index = 0; index < words.Count; index++, row++)
        {
            blocks.Add(Prompt("lab", index, words[index], row, 1, 4));
            blocks.Add(Input("lab", index, words[index], row, 7, 6, $"Lab answer {index + 1}"));
        }
        AddActions(blocks, "lab", row);
        return Document(CustomQuizStylePresets.WordLab, blocks);
    }

    private static List<CustomQuizBlockV1> Structure(string prefix, string heading, string instruction) =>
    [
        new() { Id = $"{prefix}-heading", Type = CustomQuizBlockTypes.Heading, GridRow = 1, GridColumn = 1, ColumnSpan = 12, Text = heading },
        new() { Id = $"{prefix}-instruction", Type = CustomQuizBlockTypes.InstructionLabel, GridRow = 2, GridColumn = 1, ColumnSpan = 12, Text = instruction },
    ];

    private static CustomQuizBlockV1 Prompt(string prefix, int index, Word word, int row, int column, int span) => new()
    {
        Id = $"{prefix}-prompt-{index + 1}", Type = CustomQuizBlockTypes.PromptLabel,
        GridRow = row, GridColumn = column, ColumnSpan = span,
        Binding = Bind(word, CustomQuizWordFields.Translation),
    };

    private static CustomQuizBlockV1 Input(string prefix, int index, Word word, int row, int column, int span, string label) => new()
    {
        Id = $"{prefix}-input-{index + 1}", Type = CustomQuizBlockTypes.TextInput,
        GridRow = row, GridColumn = column, ColumnSpan = span, Label = label,
        ExpectedBinding = Bind(word, CustomQuizWordFields.Lemma),
    };

    private static CustomQuizWordBindingV1 Bind(Word word, string field) => new() { WordId = word.Id, Field = field };

    private static void AddActions(List<CustomQuizBlockV1> blocks, string prefix, int row)
    {
        blocks.Add(new CustomQuizBlockV1
        {
            Id = $"{prefix}-submit", Type = CustomQuizBlockTypes.SubmitButton,
            GridRow = row, GridColumn = 1, ColumnSpan = 4, Text = "Check my answers",
        });
        blocks.Add(new CustomQuizBlockV1
        {
            Id = $"{prefix}-feedback", Type = CustomQuizBlockTypes.FeedbackMessage,
            GridRow = row, GridColumn = 7, ColumnSpan = 6,
        });
    }

    private static CustomQuizDocumentV1 Document(string style, List<CustomQuizBlockV1> blocks)
    {
        for (var index = 0; index < blocks.Count; index++) blocks[index].Order = index;
        return new() { StylePreset = style, Blocks = blocks };
    }
}

using System.Text.Json;
using Glosify.Data;
using Glosify.Models.CustomQuizzes;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.CustomQuizzes;

public sealed class CustomQuizService : ICustomQuizService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly int[] AllowedSpans = [3, 4, 6, 12];
    private readonly GlosifyContext _context;
    private readonly CollectionVisibility _collectionVisibility;

    public CustomQuizService(GlosifyContext context)
    {
        _context = context;
        _collectionVisibility = new CollectionVisibility(context);
    }

    public async Task<IReadOnlyList<CustomQuizSummaryDto>> ListForQuizAsync(Guid quizId, bool playableOnly = false, CancellationToken cancellationToken = default)
    {
        var query = _context.CustomQuizzes.AsNoTracking().Where(item => item.QuizId == quizId);
        if (playableOnly) query = query.Where(item => item.IsPlayable);
        return await query.OrderBy(item => item.Name)
            .Select(item => new CustomQuizSummaryDto(item.Id, item.QuizId, item.Name, item.IsPlayable, item.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<CustomQuizSummaryDto>>> ListForQuizzesAsync(
        IReadOnlyCollection<Guid> quizIds,
        bool playableOnly = false,
        CancellationToken cancellationToken = default)
    {
        var distinctQuizIds = quizIds.Distinct().ToList();
        if (distinctQuizIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<CustomQuizSummaryDto>>();
        }

        var query = _context.CustomQuizzes
            .AsNoTracking()
            .Where(item => distinctQuizIds.Contains(item.QuizId));
        if (playableOnly)
        {
            query = query.Where(item => item.IsPlayable);
        }

        var summaries = await query
            .OrderBy(item => item.Name)
            .Select(item => new CustomQuizSummaryDto(item.Id, item.QuizId, item.Name, item.IsPlayable, item.UpdatedAt))
            .ToListAsync(cancellationToken);

        return summaries
            .GroupBy(item => item.QuizId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CustomQuizSummaryDto>)group.ToList());
    }

    public async Task<CustomQuizEditorDto?> GetForEditorAsync(Guid id, string userId, CancellationToken cancellationToken = default)
    {
        var entity = await _context.CustomQuizzes
            .Include(item => item.Quiz)
            .FirstOrDefaultAsync(item => item.Id == id && item.Quiz.UserId == userId, cancellationToken);
        if (entity == null) return null;
        var words = await LoadWordsAsync(entity.QuizId, cancellationToken);
        var canPersistNormalization = TryDeserialize(entity.DefinitionJson, out var document);
        var validation = Validate(document, words);

        // Persist safe schema normalization when an older or generated document is
        // opened. This repairs presentation-only omissions such as answer labels and
        // keeps the stored playability flag aligned with the normalized document.
        var normalizedJson = JsonSerializer.Serialize(document, JsonOptions);
        if (canPersistNormalization
            && validation.IsStructurallyValid
            && (!string.Equals(normalizedJson, entity.DefinitionJson, StringComparison.Ordinal)
                || entity.IsPlayable != validation.IsPlayable))
        {
            entity.DefinitionJson = normalizedJson;
            entity.IsPlayable = validation.IsPlayable;
            await _context.SaveChangesAsync(cancellationToken);
        }

        return new CustomQuizEditorDto(entity.Id, entity.QuizId, entity.Name, document, entity.IsPlayable,
            validation.StructuralErrors.Concat(validation.PlayabilityErrors).ToList(),
            entity.RowVersion.Length == 0 ? string.Empty : Convert.ToBase64String(entity.RowVersion));
    }

    public async Task<CustomQuizEditorDto> CreateAsync(SaveCustomQuizRequest request, string userId, CancellationToken cancellationToken = default)
    {
        var quizExists = await _context.Quizzes.AnyAsync(q => q.Id == request.QuizId && q.UserId == userId, cancellationToken);
        if (!quizExists) throw new QuizNotFoundException();
        var name = ValidateName(request.Name);
        if (await _context.CustomQuizzes.AnyAsync(item => item.QuizId == request.QuizId && item.Name == name, cancellationToken))
            throw new CustomQuizValidationException(["A custom quiz with this name already exists."]);

        var words = await LoadWordsAsync(request.QuizId, cancellationToken);
        Normalize(request.Document);
        var validation = Validate(request.Document, words);
        if (!validation.IsStructurallyValid) throw new CustomQuizValidationException(validation.StructuralErrors);
        var now = DateTimeOffset.UtcNow;
        var entity = new CustomQuiz
        {
            Id = Guid.NewGuid(), QuizId = request.QuizId, Name = name,
            DefinitionJson = JsonSerializer.Serialize(request.Document, JsonOptions),
            SchemaVersion = 1, IsPlayable = validation.IsPlayable, CreatedAt = now, UpdatedAt = now
        };
        _context.CustomQuizzes.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);
        return MapEditor(entity, validation);
    }

    public async Task<CustomQuizEditorDto?> UpdateAsync(Guid id, SaveCustomQuizRequest request, string userId, CancellationToken cancellationToken = default)
    {
        var entity = await _context.CustomQuizzes.Include(item => item.Quiz)
            .FirstOrDefaultAsync(item => item.Id == id && item.Quiz.UserId == userId, cancellationToken);
        if (entity == null) return null;
        if (entity.RowVersion.Length > 0 && string.IsNullOrWhiteSpace(request.RowVersion))
            throw new CustomQuizConcurrencyException();
        if (!string.IsNullOrWhiteSpace(request.RowVersion))
        {
            byte[] supplied;
            try { supplied = Convert.FromBase64String(request.RowVersion); }
            catch (FormatException) { throw new CustomQuizConcurrencyException(); }
            if (entity.RowVersion.Length > 0 && !entity.RowVersion.SequenceEqual(supplied))
                throw new CustomQuizConcurrencyException();
            _context.Entry(entity).Property(item => item.RowVersion).OriginalValue = supplied;
        }

        var name = ValidateName(request.Name);
        if (await _context.CustomQuizzes.AnyAsync(item => item.QuizId == entity.QuizId && item.Id != id && item.Name == name, cancellationToken))
            throw new CustomQuizValidationException(["A custom quiz with this name already exists."]);
        var words = await LoadWordsAsync(entity.QuizId, cancellationToken);
        Normalize(request.Document);
        var validation = Validate(request.Document, words);
        if (!validation.IsStructurallyValid) throw new CustomQuizValidationException(validation.StructuralErrors);
        entity.Name = name;
        entity.DefinitionJson = JsonSerializer.Serialize(request.Document, JsonOptions);
        entity.SchemaVersion = 1;
        entity.IsPlayable = validation.IsPlayable;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        try { await _context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new CustomQuizConcurrencyException(); }
        return MapEditor(entity, validation);
    }

    public async Task<bool> DeleteAsync(Guid id, string userId, CancellationToken cancellationToken = default)
    {
        var entity = await _context.CustomQuizzes.Include(item => item.Quiz)
            .FirstOrDefaultAsync(item => item.Id == id && item.Quiz.UserId == userId, cancellationToken);
        if (entity == null) return false;
        _context.CustomQuizzes.Remove(entity);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<CustomQuizPlayData?> GetForPlayAsync(Guid id, string userId, Guid? classroomId = null, CancellationToken cancellationToken = default)
    {
        var entity = await _context.CustomQuizzes.AsNoTracking().Include(item => item.Quiz)
            .FirstOrDefaultAsync(item => item.Id == id && item.IsPlayable, cancellationToken);
        if (entity == null || !await CanPlayAsync(entity.Quiz, userId, classroomId, cancellationToken)) return null;
        var words = await LoadWordsAsync(entity.QuizId, cancellationToken);
        var document = Deserialize(entity.DefinitionJson);
        var validation = Validate(document, words);
        if (!validation.IsPlayable) return null;
        return new CustomQuizPlayData(entity.Id, entity.QuizId, entity.Name, entity.Quiz.Name,
            entity.Quiz.SourceLanguage, entity.Quiz.TargetLanguage, Guid.NewGuid(),
            SanitizeForPlayer(document), ResolveValues(document, words), classroomId);
    }

    public async Task<CustomQuizGradeResult?> GradeAsync(Guid id, GradeCustomQuizRequest request, string userId, CancellationToken cancellationToken = default)
    {
        var prior = await _context.QuizAttempts.AsNoTracking().Include(attempt => attempt.Items)
            .FirstOrDefaultAsync(attempt => attempt.Id == request.AttemptId && attempt.UserId == userId, cancellationToken);
        if (prior != null)
        {
            var priorBlocks = prior.Items.OrderBy(item => item.Sequence).Select(item => new CustomQuizBlockGrade(
                item.Prompt, item.IsCorrect ? "correct" : "incorrect",
                item.IsCorrect ? "Correct." : $"Correct answer: {item.ExpectedAnswer}", [item.ExpectedAnswer])).ToList();
            return BuildGradeResult(priorBlocks);
        }

        var entity = await _context.CustomQuizzes.AsNoTracking().Include(item => item.Quiz)
            .FirstOrDefaultAsync(item => item.Id == id && item.IsPlayable, cancellationToken);
        if (entity == null || !await CanPlayAsync(entity.Quiz, userId, request.ClassroomId, cancellationToken)) return null;
        var words = await LoadWordsAsync(entity.QuizId, cancellationToken);
        var document = Deserialize(entity.DefinitionJson);
        if (!Validate(document, words).IsPlayable) return null;
        var supplied = request.Answers
            .GroupBy(answer => answer.BlockId)
            .ToDictionary(group => group.Key, group => group.Last().Values, StringComparer.Ordinal);
        var answerBlocks = document.Blocks.Where(block => CustomQuizBlockTypes.IsAnswer(block.Type)).OrderBy(block => block.Order).ToList();
        var incomplete = answerBlocks.Where(block => !supplied.TryGetValue(block.Id, out var values) || values.Count == 0 || values.All(string.IsNullOrWhiteSpace)).ToList();
        if (incomplete.Count > 0)
        {
            return new CustomQuizGradeResult("incomplete", 0, answerBlocks.Count, 0,
                incomplete.Select(block => new CustomQuizBlockGrade(block.Id, "incomplete", "Answer this question.", [])).ToList());
        }

        var grades = answerBlocks.Select(block => GradeBlock(block, supplied[block.Id], words)).ToList();
        var attempt = new QuizAttempt
        {
            Id = request.AttemptId == Guid.Empty ? Guid.NewGuid() : request.AttemptId,
            QuizId = entity.QuizId,
            UserId = userId,
            ClassroomId = request.ClassroomId,
            Mode = Truncate($"Custom · {entity.Name}", 200),
            TotalItems = grades.Count,
            CorrectCount = grades.Count(grade => grade.State == "correct"),
            IncorrectCount = grades.Count(grade => grade.State == "incorrect"),
            SkippedCount = 0,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Items = grades.Select((grade, index) => new QuizAttemptItem
            {
                Id = Guid.NewGuid(), Sequence = index, Prompt = grade.BlockId,
                ExpectedAnswer = Truncate(string.Join(", ", grade.CorrectValues), 512),
                GivenAnswer = Truncate(string.Join(", ", supplied[grade.BlockId]), 512),
                IsCorrect = grade.State == "correct"
            }).ToList()
        };
        _context.QuizAttempts.Add(attempt);
        try { await _context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            if (!await _context.QuizAttempts.AnyAsync(item => item.Id == attempt.Id, cancellationToken)) throw;
        }
        return BuildGradeResult(grades);
    }

    public async Task PruneWordBindingsAsync(Guid quizId, IReadOnlyCollection<string> wordIds, CancellationToken cancellationToken = default)
    {
        if (wordIds.Count == 0) return;
        var removed = wordIds.ToHashSet(StringComparer.Ordinal);
        var entities = await _context.CustomQuizzes.Where(item => item.QuizId == quizId).ToListAsync(cancellationToken);
        var remainingWords = await LoadWordsAsync(quizId, cancellationToken);
        foreach (var entity in entities)
        {
            var document = Deserialize(entity.DefinitionJson);
            var removedBlockIds = document.Blocks
                .Where(block => References(block.Binding, removed) || References(block.ExpectedBinding, removed))
                .Select(block => block.Id).ToHashSet(StringComparer.Ordinal);
            document.Blocks.RemoveAll(block => removedBlockIds.Contains(block.Id));
            foreach (var block in document.Blocks)
            {
                block.Options.RemoveAll(option => References(option.Binding, removed));
                block.TargetInputIds.RemoveAll(target => removedBlockIds.Contains(target));
            }
            document.Blocks.RemoveAll(block =>
                CustomQuizBlockTypes.IsChoice(block.Type) && (block.Options.Count < 2 || !block.Options.Any(option => option.IsCorrect))
                || block.Type == CustomQuizBlockTypes.WordBank && (block.Options.Count == 0 || block.TargetInputIds.Count == 0));
            Normalize(document);
            var validation = Validate(document, remainingWords);
            entity.DefinitionJson = JsonSerializer.Serialize(document, JsonOptions);
            entity.IsPlayable = validation.IsPlayable;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public async Task CloneForCopiedQuizAsync(Guid sourceQuizId, Guid targetQuizId, IReadOnlyDictionary<string, string> wordIdMap, CancellationToken cancellationToken = default)
    {
        var sourceItems = await _context.CustomQuizzes.AsNoTracking()
            .Where(item => item.QuizId == sourceQuizId)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var source in sourceItems)
        {
            var document = RemapWordBindings(Deserialize(source.DefinitionJson), wordIdMap);
            _context.CustomQuizzes.Add(new CustomQuiz
            {
                Id = Guid.NewGuid(), QuizId = targetQuizId, Name = source.Name,
                DefinitionJson = JsonSerializer.Serialize(document, JsonOptions),
                SchemaVersion = 1, IsPlayable = source.IsPlayable, CreatedAt = now, UpdatedAt = now
            });
        }
    }

    public CustomQuizDocumentV1 RemapWordBindings(CustomQuizDocumentV1 document, IReadOnlyDictionary<string, string> wordIdMap)
    {
        var copy = Deserialize(JsonSerializer.Serialize(document, JsonOptions));
        foreach (var block in copy.Blocks)
        {
            Remap(block.Binding, wordIdMap);
            Remap(block.ExpectedBinding, wordIdMap);
            foreach (var option in block.Options) Remap(option.Binding, wordIdMap);
        }
        return copy;
    }

    public CustomQuizValidationResult Validate(CustomQuizDocumentV1 document, IReadOnlyDictionary<string, Word> words)
    {
        var structural = new List<string>();
        var playable = new List<string>();
        if (document.SchemaVersion != 1) structural.Add("Only custom quiz schema version 1 is supported.");
        if (document.Blocks.Count > 200) structural.Add("A custom quiz can contain at most 200 blocks.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in document.Blocks)
        {
            var prefix = string.IsNullOrWhiteSpace(block.Id) ? "Block" : $"Block {block.Id}";
            if (string.IsNullOrWhiteSpace(block.Id) || !ids.Add(block.Id)) structural.Add($"{prefix} must have a unique id.");
            if (!CustomQuizBlockTypes.All.Contains(block.Type)) structural.Add($"{prefix} has an unsupported type.");
            if (!AllowedSpans.Contains(block.ColumnSpan)) structural.Add($"{prefix} has an unsupported width.");
            if (block.GridColumn is < 1 or > 12 || block.GridColumn + block.ColumnSpan > 13)
                structural.Add($"{prefix} has an invalid grid column.");
            if (block.GridRow is < 1 or > 500) structural.Add($"{prefix} has an invalid grid row.");
            ValidateBinding(block.Binding, prefix, words, structural);
            ValidateBinding(block.ExpectedBinding, prefix, words, structural);
            if (block.Options.Count > 100) structural.Add($"{prefix} can contain at most 100 options.");
            var optionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in block.Options)
            {
                if (string.IsNullOrWhiteSpace(option.Id) || !optionIds.Add(option.Id)) structural.Add($"{prefix} has an option without a unique id.");
                ValidateBinding(option.Binding, prefix, words, structural);
            }
            if ((block.Text?.Length ?? 0) > 2000
                || (block.Label?.Length ?? 0) > 2000
                || (block.ExpectedText?.Length ?? 0) > 2000)
                structural.Add($"{prefix} text is too long.");
        }
        foreach (var row in document.Blocks.GroupBy(block => block.GridRow))
        {
            var placed = row.OrderBy(block => block.GridColumn).ToList();
            for (var index = 1; index < placed.Count; index++)
            {
                var previous = placed[index - 1];
                if (placed[index].GridColumn < previous.GridColumn + previous.ColumnSpan)
                    structural.Add($"Blocks {previous.Id} and {placed[index].Id} overlap on the canvas.");
            }
        }
        foreach (var bank in document.Blocks.Where(block => block.Type == CustomQuizBlockTypes.WordBank))
        {
            foreach (var target in bank.TargetInputIds)
            {
                var block = document.Blocks.FirstOrDefault(item => item.Id == target);
                if (block == null || block.Type is not (CustomQuizBlockTypes.TextInput or CustomQuizBlockTypes.Textarea))
                    structural.Add($"Word bank {bank.Id} targets an unknown text input.");
            }
        }

        if (structural.Count == 0)
        {
            if (document.Blocks.Count(block => block.Type == CustomQuizBlockTypes.SubmitButton) != 1) playable.Add("Add exactly one submit button.");
            if (document.Blocks.Count(block => block.Type == CustomQuizBlockTypes.FeedbackMessage) != 1) playable.Add("Add exactly one feedback message.");
            var answers = document.Blocks.Where(block => CustomQuizBlockTypes.IsAnswer(block.Type)).ToList();
            if (answers.Count == 0) playable.Add("Add at least one answer control.");
            foreach (var block in answers)
            {
                if (string.IsNullOrWhiteSpace(block.Label)) playable.Add($"Answer block {block.Id} needs a label.");
                if (block.Type is CustomQuizBlockTypes.TextInput or CustomQuizBlockTypes.Textarea
                    && block.ExpectedBinding == null
                    && string.IsNullOrWhiteSpace(block.ExpectedText))
                    playable.Add($"Answer block {block.Id} needs an expected answer.");
                if (CustomQuizBlockTypes.IsChoice(block.Type))
                {
                    if (block.Options.Count < 2) playable.Add($"Choice block {block.Id} needs at least two options.");
                    var correct = block.Options.Count(option => option.IsCorrect);
                    if (block.Type == CustomQuizBlockTypes.MultiSelectGroup ? correct < 1 : correct != 1)
                        playable.Add($"Choice block {block.Id} has an invalid correct-option selection.");
                }
            }
            var duplicateLabels = answers
                .Where(block => !string.IsNullOrWhiteSpace(block.Label))
                .GroupBy(block => block.Label!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => $"\"{group.Key}\"")
                .ToList();
            if (duplicateLabels.Count > 0)
                playable.Add($"Answer controls need distinct question labels. Repeated: {string.Join(", ", duplicateLabels)}.");
            foreach (var bank in document.Blocks.Where(block => block.Type == CustomQuizBlockTypes.WordBank))
            {
                if (bank.Options.Count == 0 || bank.TargetInputIds.Count == 0) playable.Add($"Word bank {bank.Id} needs options and a target input.");
            }
        }
        return new CustomQuizValidationResult(structural.Count == 0, structural.Count == 0 && playable.Count == 0, structural, playable);
    }

    private async Task<bool> CanPlayAsync(Quiz quiz, string userId, Guid? classroomId, CancellationToken cancellationToken)
    {
        if (quiz.UserId == userId) return true;
        if (classroomId.HasValue)
        {
            var member = await _context.ClassroomMemberships.AnyAsync(item => item.ClassroomId == classroomId && item.UserId == userId, cancellationToken);
            var shared = await _context.ClassroomContents.AnyAsync(item => item.ClassroomId == classroomId && item.QuizId == quiz.Id, cancellationToken);
            if (member && shared) return true;
        }
        return quiz.IsPublic || quiz.CollectionId.HasValue && await _collectionVisibility.IsCollectionPubliclyReadableAsync(quiz.CollectionId.Value, cancellationToken);
    }

    private static CustomQuizBlockGrade GradeBlock(CustomQuizBlockV1 block, IReadOnlyList<string> supplied, IReadOnlyDictionary<string, Word> words)
    {
        IReadOnlyList<string> correct;
        bool isCorrect;
        if (block.Type is CustomQuizBlockTypes.TextInput or CustomQuizBlockTypes.Textarea)
        {
            correct = [string.IsNullOrWhiteSpace(block.ExpectedText)
                ? Resolve(block.ExpectedBinding!, words)
                : block.ExpectedText.Trim()];
            isCorrect = supplied[0].Trim().Equals(correct[0].Trim(), StringComparison.OrdinalIgnoreCase);
        }
        else if (block.Type == CustomQuizBlockTypes.Checkbox)
        {
            correct = [block.ExpectedChecked ? "true" : "false"];
            isCorrect = bool.TryParse(supplied[0], out var value) && value == block.ExpectedChecked;
        }
        else
        {
            correct = block.Options.Where(option => option.IsCorrect).Select(option => option.Id).Order().ToList();
            isCorrect = supplied.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().Order().SequenceEqual(correct);
        }
        return new CustomQuizBlockGrade(block.Id, isCorrect ? "correct" : "incorrect",
            isCorrect ? "Correct." : $"Correct answer: {string.Join(", ", CorrectLabels(block, correct, words))}",
            CorrectLabels(block, correct, words));
    }

    private static IReadOnlyList<string> CorrectLabels(CustomQuizBlockV1 block, IReadOnlyList<string> correct, IReadOnlyDictionary<string, Word> words)
    {
        if (!CustomQuizBlockTypes.IsChoice(block.Type)) return correct;
        return block.Options.Where(option => correct.Contains(option.Id)).Select(option => Resolve(option.Binding, words)).ToList();
    }

    private static CustomQuizGradeResult BuildGradeResult(IReadOnlyList<CustomQuizBlockGrade> grades)
    {
        var correct = grades.Count(grade => grade.State == "correct");
        return new CustomQuizGradeResult(correct == grades.Count ? "correct" : "incorrect", correct, grades.Count,
            grades.Count == 0 ? 0 : (int)Math.Round(correct * 100d / grades.Count), grades);
    }

    private static CustomQuizDocumentV1 SanitizeForPlayer(CustomQuizDocumentV1 source)
    {
        var copy = Deserialize(JsonSerializer.Serialize(source, JsonOptions));
        foreach (var block in copy.Blocks)
        {
            block.ExpectedBinding = null;
            block.ExpectedText = null;
            block.ExpectedChecked = false;
            foreach (var option in block.Options) option.IsCorrect = false;
        }
        return copy;
    }

    private static Dictionary<string, string> ResolveValues(CustomQuizDocumentV1 document, IReadOnlyDictionary<string, Word> words)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var block in document.Blocks)
        {
            if (block.Binding != null) values[$"block:{block.Id}"] = Resolve(block.Binding, words);
            foreach (var option in block.Options) values[$"option:{block.Id}:{option.Id}"] = Resolve(option.Binding, words);
        }
        return values;
    }

    private static string Resolve(CustomQuizWordBindingV1 binding, IReadOnlyDictionary<string, Word> words)
    {
        if (!words.TryGetValue(binding.WordId, out var word)) return string.Empty;
        return binding.Field == CustomQuizWordFields.Translation ? word.Translation : word.Lemma;
    }

    private async Task<Dictionary<string, Word>> LoadWordsAsync(Guid quizId, CancellationToken cancellationToken) =>
        await _context.Words.AsNoTracking().Where(word => word.QuizId == quizId).ToDictionaryAsync(word => word.Id, cancellationToken);

    private static void ValidateBinding(CustomQuizWordBindingV1? binding, string prefix, IReadOnlyDictionary<string, Word> words, List<string> errors)
    {
        if (binding == null) return;
        if (!CustomQuizWordFields.IsValid(binding.Field)) errors.Add($"{prefix} uses an unsupported word field.");
        if (string.IsNullOrWhiteSpace(binding.WordId) || !words.ContainsKey(binding.WordId)) errors.Add($"{prefix} references a word outside this quiz.");
    }

    private static bool References(CustomQuizWordBindingV1? binding, HashSet<string> ids) => binding != null && ids.Contains(binding.WordId);
    private static void Remap(CustomQuizWordBindingV1? binding, IReadOnlyDictionary<string, string> map)
    {
        if (binding != null && map.TryGetValue(binding.WordId, out var mapped)) binding.WordId = mapped;
    }

    private static string ValidateName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 160) throw new CustomQuizValidationException(["Name must contain between 1 and 160 characters."]);
        return name;
    }

    private static void Normalize(CustomQuizDocumentV1 document)
    {
        document.SchemaVersion = 1;
        if (!CustomQuizStylePresets.All.Contains(document.StylePreset))
            document.StylePreset = CustomQuizStylePresets.Editorial;
        document.Blocks = document.Blocks.OrderBy(block => block.Order).ThenBy(block => block.Id).ToList();
        var occupied = new Dictionary<int, bool[]>();
        foreach (var block in document.Blocks)
        {
            if (CustomQuizBlockTypes.IsAnswer(block.Type) && string.IsNullOrWhiteSpace(block.Label))
            {
                block.Label = !string.IsNullOrWhiteSpace(block.Text) ? block.Text.Trim() : null;
            }
            if (!AllowedSpans.Contains(block.ColumnSpan)) continue;
            var preferredRow = block.GridRow is >= 1 and <= 500 ? block.GridRow : 1;
            var preferredColumn = Math.Clamp(block.GridColumn <= 0 ? 1 : block.GridColumn, 1, 13 - block.ColumnSpan);
            var placement = FindPlacement(occupied, preferredRow, preferredColumn, block.ColumnSpan);
            block.GridRow = placement.Row;
            block.GridColumn = placement.Column;
            MarkOccupied(occupied, placement.Row, placement.Column, block.ColumnSpan);
        }
        document.Blocks = document.Blocks.OrderBy(block => block.GridRow).ThenBy(block => block.GridColumn).ThenBy(block => block.Order).ToList();
        for (var index = 0; index < document.Blocks.Count; index++) document.Blocks[index].Order = index;
    }

    private static (int Row, int Column) FindPlacement(Dictionary<int, bool[]> occupied, int preferredRow, int preferredColumn, int span)
    {
        for (var row = preferredRow; row <= 500; row++)
        {
            var columns = Enumerable.Range(1, 13 - span)
                .OrderBy(column => Math.Abs(column - preferredColumn));
            foreach (var column in columns)
            {
                if (!IsOccupied(occupied, row, column, span)) return (row, column);
            }
        }
        return (500, 1);
    }

    private static bool IsOccupied(Dictionary<int, bool[]> occupied, int row, int column, int span)
    {
        if (!occupied.TryGetValue(row, out var cells)) return false;
        return Enumerable.Range(column - 1, span).Any(index => cells[index]);
    }

    private static void MarkOccupied(Dictionary<int, bool[]> occupied, int row, int column, int span)
    {
        if (!occupied.TryGetValue(row, out var cells)) occupied[row] = cells = new bool[12];
        foreach (var index in Enumerable.Range(column - 1, span)) cells[index] = true;
    }

    private static CustomQuizDocumentV1 Deserialize(string json)
    {
        TryDeserialize(json, out var document);
        return document;
    }

    private static bool TryDeserialize(string json, out CustomQuizDocumentV1 document)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<CustomQuizDocumentV1>(json, JsonOptions);
            if (parsed == null)
            {
                document = new();
                return false;
            }
            document = parsed;
            Normalize(document);
            return true;
        }
        catch (JsonException)
        {
            document = new();
            return false;
        }
    }

    private static CustomQuizEditorDto MapEditor(CustomQuiz entity, CustomQuizValidationResult validation) =>
        new(entity.Id, entity.QuizId, entity.Name, Deserialize(entity.DefinitionJson), entity.IsPlayable,
            validation.StructuralErrors.Concat(validation.PlayabilityErrors).ToList(),
            entity.RowVersion.Length == 0 ? string.Empty : Convert.ToBase64String(entity.RowVersion));

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

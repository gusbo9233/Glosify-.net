namespace Glosify.Services.Ai.Assistant.Runtime;

internal static class AssistantRunChangeState
{
    public static void RemoveDeletedResources(AssistantRunState state, IReadOnlyList<AppliedChange> journal)
    {
        foreach (var entry in journal)
        {
            if (!Guid.TryParse(entry.EntityId, out var id)) continue;
            if (entry.Kind == "delete_quiz")
            {
                state.Fingerprints.Remove(id);
                state.Placements.Remove(id);
                if (state.Facts?.QuizId == id) state.Facts = state.Facts with { QuizId = null };
            }
            else if (entry.Kind == "anki_delete" && state.Facts?.AnkiCollectionId == id)
                state.Facts = state.Facts with { AnkiCollectionId = null };
        }
    }
}

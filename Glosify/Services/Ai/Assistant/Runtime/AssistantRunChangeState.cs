namespace Glosify.Services.Ai.Assistant.Runtime;

internal static class AssistantRunChangeState
{
    public static void RemoveDeletedResources(AssistantRunState state, IReadOnlyList<AppliedChange> journal)
    {
        foreach (var entry in journal)
        {
            if (entry.Kind != "delete_quiz" || !Guid.TryParse(entry.EntityId, out var id)) continue;
            state.Fingerprints.Remove(id);
            state.Placements.Remove(id);
            if (state.Facts?.QuizId == id) state.Facts = state.Facts with { QuizId = null };
        }
    }
}

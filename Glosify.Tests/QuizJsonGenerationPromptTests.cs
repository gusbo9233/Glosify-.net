using Glosify.Services.Quizzes;
using Xunit;

namespace Glosify.Tests;

public sealed class QuizJsonGenerationPromptTests
{
    [Fact]
    public void Language_prompt_carries_the_display_language_and_destination()
    {
        var prompt = QuizJsonGenerationPrompt.Build(false, "Polish", "Travel");

        Assert.Contains("learner studying Polish", prompt, StringComparison.Ordinal);
        Assert.Contains("placed in Travel", prompt, StringComparison.Ordinal);
        Assert.Contains("\"version\": 1", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Freestyle_prompt_uses_prompt_answer_shape_without_sentences()
    {
        var prompt = QuizJsonGenerationPrompt.Build(true, "Freestyle", "Top level");

        Assert.Contains("source_language \"Freestyle\"", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not include sentences", prompt, StringComparison.Ordinal);
        Assert.Contains("placed in Top level", prompt, StringComparison.Ordinal);
    }
}

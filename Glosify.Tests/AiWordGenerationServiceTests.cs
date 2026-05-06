using Glosify.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public class AiWordGenerationServiceTests
{
    [Fact]
    public void ValidateResponse_RejectsDirtyVocabularyArtifacts()
    {
        var service = new AiWordGenerationService(
            Options.Create(new GeminiOptions()),
            NullLogger<AiWordGenerationService>.Instance);

        var json = """
        {
          "angielsku": {
            "translation": "English (language)",
            "example_sentence": "Czy ktoś mówi po (angielsku)?",
            "example_sentence_translation": "Does anyone speak English?"
          }
        }
        """;

        Assert.False(service.ValidateResponse(json));
    }
}

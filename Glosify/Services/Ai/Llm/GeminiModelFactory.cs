using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Mscc.GenerativeAI;

namespace Glosify.Services.Ai.Llm;

public interface IGeminiModelFactory
{
    GenerativeModel GetModel(string modelName, bool jsonMode);
}

/// <summary>
/// Singleton owner of the GoogleAI client and configured GenerativeModel instances.
/// GeminiGenerativeAiClient itself is scoped (it charges the per-request credit
/// service), so the caches live here to survive across requests.
/// </summary>
public sealed class GeminiModelFactory : IGeminiModelFactory
{
    private readonly GeminiOptions _options;
    private readonly Lazy<GoogleAI> _googleAi;
    private readonly ConcurrentDictionary<(string Model, bool Json), GenerativeModel> _models = new();

    public GeminiModelFactory(IOptions<GeminiOptions> options)
    {
        _options = options.Value;
        _googleAi = new Lazy<GoogleAI>(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                throw new InvalidOperationException(
                    "Gemini API key is not configured. Set Gemini:ApiKey (user-secrets) or the GEMINI_API_KEY environment variable.");
            }
            return new GoogleAI(apiKey: _options.ApiKey);
        });
    }

    public GenerativeModel GetModel(string modelName, bool jsonMode)
    {
        return _models.GetOrAdd((modelName, jsonMode), key =>
        {
            var model = _googleAi.Value.GenerativeModel(model: key.Model);
            model.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
            if (key.Json)
            {
                model.UseJsonMode = true;
            }
            return model;
        });
    }
}

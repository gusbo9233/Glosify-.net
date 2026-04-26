static class Prompts
{
    

    public static string ExtractWordsPrompt(string input)
    {
        return $"Extract the words from the following input: {input}. Do not include any punctuation or special characters, only the words. Only include words once. Do not include names";
    }

    public static string WordExtractionPrompt(string input, string knownLanguage, string targetLanguage)
    {
        return $@"
        The user knows {knownLanguage} and is learning {targetLanguage}.
        Extract key vocabulary from the text below and return a JSON object.
        The text may be in any language — use it only to identify the relevant concepts and vocabulary topics.

        Rules:
        - Output MUST be valid JSON only. No explanations, no extra text.
        - Each key is a {targetLanguage} word or phrase relevant to the text's topic.
        - Each value is an object with:
        - ""translation"": the {knownLanguage} translation of the word
        - ""example_sentence"": a natural example sentence in {targetLanguage} using the word
        - ""example_sentence_translation"": the {knownLanguage} translation of the example sentence

        Format:
        {{
        ""word1"": {{
            ""translation"": ""..."",
            ""example_sentence"": ""..."",
            ""example_sentence_translation"": ""...""
        }}
        }}

        Text:
        {input}";
    }
}
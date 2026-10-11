using System.Text.Json;
using Glosify.Services.Avatar;
using Xunit;

namespace Glosify.Tests;

public sealed class AvatarReplyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OnlyRequestedHelpIsGenerated(bool translate, bool suggest)
    {
        var request = AvatarReply.Request("Speak Swedish.", [], translate, suggest);
        Assert.Empty(request.Tools);
        if (!translate && !suggest)
        {
            Assert.Null(request.ResponseSchema);
            Assert.Equal(512, request.MaxOutputTokens);
            Assert.Equal("Speak Swedish.", request.SystemInstruction);
            return;
        }
        using var schema = JsonDocument.Parse(JsonSerializer.Serialize(request.ResponseSchema!.Schema));
        var expected = new List<string> { "original" };
        if (translate) expected.Add("english");
        if (suggest) expected.Add("suggestion");
        if (translate && suggest) expected.Add("suggestionEnglish");
        Assert.Equal(expected, schema.RootElement.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(expected, schema.RootElement.GetProperty("properties").EnumerateObject().Select(x => x.Name));
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(1200, request.MaxOutputTokens);
        if (suggest) Assert.Contains("do not invent personal facts", request.SystemInstruction);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"original\":42}")]
    [InlineData("{\"original\":\"  \"}")]
    public void InvalidStructuredSpeechCannotBeSentToTheVoiceProvider(string response)
    {
        Assert.Equal(502, Assert.Throws<AvatarException>(() => AvatarReply.Parse(response, true, true)).Status);
    }

    [Fact]
    public void OptionalFieldsCannotDiscardGoodSpeechOrExposeDisabledHelp()
    {
        const string response = """{"original":" Hej! ","english":" Hi! ","suggestion":" Och du? ","suggestionEnglish":" And you? "}""";
        Assert.Equal(new AvatarReply("Hej!", "Hi!", "Och du?", "And you?"), AvatarReply.Parse(response, true, true));
        Assert.Equal(new AvatarReply("Hej!", Suggestion: "Och du?"), AvatarReply.Parse(response, false, true));
        Assert.Equal(new AvatarReply("Hej!", "Hi!"), AvatarReply.Parse(response, true, false));
        Assert.Equal(new AvatarReply("Hej!"), AvatarReply.Parse("""{"original":"Hej!","english":42,"suggestion":null}""", true, true));
        Assert.Null(AvatarReply.Parse(JsonSerializer.Serialize(new { original = "Hej!", english = new string('a', 1001) }), true, false).Translation);
        Assert.Equal(502, Assert.Throws<AvatarException>(() => AvatarReply.Parse(JsonSerializer.Serialize(new { original = new string('a', 4001) }), true, false)).Status);
    }
}

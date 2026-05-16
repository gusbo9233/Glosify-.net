using Glosify.Services;
using Xunit;

namespace Glosify.Tests;

public class VocabularyInputCleanerTests
{
    [Theory]
    [InlineData("m/f pol Czy mówisz", "Czy mówisz")]
    [InlineData("Czy ktoś mówi po (angielsku)?", "Czy ktoś mówi po angielsku?")]
    [InlineData("Czy pan/pani mówi chi pan/pa-nee moo-vee", "Czy pan mówi\nCzy pani mówi")]
    [InlineData("Czy pan/pani mówi chi pan/pa-nee moo-vee Do you understand me?", "Czy pan mówi\nCzy pani mówi")]
    [InlineData("po (angielsku)? I speak English", "po angielsku?")]
    [InlineData("Tak, rozumiem. tak ro-zoo-myem", "Tak, rozumiem.")]
    [InlineData("Proszę? pro-she", "Proszę?")]
    [InlineData("angielsku pronunciation of Angielsku", "angielsku")]
    [InlineData("chi pronunciation of nie", "chi")]
    [InlineData("31", "")]
    [InlineData("language difficulties", "")]
    public void CleanForVocabulary_RemovesLearnerNoteArtifacts(string input, string expected)
    {
        Assert.Equal(expected, VocabularyInputCleaner.CleanForVocabulary(input));
    }

    [Fact]
    public void CleanForVocabulary_ProducesClearSentencesFromAnnotatedPhrasebookRows()
    {
        const string input = """
m/f pol Czy mówisz
Do you speak English?

m/f pol Czy (mnie) rozumiesz?
Do you understand me?

Nie mówię po (polsku).
I don't speak Polish
""";

        const string expected = """
Czy mówisz
Czy mnie rozumiesz?
Nie mówię po polsku.
""";

        Assert.Equal(expected, VocabularyInputCleaner.CleanForVocabulary(input));
    }

    [Fact]
    public void CleanForVocabulary_RemovesPronunciationColumnsFromPhrasebookText()
    {
        const string input = """
Do you speak (English)?
Czy pan/pani mówi chi pan/pa-nee moo-vee
po (angielsku)? m/f pol po (an-gyel-skoo)
Czy mówisz chi moo-veesh
po (angielsku)? inf po (an-gyel-skoo)

Does anyone speak (English)?
Czy ktoś mówi chi ktosh moo-vee
po (angielsku)? po (an-gyel-skoo)

language difficulties
31
""";

        var cleaned = VocabularyInputCleaner.CleanForVocabulary(input);

        Assert.DoesNotContain("Do you speak", cleaned);
        Assert.Contains("Czy pan mówi po angielsku?", cleaned);
        Assert.Contains("Czy pani mówi po angielsku?", cleaned);
        Assert.DoesNotContain("Czy pan pani mówi", cleaned);
        Assert.Contains("po angielsku?", cleaned);
        Assert.Contains("Czy mówisz po angielsku?", cleaned);
        Assert.DoesNotContain("moo", cleaned);
        Assert.DoesNotContain("an-gyel-skoo", cleaned);
        Assert.DoesNotContain("language difficulties", cleaned);
        Assert.DoesNotContain("31", cleaned);
    }

    [Fact]
    public void CleanForVocabulary_CleansFullPhrasebookPaste()
    {
        const string input = """
Do you speak (English)?
Czy pan/pani mówi chi pan/pa-nee moo-vee
po (angielsku)? m/f pol po (an-gyel-skoo)
Czy mówisz chi moo-veesh
po (angielsku)? inf po (an-gyel-skoo)

Does anyone speak (English)?
Czy ktoś mówi chi ktosh moo-vee
po (angielsku)? po (an-gyel-skoo)

Do you understand (me)?
Czy pan/pani (mnie) chi pan/pa-nee (mnye)
rozumie? m/f pol ro-zoo-mye
Czy (mnie) rozumiesz? inf chi (mnye) ro-zoo-myesh

Yes, I understand.
Tak, rozumiem. tak ro-zoo-myem

No, I don't understand.
Nie, nie rozumiem. nye nye ro-zoo-myem

I (don't) understand.
(Nie) Rozumiem. (nye) ro-zoo-myem

Pardon?
Proszę? pro-she

I speak (English).
Mówię po (angielsku). moo-vyem po (an-gyel-skoo)

I don't speak (Polish).
Nie mówię po (polsku). nye moo-vyem po (pol-skoo)

I speak a little.
Mówię trochę. moo-vyem tro-khe

Let's speak (Polish).
Rozmawiajmy po (polsku). roz-mav-yai-mi po (pol-skoo)

What does (nieczynne) mean?
Co to znaczy (nieczynne)? tso to zna-chi (nye-chi-ne)
""";

        const string expected = """
Czy pan mówi po angielsku?
Czy pani mówi po angielsku?
Czy mówisz po angielsku?
Czy ktoś mówi po angielsku?
Czy pan mnie rozumie?
Czy pani mnie rozumie?
Czy mnie rozumiesz?
Tak, rozumiem.
Nie, nie rozumiem.
Nie Rozumiem.
Proszę?
Mówię po angielsku.
Nie mówię po polsku.
Mówię trochę.
Rozmawiajmy po polsku.
Co to znaczy nieczynne?
""";

        var cleaned = VocabularyInputCleaner.CleanForVocabulary(input);

        Assert.Equal(expected, cleaned);
        Assert.DoesNotContain("Do you", cleaned);
        Assert.DoesNotContain("I speak", cleaned);
        Assert.DoesNotContain("Czy pan pani", cleaned);
        Assert.DoesNotContain("moo", cleaned);
        Assert.DoesNotContain("ro-zoo", cleaned);
        Assert.DoesNotContain("an-gyel-skoo", cleaned);
    }
}

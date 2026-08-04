using Glosify.Data;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class QuizLanguagePreferenceTests
{
    [Fact]
    public void Catalog_ContainsExactlyTheFourSupportedQuizLanguages()
    {
        Assert.Equal(
            [("et", "Estonian"), ("de", "German"), ("pl", "Polish"), ("uk", "Ukrainian")],
            QuizLanguageCatalog.All.Select(language => (language.Code, language.Name)).ToArray());
        Assert.Null(QuizLanguageCatalog.Find("Spanish"));
    }

    [Fact]
    public async Task Preference_PersistsCanonicalCodeAndCanBeCleared()
    {
        await using var context = new GlosifyContext(
            new DbContextOptionsBuilder<GlosifyContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        context.Users.Add(new ApplicationUser
        {
            Id = "user-1",
            UserName = "user@example.test",
            Email = "user@example.test",
        });
        await context.SaveChangesAsync();
        var service = new QuizLanguagePreferenceService(context);

        var selected = await service.SetSelectedAsync("user-1", "Polish");
        Assert.Equal("pl", selected.Code);
        Assert.Equal("pl", (await context.Users.SingleAsync()).SelectedQuizLanguageCode);
        Assert.Equal("Polish", (await service.GetSelectedAsync("user-1"))?.Name);

        await service.ClearAsync("user-1");
        Assert.Null((await context.Users.SingleAsync()).SelectedQuizLanguageCode);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SetSelectedAsync("user-1", "Spanish"));
    }
}

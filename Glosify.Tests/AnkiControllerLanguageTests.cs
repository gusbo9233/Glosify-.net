using System.Security.Claims;
using Glosify.Controllers;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.ViewModels;
using Glosify.Services.Anki;
using Glosify.Services.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AnkiControllerLanguageTests
{
    private const string UserId = "anki-controller-user";
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Index_only_shows_collections_for_the_current_app_language()
    {
        await using var context = CreateContext();
        var controller = CreateController(context, "Polish");
        var collections = CreateCollectionService(context);
        await collections.CreateAsync(new("Polish deck", "English", "Polish", "UTC"), UserId);
        await collections.CreateAsync(new("Spanish deck", "English", "Spanish", "UTC"), UserId);

        var result = await controller.Index(cancellationToken: CancellationToken.None);

        var model = Assert.IsType<AnkiIndexViewModel>(Assert.IsType<ViewResult>(result).Model);
        var collection = Assert.Single(model.Collections);
        Assert.Equal("Polish deck", collection.Name);
        Assert.Equal("Polish", model.TargetLanguage);
        Assert.Contains("English", model.SourceLanguages);
        Assert.DoesNotContain("Polish", model.SourceLanguages);
    }

    [Fact]
    public async Task Create_derives_target_from_the_current_app_language()
    {
        await using var context = CreateContext();
        var controller = CreateController(context, "Polish");

        var result = await controller.Create(new CreateAnkiCollectionForm
        {
            Name = "Bound deck",
            SourceLanguage = "English",
            TimeZoneId = "UTC",
        }, CancellationToken.None);

        Assert.Equal(nameof(AnkiController.Collection), Assert.IsType<RedirectToActionResult>(result).ActionName);
        var collection = Assert.Single(await context.AnkiCollections.ToListAsync());
        Assert.Equal("English", collection.SourceLanguage);
        Assert.Equal("Polish", collection.TargetLanguage);
    }

    [Fact]
    public async Task Collection_is_not_found_when_it_belongs_to_another_app_language()
    {
        await using var context = CreateContext();
        var collections = CreateCollectionService(context);
        var spanish = await collections.CreateAsync(new("Spanish deck", "English", "Spanish", "UTC"), UserId);
        var controller = CreateController(context, "Polish");

        var result = await controller.Collection(spanish.Id, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Rename_cannot_mutate_a_collection_from_another_app_language()
    {
        await using var context = CreateContext();
        var collections = CreateCollectionService(context);
        var spanish = await collections.CreateAsync(new("Spanish deck", "English", "Spanish", "UTC"), UserId);
        var controller = CreateController(context, "Polish");

        var result = await controller.Rename(spanish.Id, "Changed", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal("Spanish deck", (await context.AnkiCollections.SingleAsync()).Name);
    }

    [Fact]
    public async Task Study_cannot_open_a_collection_from_another_app_language()
    {
        await using var context = CreateContext();
        var collections = CreateCollectionService(context);
        var spanish = await collections.CreateAsync(new("Spanish deck", "English", "Spanish", "UTC"), UserId);
        var controller = CreateController(context, "Polish");

        var result = await controller.Study(spanish.Id, cancellationToken: CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    private static AnkiController CreateController(GlosifyContext context, string language)
    {
        var collections = CreateCollectionService(context);
        var clock = new FakeTimeProvider(Now);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "Test");
        return new AnkiController(
            collections,
            new AnkiStudyService(context, collections, new Fsrs6AnkiScheduler(), clock),
            new AnkiStatisticsService(context, collections, clock),
            new FixedLanguageContext(language))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
            },
        };
    }

    private static AnkiCollectionService CreateCollectionService(GlosifyContext context) =>
        new(context, new FakeTimeProvider(Now));

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var context = new GlosifyContext(options);
        context.Users.Add(new ApplicationUser { Id = UserId, UserName = "anki-controller@example.test" });
        context.SaveChanges();
        return context;
    }

    private sealed class FixedLanguageContext(string currentLanguage) : ILanguageContext
    {
        public string? CurrentLanguage => currentLanguage;
        public bool HasLanguage => true;
        public IReadOnlyList<string> SupportedLanguages { get; } = ["English", "Polish", "Spanish", "Freestyle"];
        public bool TrySetLanguage(string language) => true;
        public void Clear() { }
    }
}

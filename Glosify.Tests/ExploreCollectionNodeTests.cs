using Glosify.Models.Entities;
using Glosify.Models.ViewModels;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// The tree walk that Explore/Collection.cshtml used to do in an <c>@functions</c> block,
/// now that it is real code with a name.
/// </summary>
/// <remarks>
/// The old view flattened <c>ChildCollections</c> depth-first and built each breadcrumb by
/// walking <c>ParentCollection</c> upwards. That upward walk stopped at the tree root only
/// because CollectionService.BuildCollectionTree links parents solely within the loaded set
/// — an invariant nothing stated and nothing checked. These tests state it.
/// </remarks>
public sealed class ExploreCollectionNodeTests
{
    [Fact]
    public void Descendants_are_depth_first_and_name_ordered()
    {
        var root = Collection("Polish");
        var verbs = Child(root, "Verbs");
        Child(verbs, "Irregular");
        Child(verbs, "Auxiliary");
        Child(root, "Nouns");

        var node = ExploreCollectionNode.From(root);

        Assert.Equal(
            ["Nouns", "Verbs", "Auxiliary", "Irregular"],
            node.Descendants().Select(child => child.Name));
        Assert.Equal(4, node.DescendantCollectionCount);
    }

    [Fact]
    public void Breadcrumb_lists_ancestors_top_down_and_excludes_the_node_itself()
    {
        var root = Collection("Polish");
        var verbs = Child(root, "Verbs");
        var irregular = Child(verbs, "Irregular");
        Child(irregular, "Past tense");

        var node = ExploreCollectionNode.From(root);
        var paths = node.Descendants().ToDictionary(child => child.Name, child => child.Path);

        Assert.Equal("Polish", paths["Verbs"]);
        Assert.Equal("Polish / Verbs", paths["Irregular"]);
        Assert.Equal("Polish / Verbs / Irregular", paths["Past tense"]);
    }

    /// <summary>
    /// With no ancestors the old view fell back to the language rather than an empty string.
    /// </summary>
    [Fact]
    public void Root_falls_back_to_its_language()
    {
        var root = Collection("Polish");
        root.Language = "Polish";

        Assert.Equal("Polish", ExploreCollectionNode.From(root).Path);
    }

    [Fact]
    public void Quiz_totals_include_every_level()
    {
        var root = Collection("Polish");
        AddQuiz(root, "Greetings");
        var verbs = Child(root, "Verbs");
        AddQuiz(verbs, "Motion");
        AddQuiz(verbs, "Aspect");
        AddQuiz(Child(verbs, "Irregular"), "Top 20");

        var node = ExploreCollectionNode.From(root);

        Assert.Equal(4, node.TotalQuizCount);
        Assert.Single(node.Quizzes);
        // Quizzes arrive name-ordered, so the view no longer sorts them itself.
        Assert.Equal(
            ["Aspect", "Motion"],
            node.Descendants().Single(child => child.Name == "Verbs").Quizzes.Select(quiz => quiz.Name));
    }

    private static Collection Collection(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Language = "Polish",
    };

    private static Collection Child(Collection parent, string name)
    {
        var child = Collection(name);
        child.ParentCollectionId = parent.Id;
        child.ParentCollection = parent;
        parent.ChildCollections.Add(child);
        return child;
    }

    private static void AddQuiz(Collection collection, string name) =>
        collection.Quizzes.Add(new Quiz
        {
            Id = Guid.NewGuid(),
            Name = name,
            CollectionId = collection.Id,
            SourceLanguage = "English",
            TargetLanguage = "Polish",
            ProcessingStatus = "Ready",
        });
}

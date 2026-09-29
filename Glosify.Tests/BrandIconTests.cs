using System.Buffers.Binary;
using Xunit;

namespace Glosify.Tests;

public sealed class BrandIconTests
{
    public static TheoryData<string, int> PngIcons => new()
    {
        { "Glosify/wwwroot/images/favicon-16x16.png", 16 },
        { "Glosify/wwwroot/images/favicon-32x32.png", 32 },
        { "Glosify.LiveSubtitles.Extension/icons/icon16.png", 16 },
        { "Glosify.LiveSubtitles.Extension/icons/icon32.png", 32 },
        { "Glosify.LiveSubtitles.Extension/icons/icon48.png", 48 },
        { "Glosify.LiveSubtitles.Extension/icons/icon128.png", 128 },
        { "Glosify.Translator.Extension/icons/icon16.png", 16 },
        { "Glosify.Translator.Extension/icons/icon32.png", 32 },
        { "Glosify.Translator.Extension/icons/icon48.png", 48 },
        { "Glosify.Translator.Extension/icons/icon128.png", 128 },
    };

    [Theory]
    [MemberData(nameof(PngIcons))]
    public void Generated_png_icons_have_the_declared_square_dimensions(string relativePath, int expectedSize)
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepositoryRoot(), relativePath));

        Assert.True(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        Assert.Equal(expectedSize, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(expectedSize, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    [Fact]
    public void Shared_layouts_reference_each_versioned_favicon_format()
    {
        var sharedViews = Path.Combine(RepositoryRoot(), "Glosify", "Views", "Shared");

        foreach (var layoutName in new[] { "_AppLayout.cshtml", "_GlosifyLayout.cshtml" })
        {
            var markup = File.ReadAllText(Path.Combine(sharedViews, layoutName));

            Assert.Contains("<link rel=\"icon\" type=\"image/png\" sizes=\"32x32\" href=\"~/images/favicon-32x32.png\" asp-append-version=\"true\" />", markup, StringComparison.Ordinal);
            Assert.Contains("<link rel=\"icon\" type=\"image/png\" sizes=\"16x16\" href=\"~/images/favicon-16x16.png\" asp-append-version=\"true\" />", markup, StringComparison.Ordinal);
            Assert.Contains("<link rel=\"shortcut icon\" type=\"image/x-icon\" href=\"~/favicon.ico\" asp-append-version=\"true\" />", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("favicon.svg", markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Favicon_ico_contains_16_and_32_pixel_images()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "Glosify", "wwwroot", "favicon.ico"));

        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)));
        Assert.Equal(16, bytes[6]);
        Assert.Equal(16, bytes[7]);
        Assert.Equal(32, bytes[22]);
        Assert.Equal(32, bytes[23]);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Glosify.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Glosify repository root.");
    }
}

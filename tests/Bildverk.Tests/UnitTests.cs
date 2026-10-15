using Bildverk.Commands;
using Bildverk.Processing;
using Bildverk.Sources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Bildverk.Tests;

public sealed class CommandParserTests
{
    private static ImageCommands? Parse(string query) => CommandParser.Parse(new QueryCollection(QueryHelpers.ParseQuery(query)));

    [Fact]
    public void Parses_all_commands()
    {
        var c = Parse("?width=1920&height=600&rmode=crop&ranchor=topleft&rxy=0.25,0.75&quality=80&format=webp&bgcolor=fff")!;

        Assert.Equal(1920, c.Width);
        Assert.Equal(600, c.Height);
        Assert.Equal(ResizeMode.Crop, c.Mode);
        Assert.Equal(Anchor.TopLeft, c.Anchor);
        Assert.Equal((0.25, 0.75), c.Focal);
        Assert.Equal(80, c.Quality);
        Assert.Equal(OutputFormat.WebP, c.Format);
        Assert.Equal(Rgba.White, c.Background);
    }

    [Theory]
    [InlineData("?width=-5")]
    [InlineData("?width=abc")]
    [InlineData("?quality=0")]
    [InlineData("?quality=101")]
    [InlineData("?rmode=1")]
    [InlineData("?rmode=explode")]
    [InlineData("?rxy=2,0")]
    [InlineData("?format=bmp")]
    [InlineData("?bgcolor=zzzzzz")]
    [InlineData("?foo=bar")]
    public void Invalid_or_unknown_values_are_ignored(string query) => Assert.Null(Parse(query));

    [Fact]
    public void Canonical_form_is_order_and_case_independent()
    {
        var a = CommandParser.Canonicalize(Parse("?width=10&height=20&rmode=Crop&format=JPEG")!);
        var b = CommandParser.Canonicalize(Parse("?format=jpg&RMODE=crop&height=20&width=10")!);
        Assert.Equal(a, b);
        Assert.Equal("format=jpg&height=20&rmode=crop&width=10", a);
    }
}

public sealed class CacheKeyTests
{
    [Fact]
    public void Encoder_settings_only_affect_their_own_format()
    {
        var webp = new ImageCommands { Width = 100, Format = OutputFormat.WebP, Quality = 80 };
        var jpeg = webp with { Format = OutputFormat.Jpeg };

        Assert.NotEqual(BildverkMiddleware.CacheKey("/a.jpg", webp, 16, new(WebpEffort: 0)), BildverkMiddleware.CacheKey("/a.jpg", webp, 16, new(WebpEffort: 4)));
        Assert.Equal(BildverkMiddleware.CacheKey("/a.jpg", jpeg, 16, new(WebpEffort: 0)), BildverkMiddleware.CacheKey("/a.jpg", jpeg, 16, new(WebpEffort: 4)));
        Assert.NotEqual(BildverkMiddleware.CacheKey("/a.jpg", jpeg, 16, new(ProgressiveJpeg: false)), BildverkMiddleware.CacheKey("/a.jpg", jpeg, 16, new(ProgressiveJpeg: true)));
    }
}

public sealed class ResizePlanTests
{
    private static ResizePlan Plan(int w, int h, string query) =>
        ResizePlan.Create(w, h, CommandParser.Parse(new QueryCollection(QueryHelpers.ParseQuery(query)))!);

    [Theory]
    // source 800×400
    [InlineData("?width=400", 400, 200)]
    [InlineData("?height=100", 200, 100)]
    [InlineData("?width=400&height=400", 400, 200)]                 // max: fit
    [InlineData("?width=200&height=200&rmode=crop", 200, 200)]
    [InlineData("?width=200&height=200&rmode=pad", 200, 200)]
    [InlineData("?width=2000&height=2000&rmode=boxpad", 2000, 2000)] // canvas grows, image doesn't
    [InlineData("?width=2000&rmode=min", 800, 400)]                  // min never upscales
    [InlineData("?width=100&height=300&rmode=stretch", 100, 300)]
    public void Output_size(string query, int width, int height)
    {
        var p = Plan(800, 400, query);
        Assert.Equal((width, height), (p.OutputWidth, p.OutputHeight));
    }

    [Fact]
    public void Boxpad_does_not_upscale_the_image()
    {
        var p = Plan(800, 400, "?width=2000&height=2000&rmode=boxpad");
        Assert.Equal((800, 400), (p.ScaledWidth, p.ScaledHeight));
        Assert.Equal(new Box(600, 800, 2000, 2000), p.Pad);
    }

    [Theory]
    [InlineData("left", 0)]
    [InlineData("center", 100)]
    [InlineData("right", 200)]
    public void Crop_anchor_offsets(string anchor, int left)
    {
        var p = Plan(800, 400, $"?width=200&height=200&rmode=crop&ranchor={anchor}");
        Assert.Equal((400, 200), (p.ScaledWidth, p.ScaledHeight));
        Assert.Equal(left, p.Crop!.Value.Left);
    }

    [Fact]
    public void Focal_point_is_clamped_inside_the_image()
    {
        var p = Plan(800, 400, "?width=200&height=200&rmode=crop&rxy=1,0.5");
        Assert.Equal(200, p.Crop!.Value.Left); // can't go past the right edge
    }
}

public sealed class FileSystemImageSourceTests
{
    [Fact]
    public async Task Paths_outside_the_root_are_not_served()
    {
        var root = Path.Combine(Path.GetTempPath(), "bildverk-tests", Guid.NewGuid().ToString("N"));
        var web = Directory.CreateDirectory(Path.Combine(root, "web")).FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "secret.jpg"), "x", TestContext.Current.CancellationToken);
        try
        {
            var env = new FakeEnv { ContentRootPath = root, WebRootFileProvider = new PhysicalFileProvider(web) };
            var source = new FileSystemImageSource(Options.Create(new BildverkOptions()), env);

            Assert.Null(await source.GetAsync("/../secret.jpg", TestContext.Current.CancellationToken));
            Assert.Null(await source.GetAsync("/..\\secret.jpg", TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeEnv : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }
}

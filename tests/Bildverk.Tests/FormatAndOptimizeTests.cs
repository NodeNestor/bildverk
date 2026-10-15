using System.Net;
using Bildverk.Commands;
using Bildverk.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Bildverk.Tests;

public sealed class FormatAndOptimizeTests
{
    private const string Chrome = "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8";
    private const string WebpOnly = "image/webp,*/*";

    [Theory]
    [InlineData(Chrome, "image/avif")]
    [InlineData(WebpOnly, "image/webp")]
    [InlineData("image/avif;q=0,image/webp", "image/webp")]
    [InlineData("*/*", "image/jpeg")]
    [InlineData(null, "image/jpeg")]
    public async Task Format_auto_picks_the_best_accepted_format_and_varies_on_accept(string? accept, string expected)
    {
        await using var app = await TestApp.StartAsync();

        var (response, body) = await app.GetAsync("/img/halves.jpg?width=100&format=auto", accept);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Accept", response.Headers.Vary);
        using var img = TestImages.Decode(body);
        Assert.Equal(100, img.Width);
    }

    [Fact]
    public async Task Preference_order_is_configurable()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Formats:Preference:0"] = "webp" });

        var (response, _) = await app.GetAsync("/img/halves.jpg?width=100&format=auto", Chrome);

        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Explicit_format_wins_and_does_not_vary()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Formats:AutoByDefault"] = "true" });

        var (response, _) = await app.GetAsync("/img/halves.jpg?width=100&format=png", Chrome);

        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Accept", response.Headers.Vary);
    }

    [Fact]
    public async Task Auto_by_default_applies_to_requests_without_format()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Formats:AutoByDefault"] = "true" });

        var (webp, _) = await app.GetAsync("/img/halves.jpg?width=100", WebpOnly);
        var (jpeg, _) = await app.GetAsync("/img/halves.jpg?width=100", null);

        Assert.Equal("image/webp", webp.Content.Headers.ContentType?.MediaType);
        Assert.Equal("image/jpeg", jpeg.Content.Headers.ContentType?.MediaType);
        Assert.Equal(2, app.Processor.Calls); // one cache entry per format
    }

    [Fact]
    public async Task Auto_and_explicit_format_share_cache_entries()
    {
        await using var app = await TestApp.StartAsync();

        await app.GetAsync("/img/halves.jpg?width=100&format=webp", null);
        await app.GetAsync("/img/halves.jpg?width=100&format=auto", WebpOnly);

        Assert.Equal(1, app.Processor.Calls);
    }

    [Fact]
    public async Task Plain_urls_are_converted_when_enabled_and_untouched_otherwise()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Formats:AutoForPlainRequests"] = "true" });
        var original = await File.ReadAllBytesAsync(Path.Combine(app.WebRoot, "img", "halves.jpg"), TestContext.Current.CancellationToken);

        var (converted, body) = await app.GetAsync("/img/halves.jpg", WebpOnly);
        var (fallback, fallbackBody) = await app.GetAsync("/img/halves.jpg", "*/*");

        Assert.Equal("image/webp", converted.Content.Headers.ContentType?.MediaType);
        using var img = TestImages.Decode(body);
        Assert.Equal((800, 400), (img.Width, img.Height));
        Assert.Equal(original, fallbackBody);             // served by static files
        Assert.Contains("Accept", fallback.Headers.Vary); // but caches must still know it varies
        Assert.Equal(1, app.Processor.Calls);
    }

    [Fact]
    public async Task Plain_urls_are_not_converted_by_default()
    {
        await using var app = await TestApp.StartAsync();

        var (response, _) = await app.GetAsync("/img/halves.jpg", Chrome);

        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, app.Processor.Calls);
    }

    [Fact]
    public async Task Signed_auto_urls_work()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Security:HmacKey"] = "k-0123456789abcdef0123456789abcdef" });
        var url = app.Services.GetRequiredService<IBildverkUrlSigner>().Sign("/img/halves.jpg?width=100&format=auto");

        var (response, _) = await app.GetAsync(url, WebpOnly);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Background_optimizer_reencodes_at_high_effort_once()
    {
        await using var app = await TestApp.StartAsync(new()
        {
            ["Bildverk:Defaults:WebpEffort"] = "0",
            ["Bildverk:Optimize:WebpEffort"] = "6",
            ["Bildverk:Optimize:MinSavingsPercent"] = "0",
        });
        var optimizer = app.Services.GetRequiredService<BackgroundOptimizer>();

        var (first, _) = await app.GetAsync("/img/halves.jpg?width=300&format=webp", null);
        await WaitUntil(() => optimizer.Pending == 0 && app.Processor.Calls == 2);
        var (second, body) = await app.GetAsync("/img/halves.jpg?width=300&format=webp", null);
        await app.GetAsync("/img/halves.jpg?width=300&format=webp", null);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var efforts = app.Processor.Requests.Select(r => r.Encoder!.WebpEffort).ToArray();
        Assert.Equal([0, 6], efforts);                      // fast first, then one high-effort pass, never again
        Assert.EndsWith("-o\"", second.Headers.ETag!.Tag);  // browsers holding the first bytes revalidate
        Assert.NotEqual(first.Headers.ETag, second.Headers.ETag);
        using var img = TestImages.Decode(body);
        Assert.Equal(300, img.Width);
    }

    [Fact]
    public async Task Optimizer_ignores_formats_with_nothing_to_gain()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Optimize:Enabled"] = "true" });

        await app.GetAsync("/img/halves.jpg?width=300", null); // JPEG
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.Equal(1, app.Processor.Calls);
    }

    [Fact]
    public void Negotiate_respects_preference_and_ignores_wildcards()
    {
        Assert.Equal(OutputFormat.Avif, BildverkMiddleware.Negotiate(Chrome, ["avif", "webp"]));
        Assert.Equal(OutputFormat.WebP, BildverkMiddleware.Negotiate(Chrome, ["webp", "avif"]));
        Assert.Null(BildverkMiddleware.Negotiate("image/*,*/*", ["avif", "webp"]));
        Assert.Null(BildverkMiddleware.Negotiate("garbage;;;", ["avif"]));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(condition(), "timed out waiting for the background optimizer");
    }
}

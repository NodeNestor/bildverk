using System.Net;
using System.Net.Http.Headers;
using Bildverk.Caching;
using Bildverk.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Bildverk.Tests;

public sealed class MiddlewareTests
{
    [Fact]
    public async Task Crop_returns_exact_size_and_respects_anchor()
    {
        await using var app = await TestApp.StartAsync();

        var (left, leftBody) = await app.GetAsync("/img/halves.jpg?width=200&height=200&rmode=crop&ranchor=left");
        var (right, rightBody) = await app.GetAsync("/img/halves.jpg?width=200&height=200&rmode=crop&ranchor=right");

        Assert.Equal(HttpStatusCode.OK, left.StatusCode);
        Assert.Equal("image/jpeg", left.Content.Headers.ContentType?.MediaType);
        using var l = TestImages.Decode(leftBody);
        using var r = TestImages.Decode(rightBody);
        Assert.Equal((200, 200), (l.Width, l.Height));
        Assert.Equal((200, 200), (r.Width, r.Height));
        AssertRedish(l.Getpoint(100, 100));
        AssertBlueish(r.Getpoint(100, 100));
    }

    [Fact]
    public async Task Focal_point_moves_the_crop()
    {
        await using var app = await TestApp.StartAsync();

        var (_, body) = await app.GetAsync("/img/halves.jpg?width=100&height=100&rmode=crop&rxy=0.9,0.5");

        using var img = TestImages.Decode(body);
        Assert.Equal((100, 100), (img.Width, img.Height));
        AssertBlueish(img.Getpoint(50, 50));
    }

    [Fact]
    public async Task Max_mode_keeps_aspect_ratio()
    {
        await using var app = await TestApp.StartAsync();

        var (_, body) = await app.GetAsync("/img/halves.jpg?width=400&height=400");

        using var img = TestImages.Decode(body);
        Assert.Equal((400, 200), (img.Width, img.Height));
    }

    [Fact]
    public async Task Pad_fills_with_transparency_or_bgcolor()
    {
        await using var app = await TestApp.StartAsync();

        var (_, transparent) = await app.GetAsync("/img/alpha.png?width=200&height=200&rmode=pad");
        var (_, red) = await app.GetAsync("/img/alpha.png?width=200&height=200&rmode=pad&bgcolor=ff0000");

        using var t = TestImages.Decode(transparent);
        Assert.Equal((200, 200), (t.Width, t.Height));
        Assert.Equal(4, t.Bands);
        Assert.Equal(0, t.Getpoint(0, 0)[3]);       // padding: transparent
        Assert.Equal(255, t.Getpoint(100, 100)[1]); // image: green

        using var r = TestImages.Decode(red);
        AssertRedish(r.Getpoint(0, 0));
    }

    [Fact]
    public async Task Pad_to_jpeg_uses_white_background()
    {
        await using var app = await TestApp.StartAsync();

        var (_, body) = await app.GetAsync("/img/halves.jpg?width=400&height=400&rmode=pad");

        using var img = TestImages.Decode(body);
        Assert.Equal((400, 400), (img.Width, img.Height));
        Assert.All(img.Getpoint(5, 5), v => Assert.True(v > 240));
    }

    [Theory]
    [InlineData("webp", "image/webp")]
    [InlineData("png", "image/png")]
    [InlineData("avif", "image/avif")]
    [InlineData("gif", "image/gif")]
    public async Task Format_converts(string format, string contentType)
    {
        await using var app = await TestApp.StartAsync();

        var (response, body) = await app.GetAsync($"/img/halves.jpg?width=100&format={format}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        using var img = TestImages.Decode(body);
        Assert.Equal(100, img.Width);
    }

    [Fact]
    public async Task Exif_orientation_is_applied()
    {
        await using var app = await TestApp.StartAsync();

        var (_, body) = await app.GetAsync("/img/rotated.jpg?width=50");

        using var img = TestImages.Decode(body);
        Assert.Equal((50, 100), (img.Width, img.Height));
    }

    [Fact]
    public async Task Second_request_is_served_from_the_configured_cache_folder()
    {
        await using var app = await TestApp.StartAsync();

        var (first, a) = await app.GetAsync("/img/halves.jpg?width=120");
        var (second, b) = await app.GetAsync("/img/halves.jpg?width=120");

        Assert.Equal(1, app.Processor.Calls);
        Assert.Equal(a, b);
        Assert.Equal(first.Headers.ETag, second.Headers.ETag);

        var files = Directory.GetFiles(app.CacheDir, "*", SearchOption.AllDirectories);
        Assert.Equal(2, files.Length); // image + .json metadata
        Assert.Single(files, f => f.EndsWith(".json"));
    }

    [Fact]
    public async Task Equivalent_urls_share_one_cache_entry()
    {
        await using var app = await TestApp.StartAsync();

        await app.GetAsync("/img/halves.jpg?width=100&height=50&rmode=crop");
        await app.GetAsync("/img/halves.jpg?height=50&rmode=CROP&width=100");      // order, case
        await app.GetAsync("/img/halves.jpg?width=100&height=50&rmode=crop&utm=x"); // unknown parameter
        await app.GetAsync("/img/halves.jpg?width=100&height=50&rmode=crop&quality=80"); // = default quality

        Assert.Equal(1, app.Processor.Calls);
    }

    [Fact]
    public async Task Requests_without_commands_get_the_original_file()
    {
        await using var app = await TestApp.StartAsync();

        var (plain, body) = await app.GetAsync("/img/halves.jpg");
        var (unknownOnly, body2) = await app.GetAsync("/img/halves.jpg?v=123");

        var original = await File.ReadAllBytesAsync(Path.Combine(app.WebRoot, "img", "halves.jpg"), TestContext.Current.CancellationToken);
        Assert.Equal(original, body);
        Assert.Equal(original, body2);
        Assert.Equal(0, app.Processor.Calls);
    }

    [Fact]
    public async Task Missing_image_is_404()
    {
        await using var app = await TestApp.StartAsync();

        var (response, _) = await app.GetAsync("/img/nope.jpg?width=100");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Non_images_are_415_corrupt_images_422_and_nothing_is_cached()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await app.GetAsync("/img/broken.jpg?width=100")).Response.StatusCode);
        Assert.Equal((HttpStatusCode)422, (await app.GetAsync("/img/truncated.jpg?width=100")).Response.StatusCode);
        Assert.False(Directory.Exists(app.CacheDir) && Directory.EnumerateFiles(app.CacheDir, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task Svg_disguised_as_jpeg_is_rejected_before_decoding()
    {
        await using var app = await TestApp.StartAsync();

        var (response, _) = await app.GetAsync("/img/disguised.jpg?width=10");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Allowed_input_formats_are_configurable()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Limits:AllowedInputFormats:0"] = "jpeg" });

        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/img/halves.jpg?width=10")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await app.GetAsync("/img/alpha.png?width=10")).Response.StatusCode);
    }

    [Fact]
    public async Task Sizes_are_clamped_to_configured_limits()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Limits:MaxWidth"] = "300" });

        var (_, body) = await app.GetAsync("/img/halves.jpg?width=99999");
        await app.GetAsync("/img/halves.jpg?width=300");

        using var img = TestImages.Decode(body);
        Assert.Equal(300, img.Width);
        Assert.Equal(1, app.Processor.Calls); // clamped request and width=300 are the same entry
    }

    [Fact]
    public async Task Too_many_source_pixels_is_rejected()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Limits:MaxSourcePixels"] = "1000" });

        var (response, _) = await app.GetAsync("/img/halves.jpg?width=10");

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Changed_source_is_reprocessed()
    {
        await using var app = await TestApp.StartAsync();
        var file = Path.Combine(app.WebRoot, "img", "halves.jpg");

        var (first, _) = await app.GetAsync("/img/halves.jpg?width=100");
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(5));
        var (second, _) = await app.GetAsync("/img/halves.jpg?width=100");

        Assert.Equal(2, app.Processor.Calls);
        Assert.NotEqual(first.Headers.ETag, second.Headers.ETag);
    }

    [Fact]
    public async Task Replacing_an_entry_that_is_being_downloaded_still_succeeds()
    {
        await using var app = await TestApp.StartAsync();
        var file = Path.Combine(app.WebRoot, "img", "halves.jpg");
        await app.GetAsync("/img/halves.jpg?width=100");
        var cached = Directory.GetFiles(app.CacheDir, "*", SearchOption.AllDirectories).Single(f => !f.EndsWith(".json"));

        // Kestrel's SendFile opens files like this (no FileShare.Delete), which blocks replacing them on Windows.
        using (new FileStream(cached, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(1));
            var (response, body) = await app.GetAsync("/img/halves.jpg?width=100");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var img = TestImages.Decode(body);
            Assert.Equal(100, img.Width);
        }
    }

    [Fact]
    public async Task Superseded_cache_files_are_removed_by_cleanup_once_free()
    {
        await using var app = await TestApp.StartAsync();
        var file = Path.Combine(app.WebRoot, "img", "halves.jpg");
        await app.GetAsync("/img/halves.jpg?width=100");
        var first = Directory.GetFiles(app.CacheDir, "*", SearchOption.AllDirectories).Single(f => !f.EndsWith(".json"));

        using (new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(1));
            await app.GetAsync("/img/halves.jpg?width=100"); // new version written
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows can't delete a file that is open, so the old version waits for the cleanup.
            Assert.True(File.Exists(first));
            File.SetLastWriteTimeUtc(first, app.Time.GetUtcNow().UtcDateTime.AddHours(-2)); // past the orphan grace period
            var result = await app.Services.GetRequiredService<IImageCache>().CleanupAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, result.Orphans);
        }

        // Linux/macOS delete it immediately (open readers keep their handle).
        Assert.False(File.Exists(first));
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/img/halves.jpg?width=100")).Response.StatusCode);
        Assert.Equal(2, app.Processor.Calls);
    }

    [Fact]
    public async Task Allowed_sizes_snap_requests_to_a_fixed_set()
    {
        await using var app = await TestApp.StartAsync(new()
        {
            ["Bildverk:Limits:AllowedWidths:0"] = "320",
            ["Bildverk:Limits:AllowedWidths:1"] = "640",
        });

        var (_, a) = await app.GetAsync("/img/halves.jpg?width=300"); // → 320
        await app.GetAsync("/img/halves.jpg?width=319");             // → 320, same entry
        var (_, b) = await app.GetAsync("/img/halves.jpg?width=900"); // → 640 (largest)

        using var ia = TestImages.Decode(a);
        using var ib = TestImages.Decode(b);
        Assert.Equal(320, ia.Width);
        Assert.Equal(640, ib.Width);
        Assert.Equal(2, app.Processor.Calls);
    }

    [Fact]
    public async Task Responses_forbid_content_sniffing()
    {
        await using var app = await TestApp.StartAsync();

        var (response, _) = await app.GetAsync("/img/halves.jpg?width=100");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Expired_entry_is_reprocessed()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Cache:MaxAge"] = "1.00:00:00" });

        await app.GetAsync("/img/halves.jpg?width=100");
        app.Time.Advance(TimeSpan.FromHours(23));
        await app.GetAsync("/img/halves.jpg?width=100");
        Assert.Equal(1, app.Processor.Calls);

        app.Time.Advance(TimeSpan.FromHours(2));
        await app.GetAsync("/img/halves.jpg?width=100");
        Assert.Equal(2, app.Processor.Calls);
    }

    [Fact]
    public async Task Conditional_request_gets_304_and_cache_headers_follow_config()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Browser:MaxAge"] = "02:00:00" });

        var (first, _) = await app.GetAsync("/img/halves.jpg?width=100");
        Assert.Equal(TimeSpan.FromHours(2), first.Headers.CacheControl?.MaxAge);
        Assert.True(first.Headers.CacheControl?.Public);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/img/halves.jpg?width=100");
        request.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var second = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotModified, second.StatusCode);
    }

    [Fact]
    public async Task Head_returns_headers_only()
    {
        await using var app = await TestApp.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Head, "/img/halves.jpg?width=100");
        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Content.Headers.ContentLength > 0);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_requests_for_the_same_image_process_once()
    {
        await using var app = await TestApp.StartAsync();
        app.Processor.Delay = TimeSpan.FromMilliseconds(200);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => app.GetAsync("/img/halves.jpg?width=64")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.Response.StatusCode));
        Assert.Equal(1, app.Processor.Calls);
    }

    [Fact]
    public async Task Hmac_blocks_unsigned_and_tampered_urls()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Security:HmacKey"] = "test-secret-0123456789abcdef0123456789" });
        var signer = app.Services.GetRequiredService<IBildverkUrlSigner>();

        var signed = signer.Sign("/img/halves.jpg?width=100&height=100&rmode=crop");
        var tampered = signed.Replace("width=100", "width=4000");

        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync("/img/halves.jpg?width=100&height=100&rmode=crop")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync(tampered)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync(signed)).Response.StatusCode);
        // Originals (no commands) are still served.
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/img/halves.jpg")).Response.StatusCode);
    }

    [Fact]
    public async Task Cleanup_removes_expired_entries()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Cache:MaxAge"] = "1.00:00:00" });
        var cache = app.Services.GetRequiredService<IImageCache>();

        await app.GetAsync("/img/halves.jpg?width=100");
        app.Time.Advance(TimeSpan.FromDays(2));
        var result = await cache.CleanupAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Expired);
        Assert.Empty(Directory.GetFiles(app.CacheDir, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cleanup_evicts_least_recently_used_when_over_size_limit()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Cache:MaxSizeMB"] = "1" });
        var cache = app.Services.GetRequiredService<IImageCache>();
        var meta = new CacheEntryMetadata("image/jpeg", DateTimeOffset.UnixEpoch, 1, app.Time.GetUtcNow());
        var payload = new byte[400 * 1024];

        string[] keys = ["aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", "cccccccccccccccc"];
        for (var i = 0; i < keys.Length; i++)
        {
            var hit = await cache.SetAsync(keys[i], payload, meta, TestContext.Current.CancellationToken);
            File.SetLastAccessTimeUtc(hit.PhysicalPath!, new DateTime(2026, 1, 1).AddHours(i)); // a oldest, c newest
        }

        var result = await cache.CleanupAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Evicted); // 1200 KB > 1024 KB → drop oldest → 800 KB ≤ 90 %
        Assert.Null(await cache.GetAsync("aaaaaaaaaaaaaaaa", TestContext.Current.CancellationToken));
        Assert.NotNull(await cache.GetAsync("cccccccccccccccc", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cache_folder_and_depth_come_from_configuration()
    {
        await using var app = await TestApp.StartAsync(new()
        {
            ["Bildverk:Cache:Path"] = "custom/where-images-go",
            ["Bildverk:Cache:FolderDepth"] = "3",
            ["Bildverk:Cache:HashLength"] = "20",
        });

        await app.GetAsync("/img/halves.jpg?width=100");

        var file = Directory.GetFiles(Path.Combine(app.Root, "custom", "where-images-go"), "*", SearchOption.AllDirectories)
            .Single(f => !f.EndsWith(".json"));
        var key = Path.GetFileNameWithoutExtension(file); // "{key}.{version}"
        Assert.Equal(20, key.Length);
        var relative = Path.GetRelativePath(Path.Combine(app.Root, "custom", "where-images-go"), file);
        Assert.Equal(Path.Combine(key[0].ToString(), key[1].ToString(), key[2].ToString(), Path.GetFileName(file)), relative);
    }

    [Fact]
    public async Task Masters_are_made_once_and_reused_for_smaller_sizes()
    {
        await using var app = await TestApp.StartAsync(new()
        {
            ["Bildverk:Masters:Enabled"] = "true",
            ["Bildverk:Masters:Size"] = "400",
            ["Bildverk:Masters:MinSourceBytes"] = "1",
        });

        var (_, a) = await app.GetAsync("/img/halves.jpg?width=200&height=200&rmode=crop&ranchor=right"); // master + this
        var (_, b) = await app.GetAsync("/img/halves.jpg?width=100&height=100&rmode=crop&ranchor=left");  // from master
        var (_, c) = await app.GetAsync("/img/halves.jpg?width=600");                                      // bigger than master: original

        Assert.Equal(4, app.Processor.Calls);
        Assert.Equal(1, app.Processor.Requests.Count(r => r.Commands.NoUpscale)); // exactly one master
        using var ia = TestImages.Decode(a);
        using var ib = TestImages.Decode(b);
        using var ic = TestImages.Decode(c);
        Assert.Equal((200, 200), (ia.Width, ia.Height));
        Assert.Equal((100, 100), (ib.Width, ib.Height));
        Assert.Equal((600, 300), (ic.Width, ic.Height));
        AssertBlueish(ia.Getpoint(100, 100));
        AssertRedish(ib.Getpoint(50, 50));
    }

    [Fact]
    public async Task Masters_are_skipped_for_small_originals()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Masters:Enabled"] = "true" }); // default MinSourceBytes 1.5 MB

        await app.GetAsync("/img/halves.jpg?width=200");

        Assert.Equal(1, app.Processor.Calls);
    }

    [Fact]
    public async Task Cache_file_deleted_from_disk_is_reprocessed()
    {
        await using var app = await TestApp.StartAsync();

        await app.GetAsync("/img/halves.jpg?width=100");
        foreach (var f in Directory.GetFiles(app.CacheDir, "*", SearchOption.AllDirectories)) File.Delete(f);
        var (response, body) = await app.GetAsync("/img/halves.jpg?width=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, app.Processor.Calls);
        using var img = TestImages.Decode(body);
        Assert.Equal(100, img.Width);
    }

    [Fact]
    public async Task Defaults_encode_avif_fast_then_optimize_it_in_the_background()
    {
        await using var app = await TestApp.StartAsync();

        await app.GetAsync("/img/halves.jpg?width=100&format=avif");
        for (var i = 0; i < 100 && app.Processor.Calls < 2; i++) await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(
            [new Bildverk.Processing.EncoderSettings(true, 4, 0), new Bildverk.Processing.EncoderSettings(true, 4, 4)],
            app.Processor.Requests.Select(r => r.Encoder).ToArray());
    }

    private static void AssertRedish(double[] px) => Assert.True(px[0] > 200 && px[1] < 60 && px[2] < 60, $"expected red, got {string.Join(",", px)}");

    private static void AssertBlueish(double[] px) => Assert.True(px[2] > 200 && px[0] < 60 && px[1] < 60, $"expected blue, got {string.Join(",", px)}");
}

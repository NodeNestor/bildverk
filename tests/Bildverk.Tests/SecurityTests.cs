using System.Net;
using Bildverk.Caching;
using Bildverk.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetVips;

namespace Bildverk.Tests;

/// <summary>One test (or theory) per item of the threat list in README "Security".</summary>
public sealed class SecurityTests
{
    // ---- 1. Cache path can never point at app data, and cleanup only deletes its own files ----

    [Theory]
    [InlineData(".")]               // the content root itself
    [InlineData("wwwroot")]         // the web root
    [InlineData("wwwroot/cache")]   // inside the web root: processed files would be public
    [InlineData("..")]              // contains the content root
    public async Task Dangerous_cache_paths_refuse_to_start(string path)
    {
        var ex = await Assert.ThrowsAsync<OptionsValidationException>(() => TestApp.StartAsync(new() { ["Bildverk:Cache:Path"] = path }));
        Assert.Contains("Cache:Path", ex.Message);
    }

    [Fact]
    public async Task Cleanup_never_deletes_files_it_did_not_write()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Cache:MaxAge"] = "1.00:00:00" });
        await app.GetAsync("/img/halves.jpg?width=100");
        string[] foreign =
        [
            Path.Combine(app.CacheDir, "notes.txt"),
            Path.Combine(app.CacheDir, "settings.json"),
            Path.Combine(app.CacheDir, "0123456789abcdef.txt"),
            Path.Combine(app.CacheDir, "a", "data.db"),
        ];
        foreach (var f in foreign)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            await File.WriteAllTextAsync(f, "keep me", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(f, app.Time.GetUtcNow().UtcDateTime.AddDays(-30));
        }
        app.Time.Advance(TimeSpan.FromDays(2));

        var result = await app.Services.GetRequiredService<IImageCache>().CleanupAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Expired);                // our entry went
        Assert.All(foreign, f => Assert.True(File.Exists(f), f)); // nothing else did
    }

    // ---- 2. A hash collision can't serve the wrong image ----

    [Fact]
    public async Task Cache_hit_requires_the_exact_canonical_request()
    {
        await using var app = await TestApp.StartAsync();
        var pipeline = app.Services.GetRequiredService<ImagePipeline>();
        var options = app.Services.GetRequiredService<IOptions<BildverkOptions>>().Value;
        var source = new SourceImage(DateTimeOffset.UnixEpoch, 10, _ => Task.FromResult<Stream>(new MemoryStream()));
        var cached = new CachedImage(new CacheEntryMetadata("image/jpeg", DateTimeOffset.UnixEpoch, 10, app.Time.GetUtcNow(), Canonical: "v2|/a.jpg?width=100"),
            1, app.Time.GetUtcNow(), null, _ => Task.FromResult<Stream>(new MemoryStream()));

        Assert.True(pipeline.IsValid(cached, "v2|/a.jpg?width=100", source, options));
        Assert.False(pipeline.IsValid(cached, "v2|/b.jpg?width=100", source, options)); // same hash, different request
    }

    // ---- 3. Slow images are cut off ----

    [Fact]
    public async Task Processing_that_exceeds_the_time_limit_is_aborted_and_not_cached()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Limits:ProcessingTimeout"] = "00:00:00.001" });

        var (response, _) = await app.GetAsync("/img/halves.jpg?width=3000&format=avif");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
        Assert.False(Directory.Exists(app.CacheDir) && Directory.EnumerateFiles(app.CacheDir, "*", SearchOption.AllDirectories).Any());
    }

    // ---- 4. Path tricks never reach files outside the root ----

    [Theory]
    [InlineData("/img/..%2F..%2Fsecret.jpg?width=10")]
    [InlineData("/img/%2e%2e/%2e%2e/secret.jpg?width=10")]
    [InlineData("/img/..%5C..%5Csecret.jpg?width=10")]
    [InlineData("/..%2Fsecret.jpg?width=10")]
    [InlineData("/img/halves.jpg:secret?width=10")]      // NTFS alternate data stream
    [InlineData("/img/halves.jpg%00.png?width=10")]      // null byte
    [InlineData("/img/con.jpg?width=10")]                // Windows device name
    [InlineData("/img/nul.jpg?width=10")]
    [InlineData("/C:/Windows/win.ini.jpg?width=10")]     // absolute path
    [InlineData("//server/share/x.jpg?width=10")]        // UNC
    public async Task Path_tricks_are_not_served(string url)
    {
        await using var app = await TestApp.StartAsync();
        await File.WriteAllBytesAsync(Path.Combine(app.Root, "secret.jpg"),
            await File.ReadAllBytesAsync(Path.Combine(app.WebRoot, "img", "halves.jpg"), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(app.Client.BaseAddress!, url));
        try
        {
            using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("null characters"))
        {
            // ASP.NET's URL decoder refuses %00 before any app code runs (Kestrel answers 400).
        }

        Assert.Equal(0, app.Processor.Calls);
    }

    [Fact]
    public async Task Hidden_files_in_the_web_root_are_not_served()
    {
        await using var app = await TestApp.StartAsync();
        File.Copy(Path.Combine(app.WebRoot, "img", "halves.jpg"), Path.Combine(app.WebRoot, "img", ".private.jpg"));

        var (response, _) = await app.GetAsync("/img/.private.jpg?width=10");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- 5. No metadata (GPS, camera, author) leaks into outputs ----

    [Fact]
    public async Task Exif_and_xmp_metadata_are_stripped()
    {
        await using var app = await TestApp.StartAsync();
        var path = Path.Combine(app.WebRoot, "img", "tagged.jpg");
        using (var plain = Image.Black(64, 64, bands: 3))
        using (var tagged = plain.Mutate(m =>
        {
            m.Set(GValue.GStrType, "exif-ifd0-Artist", "Secret Person");
            m.Set(GValue.GStrType, "exif-ifd0-ImageDescription", "home address");
        }))
            tagged.WriteToFile(path);
        using (var check = Image.NewFromFile(path)) Assert.True(check.Contains("exif-data")); // the source really has EXIF

        foreach (var format in new[] { "jpg", "webp", "png", "avif" })
        {
            var (_, body) = await app.GetAsync($"/img/tagged.jpg?width=32&format={format}");
            using var output = Image.NewFromBuffer(body);
            Assert.False(output.Contains("exif-data"), format);
            Assert.False(output.Contains("xmp-data"), format);
        }
    }

    // ---- 6. Risky configuration is refused or warned about ----

    [Fact]
    public async Task Short_hmac_keys_refuse_to_start()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(() => TestApp.StartAsync(new() { ["Bildverk:Security:HmacKey"] = "short" }));
    }

    [Fact]
    public void Startup_warns_about_abusable_configurations()
    {
        Assert.Contains(BildverkStartupCheck.Warnings(new BildverkOptions { Cache = { MaxSizeMB = 0 } }), w => w.Contains("fill the disk"));
        Assert.Contains(BildverkStartupCheck.Warnings(new BildverkOptions()), w => w.Contains("unsigned"));
        Assert.Contains(BildverkStartupCheck.Warnings(new BildverkOptions { Security = { HmacKey = new string('k', 20) } }), w => w.Contains("at least"));
        Assert.Empty(BildverkStartupCheck.Warnings(new BildverkOptions { Security = { HmacKey = new string('k', 40) } }));
        Assert.Empty(BildverkStartupCheck.Warnings(new BildverkOptions { Limits = { AllowedWidths = [320, 640] } }));
    }

    // ---- Output size: shapes can't be used to blow sizes up (found by bench/Fuzz) ----

    [Fact]
    public async Task Proportional_sizes_from_extreme_aspect_ratios_stay_within_limits()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Limits:MaxWidth"] = "1000", ["Bildverk:Limits:MaxHeight"] = "1000" });

        // height=400 of a 4000x4 strip would be 400 000 x 400.
        var (response, body) = await app.GetAsync("/img/strip.png?height=400");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var img = Image.NewFromBuffer(body);
        Assert.True(img.Width <= 1000 && img.Height <= 1000, $"{img.Width}x{img.Height}");
    }

    [Fact]
    public async Task Crops_needing_a_huge_intermediate_image_are_rejected()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Limits:MaxWidth"] = "500", ["Bildverk:Limits:MaxHeight"] = "500" });

        // Filling 500x500 from a 4000x4 strip needs a 500 000 x 500 intermediate.
        var (response, _) = await app.GetAsync("/img/strip.png?width=500&height=500&rmode=crop");

        Assert.Equal((HttpStatusCode)422, response.StatusCode);
    }

    [Fact]
    public async Task Format_conversion_without_resize_is_also_limited()
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Limits:MaxWidth"] = "1000" });

        var (_, body) = await app.GetAsync("/img/strip.png?format=webp");

        using var img = Image.NewFromBuffer(body);
        Assert.True(img.Width <= 1000, $"{img.Width}x{img.Height}");
    }

    // ---- Signed URLs can't be extended or redirected (cf. Thumbor CVE-2026-53501) ----

    [Theory]
    [InlineData("&width=4000")]        // a second width: the last one would win
    [InlineData("&rmode=stretch")]     // an extra command
    [InlineData("&format=png")]
    public async Task Signed_urls_reject_appended_commands(string appended)
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Security:HmacKey"] = "k-0123456789abcdef0123456789abcdef" });
        var signed = app.Services.GetRequiredService<Bildverk.Security.IBildverkUrlSigner>().Sign("/img/halves.jpg?width=100");

        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync(signed)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync(signed + appended)).Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.GetAsync(signed.Replace("/img/halves.jpg", "/img/alpha.png", StringComparison.Ordinal))).Response.StatusCode);
    }

    // ---- One cache entry per file, however the path is spelled ----

    [Fact]
    public async Task Path_spellings_of_the_same_file_share_one_cache_entry()
    {
        await using var app = await TestApp.StartAsync();

        await app.GetAsync("/img/halves.jpg?width=100");
        await app.GetAsync("/img//halves.jpg?width=100");
        var (upper, _) = await app.GetAsync("/IMG/HALVES.JPG?width=100");

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.Equal(HttpStatusCode.OK, upper.StatusCode);   // same file on a case-insensitive file system...
        else
            Assert.Equal(HttpStatusCode.NotFound, upper.StatusCode); // ...a different (missing) file on Linux
        Assert.Equal(1, app.Processor.Calls);                   // either way: processed once, cached once
    }

    [Fact]
    public async Task Backslashes_in_paths_are_rejected()
    {
        await using var app = await TestApp.StartAsync();

        var (response, _) = await app.GetAsync("/img%5Chalves.jpg?width=10");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- Load: waiting work is capped, cached images are unaffected ----

    [Fact]
    public async Task Too_many_waiting_requests_get_503_with_retry_after()
    {
        await using var app = await TestApp.StartAsync(new()
        {
            ["Bildverk:Limits:MaxConcurrentProcessing"] = "1",
            ["Bildverk:Limits:MaxQueuedRequests"] = "2",
        });
        await app.GetAsync("/img/halves.jpg?width=50"); // cached before the flood
        app.Processor.Delay = TimeSpan.FromMilliseconds(400);

        var flood = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => app.GetAsync($"/img/halves.jpg?width={100 + i}")));
        var (cached, _) = await app.GetAsync("/img/halves.jpg?width=50");

        var busy = flood.Where(r => r.Response.StatusCode == HttpStatusCode.ServiceUnavailable).ToList();
        Assert.NotEmpty(busy);
        Assert.All(busy, r => Assert.NotNull(r.Response.Headers.RetryAfter));
        Assert.True(flood.Count(r => r.Response.StatusCode == HttpStatusCode.OK) >= 3); // 1 running + 2 queued
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
    }

    // ---- TIFF is opt-in; odd channel layouts never reach an encoder ----

    [Fact]
    public async Task Tiff_is_off_by_default()
    {
        await using var app = await TestApp.StartAsync();

        var (response, _) = await app.GetAsync("/img/multiband.tif?width=32&format=png");

        // Not ours: static files serve the original untouched, no decoder ever runs on it.
        Assert.Equal(0, app.Processor.Calls);
        Assert.NotEqual("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/img/multiband.tif", "avif")]
    [InlineData("/img/multiband.tif", "webp")]
    [InlineData("/img/multiband.tif", "jpg")]
    [InlineData("/img/grey-alpha.png", "avif")]
    [InlineData("/img/grey-alpha.png", "webp")]
    [InlineData("/img/grey-alpha.png", "jpg")]
    public async Task Unusual_channel_layouts_become_rgb_or_rgba(string path, string format)
    {
        await using var app = await TestApp.StartAsync(new()
        {
            ["Bildverk:Extensions:0"] = ".tif",
            ["Bildverk:Extensions:1"] = ".png",
            ["Bildverk:Limits:AllowedInputFormats:0"] = "tiff",
            ["Bildverk:Limits:AllowedInputFormats:1"] = "png",
        });

        var (response, body) = await app.GetAsync($"{path}?width=32&format={format}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var img = Image.NewFromBuffer(body);
        Assert.Contains(img.Bands, new[] { 3, 4 });
        Assert.Equal(32, img.Width);
    }

    // ---- Query abuse: absurd values never crash or slip through ----

    [Theory]
    [InlineData("?width=99999999999999999999")]
    [InlineData("?width=-1&height=0")]
    [InlineData("?width=NaN&height=Infinity")]
    [InlineData("?width=1e309")]
    [InlineData("?rxy=NaN,NaN&width=10&height=10&rmode=crop")]
    [InlineData("?bgcolor=%00%00%00&width=10")]
    [InlineData("?format=..%2F..%2Fetc&width=10")]
    [InlineData("?width=10&width=20&width=30")]
    [InlineData("?quality=1000000&width=10")]
    public async Task Absurd_query_values_are_ignored_or_clamped(string query)
    {
        await using var app = await TestApp.StartAsync(new() { ["Bildverk:Limits:MaxWidth"] = "500" });

        var (response, body) = await app.GetAsync("/img/halves.jpg" + query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var img = Image.NewFromBuffer(body);
        Assert.InRange(img.Width, 1, 800);
    }
}

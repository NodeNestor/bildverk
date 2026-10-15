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

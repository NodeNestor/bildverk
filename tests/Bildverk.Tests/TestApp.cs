using Bildverk.Processing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetVips;

namespace Bildverk.Tests;

/// <summary>A real ASP.NET Core pipeline (Bildverk + static files) on TestServer, with a temp web root and cache.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    public string Root { get; }
    public string WebRoot => Path.Combine(Root, "wwwroot");
    public string CacheDir => Path.Combine(Root, "img-cache");
    public CountingProcessor Processor { get; }
    public ManualTime Time { get; }
    public HttpClient Client { get; }
    public IServiceProvider Services => _app.Services;

    private TestApp(WebApplication app, string root, CountingProcessor processor, ManualTime time)
    {
        _app = app;
        Root = root;
        Processor = processor;
        Time = time;
        Client = app.GetTestClient();
    }

    /// <param name="settings">Extra configuration keys, e.g. "Bildverk:Limits:MaxWidth" = "300" (as appsettings.json would supply).</param>
    public static async Task<TestApp> StartAsync(Dictionary<string, string?>? settings = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "bildverk-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "wwwroot", "img"));
        TestImages.WriteAll(Path.Combine(root, "wwwroot", "img"));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root,
            WebRootPath = "wwwroot",
            EnvironmentName = "Development",
        });
        builder.WebHost.UseTestServer();

        var config = new Dictionary<string, string?>
        {
            ["Bildverk:Cache:Path"] = "img-cache",
            ["Bildverk:Cache:CleanupInterval"] = "00:00:00",
            ["Bildverk:Cache:MaxSizeMB"] = "0",
        };
        foreach (var (k, v) in settings ?? []) config[k] = v;
        builder.Configuration.AddInMemoryCollection(config);

        var time = new ManualTime(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var processor = new CountingProcessor(new VipsImageProcessor());
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton<IImageProcessor>(processor);
        builder.Services.AddBildverk();

        var app = builder.Build();
        app.UseBildverk();
        app.UseStaticFiles();
        await app.StartAsync();
        return new TestApp(app, root, processor, time);
    }

    public async Task<(HttpResponseMessage Response, byte[] Body)> GetAsync(string url)
    {
        var response = await Client.GetAsync(url, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    public async Task<(HttpResponseMessage Response, byte[] Body)> GetAsync(string url, string? accept)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (accept is not null) request.Headers.TryAddWithoutValidation("Accept", accept);
        var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        return (response, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed class CountingProcessor(IImageProcessor inner) : IImageProcessor
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public System.Collections.Concurrent.ConcurrentQueue<ProcessingRequest> Requests { get; } = new();

    /// <summary>Optional artificial delay, to widen race windows in concurrency tests.</summary>
    public TimeSpan Delay { get; set; }

    public async Task<ProcessedImage> ProcessAsync(Stream source, ProcessingRequest request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        Requests.Enqueue(request);
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        return await inner.ProcessAsync(source, request, cancellationToken);
    }

    public Task<(int Width, int Height)?> IdentifyAsync(Stream source, CancellationToken cancellationToken) => inner.IdentifyAsync(source, cancellationToken);
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

internal static class TestImages
{
    /// <summary>
    /// halves.jpg  800×400, left half red, right half blue.
    /// alpha.png   200×100, fully opaque green.
    /// rotated.jpg 200×100 stored, EXIF orientation 6 (displays as 100×200).
    /// broken.jpg  not an image.
    /// </summary>
    public static void WriteAll(string dir)
    {
        using (var red = Image.Black(400, 400, bands: 3).NewFromImage(255, 0, 0))
        using (var blue = Image.Black(400, 400, bands: 3).NewFromImage(0, 0, 255))
        using (var halves = red.Join(blue, Enums.Direction.Horizontal))
            halves.WriteToFile(Path.Combine(dir, "halves.jpg"), new VOption { { "Q", 95 } });

        using (var green = Image.Black(200, 100, bands: 4).NewFromImage(0, 255, 0, 255).Copy(interpretation: Enums.Interpretation.Srgb))
            green.WriteToFile(Path.Combine(dir, "alpha.png"));

        using (var plain = Image.Black(200, 100, bands: 3).NewFromImage(200, 200, 200))
        using (var rotated = plain.Mutate(m => m.Set(GValue.GIntType, "orientation", 6)))
            rotated.WriteToFile(Path.Combine(dir, "rotated.jpg"));

        File.WriteAllText(Path.Combine(dir, "broken.jpg"), "this is not a jpeg");

        // Extreme aspect ratio: 4000 x 4. Proportional resizes of it explode without output limits.
        using (var strip = Image.Black(4000, 4, bands: 3).NewFromImage(90, 90, 90))
            strip.WriteToFile(Path.Combine(dir, "strip.png"));
        File.WriteAllBytes(Path.Combine(dir, "truncated.jpg"), File.ReadAllBytes(Path.Combine(dir, "halves.jpg"))[..600]);
        File.WriteAllText(Path.Combine(dir, "disguised.jpg"), "<svg xmlns='http://www.w3.org/2000/svg' width='10' height='10'><rect width='10' height='10'/></svg>");
    }

    public static Image Decode(byte[] bytes) => Image.NewFromBuffer(bytes);
}

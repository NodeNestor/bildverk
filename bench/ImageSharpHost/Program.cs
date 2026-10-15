using SixLabors.ImageSharp.Web.Caching;
using SixLabors.ImageSharp.Web.DependencyInjection;

// Usage: ImageSharpHost <webroot> <cacheDir> <url>   (stock defaults, only the cache folder is set)
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = args[0] });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls(args[2]);
builder.Services.AddImageSharp()
    .Configure<PhysicalFileSystemCacheOptions>(o =>
    {
        o.CacheRootPath = Path.GetDirectoryName(Path.GetFullPath(args[1]));
        o.CacheFolder = Path.GetFileName(Path.GetFullPath(args[1]));
    });

var app = builder.Build();
app.UseImageSharp();
app.UseStaticFiles();
app.Run();

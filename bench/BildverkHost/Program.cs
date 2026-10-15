using Bildverk;

// Usage: BildverkHost <webroot> <cacheDir> <url>
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = args[0] });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls(args[2]);
builder.Services.AddBildverk(o => o.Cache.Path = args[1]);

var app = builder.Build();
app.UseBildverk();
app.UseStaticFiles();
app.Run();

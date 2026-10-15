using Bildverk;
using Bildverk.Sample.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents();

// Reads the "Bildverk" section of appsettings.json.
builder.Services.AddBildverk();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Before static files: requests with ?width=... etc. are answered here, everything else falls through.
app.UseBildverk();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>();

app.Run();

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

namespace Bildverk;

/// <summary>
/// Refuses to start with a cache folder that overlaps the app, the web root or the image source.
/// The cache cleanup deletes files, so a misconfigured <see cref="CacheOptions.Path"/> such as "." or "wwwroot"
/// must never be possible. (The cleanup also only ever deletes files named like its own; this is the second fence.)
/// </summary>
internal sealed class BildverkPathValidator(IWebHostEnvironment env) : IValidateOptions<BildverkOptions>
{
    public ValidateOptionsResult Validate(string? name, BildverkOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Cache.Path)) return ValidateOptionsResult.Fail("Bildverk:Cache:Path must be set.");

        var cache = Full(Path.Combine(env.ContentRootPath, options.Cache.Path));
        if (Path.GetPathRoot(cache) is { } drive && Same(cache, Full(drive)))
            return ValidateOptionsResult.Fail($"Bildverk:Cache:Path '{cache}' is a filesystem root.");

        var protectedFolders = new List<(string What, string Path)> { ("the content root", Full(env.ContentRootPath)) };
        if (!string.IsNullOrEmpty(env.WebRootPath)) protectedFolders.Add(("the web root", Full(env.WebRootPath)));
        if (!string.IsNullOrWhiteSpace(options.Source.Root)) protectedFolders.Add(("Source:Root", Full(Path.Combine(env.ContentRootPath, options.Source.Root))));

        foreach (var (what, folder) in protectedFolders)
        {
            if (Same(cache, folder) || IsInside(folder, cache))
                return ValidateOptionsResult.Fail($"Bildverk:Cache:Path '{cache}' is or contains {what} ('{folder}'). Use a dedicated folder, e.g. App_Data/bildverk-cache.");

            // Inside the content root is fine (App_Data); inside the web root or image source is not:
            // processed files would be publicly downloadable or picked up as originals.
            if (what != "the content root" && IsInside(cache, folder))
                return ValidateOptionsResult.Fail($"Bildverk:Cache:Path '{cache}' is inside {what} ('{folder}'). Use a folder outside it.");
        }

        return ValidateOptionsResult.Success;
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison Comparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool Same(string a, string b) => string.Equals(a, b, Comparison);

    /// <summary>True when <paramref name="child"/> is strictly inside <paramref name="parent"/>.</summary>
    private static bool IsInside(string child, string parent) =>
        child.Length > parent.Length + 1
        && child.StartsWith(parent, Comparison)
        && (child[parent.Length] == Path.DirectorySeparatorChar || child[parent.Length] == Path.AltDirectorySeparatorChar
            || parent.EndsWith(Path.DirectorySeparatorChar));
}

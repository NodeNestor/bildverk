using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Bildverk.Tests;

/// <summary>The README's configuration block is the reference people copy, so it must be exactly right.</summary>
public sealed class DocsTests
{
    [Fact]
    public void Readme_configuration_block_binds_to_exactly_the_defaults()
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("{" + ReadmeConfigBlock() + "}")))
            .Build();

        var fromReadme = config.GetSection(BildverkOptions.SectionName).Get<BildverkOptions>(o => o.ErrorOnUnknownConfiguration = true)!;
        var defaults = new BildverkOptions
        {
            Extensions = [.. BildverkOptions.DefaultExtensions],
            Formats = { Preference = [.. FormatOptions.DefaultPreference] },
        };

        Assert.Equal(Json(defaults), Json(fromReadme));
    }

    [Fact]
    public void Every_option_is_documented_in_the_readme()
    {
        using var doc = JsonDocument.Parse("{" + ReadmeConfigBlock() + "}", new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var documented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(doc.RootElement.GetProperty(BildverkOptions.SectionName), "", documented);

        var missing = OptionPaths(typeof(BildverkOptions), "").Where(p => !documented.Contains(p)).ToList();
        Assert.True(missing.Count == 0, "Not in README: " + string.Join(", ", missing));
    }

    private static string ReadmeConfigBlock()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "README.md"))) dir = Path.GetDirectoryName(dir) ?? throw new FileNotFoundException("README.md");
        var readme = File.ReadAllText(Path.Combine(dir, "README.md"));
        var section = readme[readme.IndexOf("## Configuration", StringComparison.Ordinal)..];
        var start = section.IndexOf("```jsonc", StringComparison.Ordinal) + "```jsonc".Length;
        return section[start..section.IndexOf("```", start, StringComparison.Ordinal)];
    }

    private static string Json(BildverkOptions o) => JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });

    private static void Collect(JsonElement e, string prefix, HashSet<string> paths)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        foreach (var p in e.EnumerateObject())
        {
            var path = prefix + p.Name;
            paths.Add(path);
            Collect(p.Value, path + ":", paths);
        }
    }

    private static IEnumerable<string> OptionPaths(Type type, string prefix)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            var path = prefix + p.Name;
            var isLeaf = p.PropertyType.IsValueType || p.PropertyType == typeof(string) || typeof(IEnumerable).IsAssignableFrom(p.PropertyType);
            if (isLeaf) yield return path;
            else foreach (var nested in OptionPaths(p.PropertyType, path + ":")) yield return nested;
        }
    }
}

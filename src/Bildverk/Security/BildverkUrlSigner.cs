using System.Security.Cryptography;
using System.Text;
using Bildverk.Commands;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Bildverk.Security;

/// <summary>Adds &amp;hmac= signatures to image URLs when <see cref="SecurityOptions.HmacKey"/> is set.</summary>
public interface IBildverkUrlSigner
{
    /// <summary>
    /// Returns the URL with an hmac parameter appended, or unchanged when signing is disabled
    /// or the URL has no image commands. Accepts relative ("/img/a.jpg?width=100") or absolute URLs.
    /// </summary>
    string Sign(string url);
}

internal sealed class BildverkUrlSigner(IOptionsMonitor<BildverkOptions> options) : IBildverkUrlSigner
{
    public string Sign(string url)
    {
        var key = options.CurrentValue.Security.HmacKey;
        if (string.IsNullOrEmpty(key)) return url;

        var q = url.IndexOf('?');
        if (q < 0) return url;

        var path = PathOf(url[..q]);
        var query = new QueryCollection(QueryHelpers.ParseQuery(url[q..]));
        var commands = CommandParser.Parse(query);
        if (commands is null) return url;

        return $"{url}&{CommandParser.HmacParameter}={Compute(key, path, commands)}";
    }

    internal static string Compute(string key, string path, ImageCommands commands)
    {
        var payload = Encoding.UTF8.GetBytes($"{path}?{CommandParser.Canonicalize(commands)}");
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), payload);
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    internal static bool Verify(string key, string path, ImageCommands commands, string? provided)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        var expected = Encoding.ASCII.GetBytes(Compute(key, path, commands));
        var actual = Encoding.ASCII.GetBytes(provided.ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    // Same form as HttpRequest.Path.Value: unescaped, relative to the app.
    private static string PathOf(string urlWithoutQuery) => Uri.UnescapeDataString(
        Uri.TryCreate(urlWithoutQuery, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https"
            ? abs.AbsolutePath
            : urlWithoutQuery);
}

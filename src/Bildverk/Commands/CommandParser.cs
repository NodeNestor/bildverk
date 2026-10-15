using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Bildverk.Commands;

/// <summary>
/// Turns a query string into <see cref="ImageCommands"/>. Unknown parameters and invalid values
/// are ignored, so they never create extra cache entries.
/// </summary>
public static class CommandParser
{
    public const string HmacParameter = "hmac";

    /// <summary>Every query parameter the middleware understands.</summary>
    public static readonly IReadOnlySet<string> KnownCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "width", "height", "rmode", "ranchor", "rxy", "quality", "format", "bgcolor",
    };

    /// <summary>Returns null when the query contains no valid command (the request is not for us).</summary>
    public static ImageCommands? Parse(IQueryCollection query)
    {
        var c = new ImageCommands();
        var any = false;

        foreach (var (key, values) in query)
        {
            var value = Last(values);
            if (value is null) continue;

            switch (key.ToLowerInvariant())
            {
                case "width" when TryPositiveInt(value, out var w): c = c with { Width = w }; any = true; break;
                case "height" when TryPositiveInt(value, out var h): c = c with { Height = h }; any = true; break;
                case "rmode" when TryMode(value, out var m): c = c with { Mode = m }; any = true; break;
                case "ranchor" when TryAnchor(value, out var a): c = c with { Anchor = a }; any = true; break;
                case "rxy" when TryFocal(value, out var f): c = c with { Focal = f }; any = true; break;
                case "quality" when TryPositiveInt(value, out var q) && q <= 100: c = c with { Quality = q }; any = true; break;
                case "format" when value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase): c = c with { Format = null, AutoFormat = true }; any = true; break;
                case "format" when TryFormat(value, out var fmt): c = c with { Format = fmt, AutoFormat = false }; any = true; break;
                case "bgcolor" when TryColor(value, out var col): c = c with { Background = col }; any = true; break;
            }
        }

        return any ? c : null;
    }

    /// <summary>
    /// Stable, sorted text form of the commands. Two URLs that mean the same thing produce the same string,
    /// which is what makes it usable both as cache key and as the HMAC payload.
    /// </summary>
    public static string Canonicalize(ImageCommands c)
    {
        var sb = new StringBuilder();
        void Add(string k, string v) => sb.Append(sb.Length == 0 ? "" : "&").Append(k).Append('=').Append(v);

        // Alphabetical order.
        if (c.Background is { } bg) Add("bgcolor", bg.ToString());
        if (c.Format is { } f) Add("format", FormatName(f));
        else if (c.AutoFormat) Add("format", "auto");
        if (c.Height is { } h) Add("height", h.ToString(CultureInfo.InvariantCulture));
        if (c.Quality is { } q) Add("quality", q.ToString(CultureInfo.InvariantCulture));
        if (c.Anchor != Anchor.Center) Add("ranchor", c.Anchor.ToString().ToLowerInvariant());
        if (c.Mode != ResizeMode.Max) Add("rmode", c.Mode.ToString().ToLowerInvariant());
        if (c.Focal is { } xy) Add("rxy", $"{xy.X.ToString("0.####", CultureInfo.InvariantCulture)},{xy.Y.ToString("0.####", CultureInfo.InvariantCulture)}");
        if (c.Width is { } w) Add("width", w.ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static string FormatName(OutputFormat f) => f switch
    {
        OutputFormat.Jpeg => "jpg",
        OutputFormat.Png => "png",
        OutputFormat.WebP => "webp",
        OutputFormat.Gif => "gif",
        OutputFormat.Avif => "avif",
        OutputFormat.Tiff => "tiff",
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    public static bool TryFormat(string value, out OutputFormat format)
    {
        switch (value.Trim().TrimStart('.').ToLowerInvariant())
        {
            case "jpg" or "jpeg": format = OutputFormat.Jpeg; return true;
            case "png": format = OutputFormat.Png; return true;
            case "webp": format = OutputFormat.WebP; return true;
            case "gif": format = OutputFormat.Gif; return true;
            case "avif": format = OutputFormat.Avif; return true;
            case "tif" or "tiff": format = OutputFormat.Tiff; return true;
            default: format = default; return false;
        }
    }

    private static string? Last(StringValues values) => values.Count == 0 ? null : values[values.Count - 1];

    private static bool TryPositiveInt(string value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result > 0
        // Also accept "1920.0"-style values that some CMSes emit.
        || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 1 && d <= int.MaxValue && (result = (int)Math.Round(d)) > 0;

    private static bool TryMode(string value, out ResizeMode mode) =>
        Enum.TryParse(value.Trim(), ignoreCase: true, out mode) && Enum.IsDefined(mode) && !int.TryParse(value, out _);

    private static bool TryAnchor(string value, out Anchor anchor)
    {
        var v = value.Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
        if (v == "middle" || v == "centre") v = "center";
        return Enum.TryParse(v, ignoreCase: true, out anchor) && Enum.IsDefined(anchor) && !int.TryParse(v, out _);
    }

    private static bool TryFocal(string value, out (double X, double Y) focal)
    {
        focal = default;
        var parts = value.Split(',');
        if (parts.Length != 2) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
        if (x is < 0 or > 1 || y is < 0 or > 1 || double.IsNaN(x) || double.IsNaN(y)) return false;
        focal = (x, y);
        return true;
    }

    private static bool TryColor(string value, out Rgba color)
    {
        var v = value.Trim().TrimStart('#').ToLowerInvariant();
        switch (v)
        {
            case "transparent": color = Rgba.Transparent; return true;
            case "white": color = Rgba.White; return true;
            case "black": color = new(0, 0, 0, 255); return true;
            case "red": color = new(255, 0, 0, 255); return true;
            case "green": color = new(0, 128, 0, 255); return true;
            case "blue": color = new(0, 0, 255, 255); return true;
            case "gray" or "grey": color = new(128, 128, 128, 255); return true;
        }

        color = default;
        if (v.Length is 3 or 4) v = string.Concat(v.Select(ch => new string(ch, 2)));
        if (v.Length == 6) v += "ff";
        if (v.Length != 8 || !uint.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n)) return false;
        color = new((byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n);
        return true;
    }
}

namespace Bildverk.Commands;

public enum ResizeMode
{
    /// <summary>Fit inside the box, keep aspect ratio (default).</summary>
    Max,
    /// <summary>Fill the box, cut away what overflows.</summary>
    Crop,
    /// <summary>Fit inside the box, pad the rest with the background colour.</summary>
    Pad,
    /// <summary>Like pad, but never upscales.</summary>
    BoxPad,
    /// <summary>Shortest side reaches the box, never upscales, no cropping.</summary>
    Min,
    /// <summary>Exact box size, aspect ratio ignored.</summary>
    Stretch,
}

public enum Anchor
{
    Center, Top, Bottom, Left, Right, TopLeft, TopRight, BottomLeft, BottomRight,
}

public enum OutputFormat
{
    Jpeg, Png, WebP, Gif, Avif, Tiff,
}

/// <summary>A colour as 8-bit RGBA.</summary>
public readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    public static readonly Rgba White = new(255, 255, 255, 255);
    public static readonly Rgba Transparent = new(0, 0, 0, 0);

    public override string ToString() => $"{R:x2}{G:x2}{B:x2}{A:x2}";
}

/// <summary>Validated, normalised commands for one request.</summary>
public sealed record ImageCommands
{
    public int? Width { get; init; }
    public int? Height { get; init; }
    public ResizeMode Mode { get; init; } = ResizeMode.Max;
    public Anchor Anchor { get; init; } = Anchor.Center;
    /// <summary>Focal point in 0–1 coordinates; overrides <see cref="Anchor"/> when set.</summary>
    public (double X, double Y)? Focal { get; init; }
    public int? Quality { get; init; }
    public OutputFormat? Format { get; init; }
    public Rgba? Background { get; init; }

    /// <summary><c>format=auto</c>: pick the best format the browser accepts (see FormatOptions).</summary>
    public bool AutoFormat { get; init; }

    /// <summary>Internal (never parsed from the URL): skip resizing when it would upscale.</summary>
    internal bool NoUpscale { get; init; }

    public bool HasResize => Width is not null || Height is not null;
}

using Bildverk.Commands;

namespace Bildverk.Processing;

/// <summary>
/// Pure geometry: scale the (orientation-corrected) source to ScaledWidth × ScaledHeight,
/// then optionally crop or pad to the final box. Kept separate from libvips so it can be unit-tested.
/// </summary>
public readonly record struct ResizePlan(int ScaledWidth, int ScaledHeight, Box? Crop, Box? Pad)
{
    public int OutputWidth => Crop?.Width ?? Pad?.Width ?? ScaledWidth;
    public int OutputHeight => Crop?.Height ?? Pad?.Height ?? ScaledHeight;

    public static ResizePlan Create(int sourceWidth, int sourceHeight, ImageCommands c)
    {
        var plan = CreateCore(sourceWidth, sourceHeight, c);
        return c.NoUpscale && (plan.ScaledWidth > sourceWidth || plan.ScaledHeight > sourceHeight)
            ? new ResizePlan(sourceWidth, sourceHeight, null, null)
            : plan;
    }

    private static ResizePlan CreateCore(int sourceWidth, int sourceHeight, ImageCommands c)
    {
        var none = new ResizePlan(sourceWidth, sourceHeight, null, null);
        if (!c.HasResize) return none;

        var noUpscale = c.Mode is ResizeMode.Min or ResizeMode.BoxPad;

        // Only one side given: proportional, every mode behaves the same.
        if (c.Width is null || c.Height is null)
        {
            var s = c.Width is { } w ? (double)w / sourceWidth : (double)c.Height!.Value / sourceHeight;
            if (noUpscale && s >= 1) return none;
            return new(Round(sourceWidth * s), Round(sourceHeight * s), null, null);
        }

        int W = c.Width.Value, H = c.Height.Value;
        double sx = (double)W / sourceWidth, sy = (double)H / sourceHeight;
        double fit = Math.Min(sx, sy), fill = Math.Max(sx, sy);

        switch (c.Mode)
        {
            case ResizeMode.Stretch:
                return new(W, H, null, null);

            case ResizeMode.Max:
                return new(Round(sourceWidth * fit), Round(sourceHeight * fit), null, null);

            case ResizeMode.Min:
                return fill >= 1 ? none : new(Round(sourceWidth * fill), Round(sourceHeight * fill), null, null);

            case ResizeMode.Crop:
            {
                int sw = Math.Max(W, Round(sourceWidth * fill)), sh = Math.Max(H, Round(sourceHeight * fill));
                var (fx, fy) = c.Focal ?? Factors(c.Anchor);
                // A focal point is centred where possible; an anchor pins the edge.
                int left = c.Focal is null ? Round((sw - W) * fx, 0) : (int)Math.Round(fx * sw - W / 2.0);
                int top = c.Focal is null ? Round((sh - H) * fy, 0) : (int)Math.Round(fy * sh - H / 2.0);
                return new(sw, sh, new Box(Math.Clamp(left, 0, sw - W), Math.Clamp(top, 0, sh - H), W, H), null);
            }

            case ResizeMode.Pad:
            case ResizeMode.BoxPad:
            {
                var s = c.Mode == ResizeMode.BoxPad ? Math.Min(fit, 1) : fit;
                int sw = Math.Min(W, Round(sourceWidth * s)), sh = Math.Min(H, Round(sourceHeight * s));
                var (fx, fy) = Factors(c.Anchor);
                return new(sw, sh, null, new Box(Round((W - sw) * fx, 0), Round((H - sh) * fy, 0), W, H));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(c), c.Mode, null);
        }
    }

    /// <summary>
    /// Applies the output limits to a plan. Sizes derived from the aspect ratio (only one side requested, or no
    /// resize at all) are scaled down to fit <paramref name="maxWidth"/> × <paramref name="maxHeight"/>; a source
    /// with an extreme shape (say 500 000 × 60) could otherwise turn <c>height=350</c> into a billion-pixel image.
    /// Returns null when even the intermediate image of a crop/pad would be unreasonably large.
    /// </summary>
    public static ResizePlan? Limit(ResizePlan plan, int maxWidth, int maxHeight)
    {
        if (plan.Crop is null && plan.Pad is null && (plan.ScaledWidth > maxWidth || plan.ScaledHeight > maxHeight))
        {
            var s = Math.Min((double)maxWidth / plan.ScaledWidth, (double)maxHeight / plan.ScaledHeight);
            plan = new ResizePlan(Round(plan.ScaledWidth * s), Round(plan.ScaledHeight * s), null, null);
        }

        // The scaled image before cropping/padding is held by libvips too: bound it like an output.
        return (long)plan.ScaledWidth * plan.ScaledHeight > 4L * maxWidth * maxHeight ? null : plan;
    }

    private static (double X, double Y) Factors(Anchor a) => a switch
    {
        Anchor.TopLeft => (0, 0),
        Anchor.Top => (0.5, 0),
        Anchor.TopRight => (1, 0),
        Anchor.Left => (0, 0.5),
        Anchor.Right => (1, 0.5),
        Anchor.BottomLeft => (0, 1),
        Anchor.Bottom => (0.5, 1),
        Anchor.BottomRight => (1, 1),
        _ => (0.5, 0.5),
    };

    private static int Round(double v, int min = 1) => Math.Max(min, (int)Math.Round(v, MidpointRounding.AwayFromZero));
}

/// <summary>A rectangle. For crops: area cut out of the scaled image. For pads: where the scaled image sits on the canvas.</summary>
public readonly record struct Box(int Left, int Top, int Width, int Height);

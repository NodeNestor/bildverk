using System.Diagnostics;
using NetVips;
using VImage = NetVips.Image;

// Fair codec comparison: how many bytes does each encoder/setting need to reach the SAME visual quality (SSIM),
// and how long does it take? Every encoder gets the identical reference pixels (resize done once, by libvips).
// Usage: Codecs <imagesDir>
NetVips.NetVips.Concurrency = 1;
NetVips.Cache.Max = 0;
var dir = args[0];
// Usage: Codecs <imagesDir> [file ...]   (env CODEC_WIDTHS=1200,400 to change sizes)
string[] sources = args.Length > 1 ? args[1..] : ["carpet012-24mp.jpg", "grass004-24mp.jpg", "asphalt025a-24mp.jpg"];
int[] widths = Environment.GetEnvironmentVariable("CODEC_WIDTHS") is { } cw ? cw.Split(',').Select(int.Parse).ToArray() : [1920, 400];
int[] qs = [30, 40, 50, 60, 70, 80, 90];

var encoders = new List<Encoder>
{
    new("vips jpeg baseline+optimize (default)", (img, q) => img.WriteToBuffer($".jpg[Q={q},optimize_coding=true,keep=none]")),
    new("vips jpeg plain", (img, q) => img.WriteToBuffer($".jpg[Q={q},keep=none]")),
    new("vips jpeg progressive", (img, q) => img.WriteToBuffer($".jpg[Q={q},optimize_coding=true,interlace=true,keep=none]")),
    new("vips jpeg mozjpeg-trellis", (img, q) => img.WriteToBuffer($".jpg[Q={q},optimize_coding=true,interlace=true,trellis_quant=true,overshoot_deringing=true,optimize_scans=true,quant_table=3,keep=none]")),
    new("vips webp effort 0 (default)", (img, q) => img.WriteToBuffer($".webp[Q={q},effort=0,keep=none]")),
    new("vips webp effort 2", (img, q) => img.WriteToBuffer($".webp[Q={q},effort=2,keep=none]")),
    new("vips webp effort 4", (img, q) => img.WriteToBuffer($".webp[Q={q},effort=4,keep=none]")),
    new("vips webp effort 6", (img, q) => img.WriteToBuffer($".webp[Q={q},effort=6,keep=none]"), Repeats: 1),
    new("vips webp effort 4 + smart_subsample", (img, q) => img.WriteToBuffer($".webp[Q={q},effort=4,smart_subsample=true,keep=none]")),
    new("vips avif effort 0 (default)", (img, q) => img.WriteToBuffer($".avif[Q={q},effort=0,keep=none]"), Repeats: 1),
    new("vips avif effort 2", (img, q) => img.WriteToBuffer($".avif[Q={q},effort=2,keep=none]"), Repeats: 1),
    new("vips avif effort 4", (img, q) => img.WriteToBuffer($".avif[Q={q},effort=4,keep=none]"), Repeats: 1, Qs: [40, 50, 60, 70, 80, 90]),
};

// results[encoder] -> list of (image, bytesAtTarget, msAtTarget)
var results = encoders.ToDictionary(e => e.Name, _ => new List<(double Bytes, double Ms)>());

foreach (var file in sources)
foreach (var width in widths)
{
    using var reference = VImage.ThumbnailBuffer(File.ReadAllBytes(Path.Combine(dir, file)), width).CopyMemory();
    using var refGrey = Grey(reference);

    // Target = what our default JPEG reaches at quality 80 ("normal web quality").
    var target = Ssim(refGrey, encoders[0].Encode(reference, 80));
    Console.WriteLine($"\n== {file} @ {reference.Width}x{reference.Height}   target SSIM {target:0.0000} (= default JPEG q80)");

    foreach (var e in encoders)
    {
        var curve = new List<(int Q, double Ssim, int Bytes, double Ms)>();
        foreach (var q in e.Qs ?? qs)
        {
            byte[] data = [];
            var best = double.MaxValue;
            for (var i = 0; i < e.Repeats; i++)
            {
                var sw = Stopwatch.StartNew();
                data = e.Encode(reference, q);
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }
            curve.Add((q, Ssim(refGrey, data), data.Length, best));
        }

        var (bytes, ms) = AtTarget(curve, target);
        results[e.Name].Add((bytes, ms));
        var at80 = curve.FirstOrDefault(p => p.Q == 80);
        Console.WriteLine($"  {e.Name,-40} {(double.IsNaN(bytes) ? "   out of range" : $"{bytes / 1024,8:0.0} KB")}  {ms,7:0.0} ms   [at q80: {at80.Bytes / 1024.0,6:0.0} KB, SSIM {at80.Ssim:0.0000}]");
    }
}

// Summary: geometric mean of bytes relative to the default JPEG, at equal SSIM.
Console.WriteLine("\n== Summary over all images/sizes: bytes at EQUAL quality (SSIM of default JPEG q80), relative to default JPEG");
var baseline = results[encoders[0].Name];
foreach (var e in encoders)
{
    var r = results[e.Name];
    var pairs = r.Zip(baseline).Where(p => !double.IsNaN(p.First.Bytes)).ToList();
    if (pairs.Count == 0) { Console.WriteLine($"  {e.Name,-40} never reaches the target quality"); continue; }
    var rel = Math.Exp(pairs.Average(p => Math.Log(p.First.Bytes / p.Second.Bytes)));
    var msRel = Math.Exp(pairs.Average(p => Math.Log(p.First.Ms / p.Second.Ms)));
    Console.WriteLine($"  {e.Name,-40} size {rel * 100,6:0.0} %   encode time {msRel,6:0.0}x   ({pairs.Count}/{r.Count} in range)");
}

static (double Bytes, double Ms) AtTarget(List<(int Q, double Ssim, int Bytes, double Ms)> curve, double target)
{
    var c = curve.OrderBy(p => p.Ssim).ToList();
    for (var i = 0; i < c.Count - 1; i++)
    {
        if (target < c[i].Ssim || target > c[i + 1].Ssim) continue;
        var t = (target - c[i].Ssim) / Math.Max(1e-9, c[i + 1].Ssim - c[i].Ssim);
        // Interpolate size in log space, time linearly.
        var bytes = Math.Exp(Math.Log(c[i].Bytes) + t * (Math.Log(c[i + 1].Bytes) - Math.Log(c[i].Bytes)));
        return (bytes, c[i].Ms + t * (c[i + 1].Ms - c[i].Ms));
    }
    return (double.NaN, double.NaN);
}

static VImage Grey(VImage img) => img.Colourspace(Enums.Interpretation.Bw)[0].Cast(Enums.BandFormat.Float);

// Standard SSIM (Wang et al.) on luminance, 11-tap Gaussian sigma 1.5.
static double Ssim(VImage refGrey, byte[] encoded)
{
    using var decoded = VImage.NewFromBuffer(encoded);
    using var y = Grey(decoded.Bands > 3 ? decoded.ExtractBand(0, n: 3) : decoded);
    var x = refGrey;
    const double c1 = 6.5025, c2 = 58.5225; // (0.01*255)^2, (0.03*255)^2
    VImage Blur(VImage i) => i.Gaussblur(1.5, precision: Enums.Precision.Float);
    using var muX = Blur(x);
    using var muY = Blur(y);
    using var sxx = Blur(x * x) - muX * muX;
    using var syy = Blur(y * y) - muY * muY;
    using var sxy = Blur(x * y) - muX * muY;
    using var num = (muX * muY * 2 + c1) * (sxy * 2 + c2);
    using var den = (muX * muX + muY * muY + c1) * (sxx + syy + c2);
    using var map = num / den;
    return map.Avg();
}


record Encoder(string Name, Func<VImage, int, byte[]> Encode, int Repeats = 3, int[]? Qs = null);

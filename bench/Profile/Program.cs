using System.Diagnostics;
using NetVips;

// Times each stage of the cold path (decode+resize, encode) for one image with single-threaded libvips,
// as the server runs it. Usage: Profile <image> [width ...]
NetVips.NetVips.Concurrency = 1;
NetVips.Cache.Max = 0; // no operation cache: every iteration does the real work

var path = args[0];
int[] widths = args.Length > 1 ? args[1..].Select(int.Parse).ToArray() : [300, 1200, 1920];
var bytes = File.ReadAllBytes(path);

Console.WriteLine($"{Path.GetFileName(path)} ({bytes.Length / 1024} KB), best of 9, libvips {NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}");
Console.WriteLine($"  read file          {Time(() => File.ReadAllBytes(path)),7:0.0} ms");
Console.WriteLine($"  header only        {Time(() => { using var h = Image.NewFromBuffer(bytes); _ = h.Width; }),7:0.0} ms");

foreach (var w in widths)
{
    using var resized = Image.ThumbnailBuffer(bytes, w, height: w).CopyMemory();
    Console.WriteLine($"-- {resized.Width}x{resized.Height}");
    Console.WriteLine($"  decode + resize    {Time(() => { using var t = Image.ThumbnailBuffer(bytes, w, height: w); using var m = t.CopyMemory(); }),7:0.0} ms");
    foreach (var (name, options) in new[]
    {
        ("jpeg baseline", ".jpg[Q=80,optimize_coding=true,keep=none]"),
        ("jpeg progressive", ".jpg[Q=80,optimize_coding=true,interlace=true,keep=none]"),
        ("webp effort 0", ".webp[Q=80,effort=0,keep=none]"),
        ("webp effort 4", ".webp[Q=80,effort=4,keep=none]"),
        ("avif effort 0", ".avif[Q=80,effort=0,keep=none]"),
    })
    {
        var size = resized.WriteToBuffer(options).Length;
        Console.WriteLine($"  {name,-18} {Time(() => resized.WriteToBuffer(options)),7:0.0} ms  {size / 1024,6} KB");
    }
}

static double Time(Action action)
{
    var best = double.MaxValue;
    for (var i = 0; i < 9; i++)
    {
        var sw = Stopwatch.StartNew();
        action();
        best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
    }
    return best;
}

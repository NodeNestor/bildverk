using Bildverk.Internal;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Bildverk.Sources;

/// <summary>
/// Reads originals from <see cref="SourceOptions.Root"/> (or wwwroot). Goes through
/// <see cref="IFileProvider"/>, which refuses paths that escape the root ("../").
/// </summary>
public sealed class FileSystemImageSource : IImageSource, IDisposable
{
    private readonly IFileProvider _files;
    private readonly PhysicalFileProvider? _owned;

    public FileSystemImageSource(IOptions<BildverkOptions> options, IWebHostEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(env);
        var root = options.Value.Source.Root;
        if (string.IsNullOrWhiteSpace(root))
        {
            _files = env.WebRootFileProvider;
        }
        else
        {
            _owned = new PhysicalFileProvider(Path.GetFullPath(Path.Combine(env.ContentRootPath, root)));
            _files = _owned;
        }
    }

    public void Dispose() => _owned?.Dispose();

    public Task<SourceImage?> GetAsync(string path, CancellationToken cancellationToken)
    {
        var file = _files.GetFileInfo(path);
        if (!file.Exists || file.IsDirectory) return Task.FromResult<SourceImage?>(null);

        return Task.FromResult<SourceImage?>(new SourceImage(
            file.LastModified.ToUniversalTime(),
            file.Length,
            _ => Task.FromResult(file.CreateReadStream())));
    }
}

using Bildverk.Internal;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bildverk.Caching;

/// <summary>
/// Disk cache. Layout for key "3fa9c01b...":
/// <code>
/// {Cache.Path}/3/f/3fa9c01b....json        metadata, points to the current image file
/// {Cache.Path}/3/f/3fa9c01b....18f3a2c4e1  the image (suffix = write version)
/// </code>
/// Every write goes to a NEW file name and then the metadata is switched over, so a file that is being
/// downloaded is never overwritten (Windows refuses that) and readers never see half-written data.
/// Superseded files are deleted right away when possible, otherwise by the cleanup.
/// </summary>
public sealed class FileSystemImageCache(
    IOptionsMonitor<BildverkOptions> options,
    IWebHostEnvironment env,
    TimeProvider time,
    ILogger<FileSystemImageCache> logger) : IImageCache
{
    private const string MetaExtension = ".json";
    private const string TempExtension = ".tmp";
    private const int MaxEntriesInMemory = 100_000;
    private static readonly TimeSpan AccessTouchInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The cleanup only ever deletes files named exactly like the ones this class writes:
    // "{key}.json", "{key}.{version}" and their "….{guid}.tmp" temp files. Anything else is left alone,
    // even if the cache folder is misconfigured to point at other data.
    private static readonly Regex OwnMetaFile = new("^[0-9a-f]{16,64}\\.json$", RegexOptions.CultureInvariant);
    private static readonly Regex OwnFile = new("^[0-9a-f]{16,64}\\.(json|[0-9a-f]{1,16})(\\.[0-9a-f]{32}\\.tmp)?$", RegexOptions.CultureInvariant);
    private static readonly Regex OwnFolder = new("^[0-9a-f]$", RegexOptions.CultureInvariant);

    // Recently used entries, so a cache hit costs one file stat instead of reading and parsing JSON.
    private readonly ConcurrentDictionary<string, StoredEntry> _entries = new(StringComparer.Ordinal);
    private long _version;

    /// <summary>What the .json file holds: the public metadata plus the name of the current image file.</summary>
    private sealed record StoredEntry(CacheEntryMetadata Metadata, string File);

    /// <summary>Absolute cache folder for the current configuration.</summary>
    public string RootPath => Path.GetFullPath(Path.Combine(env.ContentRootPath, options.CurrentValue.Cache.Path));

    public async Task<CachedImage?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var dir = Folder(key);
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = await ReadEntryAsync(Path.Combine(dir, key + MetaExtension), cancellationToken);
            if (entry is null) return null;
            Remember(key, entry);
        }

        var data = new FileInfo(Path.Combine(dir, entry.File));
        if (!data.Exists)
        {
            _entries.TryRemove(key, out _);
            return null;
        }

        Touch(data);
        return Hit(data.FullName, data.Length, entry.Metadata);
    }

    public async Task<CachedImage> SetAsync(string key, ReadOnlyMemory<byte> data, CacheEntryMetadata metadata, CancellationToken cancellationToken)
    {
        var dir = Folder(key);
        var metaPath = Path.Combine(dir, key + MetaExtension);
        var previous = _entries.TryGetValue(key, out var known) ? known : await ReadEntryAsync(metaPath, cancellationToken);

        var entry = new StoredEntry(metadata, $"{key}.{NextVersion():x}");
        var dataPath = Path.Combine(dir, entry.File);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(dir);
                await WriteAtomicAsync(dataPath, data, cancellationToken);
                await WriteAtomicAsync(metaPath, JsonSerializer.SerializeToUtf8Bytes(entry, Json), cancellationToken);
                break;
            }
            catch (DirectoryNotFoundException) when (attempt == 0)
            {
                // Cleanup removed the (empty) folder between CreateDirectory and the write: try once more.
            }
        }

        Remember(key, entry);
        if (previous is not null && previous.File != entry.File)
            TryDelete(Path.Combine(dir, previous.File)); // may be in use by a download; cleanup gets it later

        return Hit(dataPath, data.Length, metadata);
    }

    public async Task<CleanupResult> CleanupAsync(CancellationToken cancellationToken)
    {
        var root = RootPath;
        if (!Directory.Exists(root)) return new CleanupResult(0, 0, 0, 0);

        var cache = options.CurrentValue.Cache;
        var now = time.GetUtcNow();
        int expired = 0, evicted = 0, orphans = 0;

        var files = new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        var live = new List<(string Key, FileInfo Data, string MetaPath)>();
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var meta in files.Where(f => OwnMetaFile.IsMatch(f.Name)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = meta.Name[..^MetaExtension.Length];
            var entry = await ReadEntryAsync(meta.FullName, cancellationToken);
            var data = entry is null ? null : new FileInfo(Path.Combine(meta.DirectoryName!, entry.File));

            if (entry is null || !data!.Exists || !OwnFile.IsMatch(entry.File))
            {
                // Unreadable, or its image is gone: drop it once it is clearly not mid-write.
                if (now - meta.LastWriteTimeUtc > OrphanGrace && TryDelete(meta.FullName)) { Forget(key); orphans++; }
                continue;
            }

            if (now - entry.Metadata.CachedAtUtc > cache.MaxAge)
            {
                if (TryDelete(meta.FullName)) { TryDelete(data.FullName); Forget(key); expired++; }
                continue;
            }

            referenced.Add(data.FullName);
            live.Add((key, data, meta.FullName));
        }

        // Superseded versions, crashed writes and temp files nobody points to.
        foreach (var f in files.Where(f => OwnFile.IsMatch(f.Name) && !OwnMetaFile.IsMatch(f.Name) && !referenced.Contains(f.FullName)))
        {
            if (now - f.LastWriteTimeUtc > OrphanGrace && TryDelete(f.FullName)) orphans++;
        }

        long total = live.Sum(e => e.Data.Length);
        if (cache.MaxSizeMB > 0)
        {
            var limit = cache.MaxSizeMB * 1024 * 1024;
            if (total > limit)
            {
                // Evict least recently used down to 90 % of the limit, so we don't evict again on the next run.
                var target = limit * 9 / 10;
                foreach (var (key, data, metaPath) in live.OrderBy(e => e.Data.LastAccessTimeUtc))
                {
                    if (total <= target) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (TryDelete(metaPath))
                    {
                        TryDelete(data.FullName);
                        Forget(key);
                        total -= data.Length;
                        evicted++;
                    }
                }
            }
        }

        RemoveEmptyDirectories(root);
        return new CleanupResult(expired, evicted, orphans, total);
    }

    private async Task<StoredEntry?> ReadEntryAsync(string metaPath, CancellationToken ct)
    {
        try
        {
            await using var s = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = await JsonSerializer.DeserializeAsync<StoredEntry>(s, Json, ct);
            return entry is { Metadata: not null, File.Length: > 0 } && IsPlainFileName(entry.File) ? entry : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Being replaced or corrupt: treat as a miss, the entry gets rewritten.
            Log.UnreadableMetadata(logger, ex, metaPath);
            return null;
        }
    }

    // The metadata only ever names a file in its own folder.
    private static bool IsPlainFileName(string name) => name.IndexOfAny(['/', '\\', ':']) < 0 && name is not ("." or "..");

    private string Folder(string key)
    {
        var depth = Math.Clamp(options.CurrentValue.Cache.FolderDepth, 0, Math.Min(8, key.Length));
        var parts = new string[depth + 1];
        parts[0] = RootPath;
        for (var i = 0; i < depth; i++) parts[i + 1] = key[i].ToString(CultureInfo.InvariantCulture);
        return Path.Combine(parts);
    }

    // Unique and increasing across restarts and servers sharing the folder (time-based, bumped on collisions).
    private long NextVersion()
    {
        var now = time.GetUtcNow().UtcTicks;
        while (true)
        {
            var last = Interlocked.Read(ref _version);
            var next = Math.Max(now, last + 1);
            if (Interlocked.CompareExchange(ref _version, next, last) == last) return next;
        }
    }

    // Record use for LRU eviction. Explicit and throttled, because many systems don't update atime themselves.
    private void Touch(FileInfo data)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (now - data.LastAccessTimeUtc <= AccessTouchInterval) return;
        try { data.LastAccessTimeUtc = now; } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Remember(string key, StoredEntry entry)
    {
        if (_entries.Count >= MaxEntriesInMemory) _entries.Clear();
        _entries[key] = entry;
    }

    private void Forget(string key) => _entries.TryRemove(key, out _);

    private static CachedImage Hit(string dataPath, long length, CacheEntryMetadata meta) => new(
        meta,
        length,
        meta.CachedAtUtc,
        dataPath,
        _ => Task.FromResult<Stream>(new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, useAsync: true)));

    private static async Task WriteAtomicAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var temp = $"{path}.{Guid.NewGuid():N}{TempExtension}";
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await fs.WriteAsync(bytes, ct);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!OwnFolder.IsMatch(Path.GetFileName(dir))) continue; // only our one-character shard folders
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

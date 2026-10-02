using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdditionalMaterial.Services;

/// <summary>A .zip seen in a folder when the folder was last listed.</summary>
/// <param name="Name">File name.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="IsLink">Whether it is a symbolic link (never served).</param>
public sealed record IndexedZip(string Name, long Size, bool IsLink);

/// <summary>
/// Remembers which folders hold .zip files, so pages are answered without touching the disk.
/// On Unraid and similar systems the first listing after a quiet spell waits for drives to spin up
/// (seconds per course). The index is built at startup and after each library scan, both of which
/// read the disks anyway; entries older than the configured age are served as they are and
/// re-listed in the background, so no timer ever wakes a sleeping disk.
/// </summary>
public sealed class MaterialIndex
{
    private readonly ConcurrentDictionary<string, (IReadOnlyDictionary<string, IndexedZip> Zips, DateTimeOffset Listed)> _folders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _refreshing = new(StringComparer.Ordinal);
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MaterialIndex> _logger;
    private int _rebuilding;

    /// <summary>Initializes a new instance of the <see cref="MaterialIndex"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="logger">Logger.</param>
    public MaterialIndex(ILibraryManager libraryManager, ILogger<MaterialIndex> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Gets the number of folders currently indexed.</summary>
    public int FolderCount => _folders.Count;

    /// <summary>
    /// The zips in <paramref name="directory"/>. Unknown folders are listed now (once); stale ones
    /// are returned as they are and re-listed in the background.
    /// </summary>
    /// <param name="directory">An absolute folder path.</param>
    /// <returns>Zips by lower-case name.</returns>
    public IReadOnlyDictionary<string, IndexedZip> Zips(string directory)
    {
        if (_folders.TryGetValue(directory, out var entry))
        {
            var maxAge = TimeSpan.FromMinutes(Math.Max(0, Plugin.Instance?.Configuration.IndexRefreshMinutes ?? 10));
            if (DateTimeOffset.UtcNow - entry.Listed > maxAge && _refreshing.TryAdd(directory, 0))
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        List(directory);
                    }
                    finally
                    {
                        _refreshing.TryRemove(directory, out _);
                    }
                });
            }

            return entry.Zips;
        }

        return List(directory);
    }

    /// <summary>Lists every folder of the enabled libraries. Runs at startup and after library scans.</summary>
    /// <param name="progress">Progress, 0–100.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public Task RebuildAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _rebuilding, 1) == 1)
        {
            return Task.CompletedTask;
        }

        try
        {
            var enabled = (Plugin.Instance?.Configuration.EnabledLibraryIds ?? Array.Empty<string>())
                .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToHashSet();
            var roots = _libraryManager.GetUserRootFolder().Children
                .OfType<CollectionFolder>()
                .Where(f => enabled.Contains(f.Id))
                .SelectMany(f => f.PhysicalLocations)
                .Where(Directory.Exists)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var done = 0;
            foreach (var root in roots)
            {
                foreach (var dir in Walk(root, cancellationToken))
                {
                    List(dir);
                    seen.Add(dir);
                }

                progress?.Report(100.0 * ++done / Math.Max(1, roots.Count));
            }

            // Forget folders that are gone, or that belong to libraries no longer enabled.
            foreach (var key in _folders.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _folders.TryRemove(key, out _);
            }

            _logger.LogInformation("Additional Material: indexed {Count} folders in {Roots} library location(s)", seen.Count, roots.Count);
        }
        finally
        {
            Interlocked.Exchange(ref _rebuilding, 0);
        }

        return Task.CompletedTask;
    }

    private static IEnumerable<string> Walk(string root, CancellationToken cancellationToken)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            yield return dir;
            IEnumerable<DirectoryInfo> children;
            try
            {
                children = new DirectoryInfo(dir).EnumerateDirectories().ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (!child.Name.StartsWith('.') && (child.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    stack.Push(child.FullName);
                }
            }
        }
    }

    private IReadOnlyDictionary<string, IndexedZip> List(string directory)
    {
        var zips = new Dictionary<string, IndexedZip>(StringComparer.Ordinal);
        try
        {
            foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true }))
            {
                if (file.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var link = file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0;
                    zips[file.Name.ToLowerInvariant()] = new IndexedZip(file.Name, link ? 0 : file.Length, link);
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Additional Material: cannot list {Directory}", directory);
            _folders.TryRemove(directory, out _);
            return zips;
        }

        _folders[directory] = (zips, DateTimeOffset.UtcNow);
        return zips;
    }
}

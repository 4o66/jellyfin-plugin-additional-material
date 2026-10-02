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
    private readonly ConcurrentDictionary<string, bool> _linkedFolders = new(StringComparer.Ordinal);
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

    /// <summary>Raised after a full rebuild, so caches built on the old index can be dropped.</summary>
    public event Action? Rebuilt;

    /// <summary>Gets the number of folders currently indexed.</summary>
    public int FolderCount => _folders.Count;

    /// <summary>Gets the state of the latest rebuild, for the settings page.</summary>
    public IndexStatus Status { get; } = new();

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

    /// <summary>Reads from the disk whether <paramref name="directory"/> is a symbolic link or junction.</summary>
    /// <param name="directory">An absolute folder path.</param>
    /// <returns><c>true</c> if it is a link, or cannot be read.</returns>
    public static bool ReadIsLinkedFolder(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Whether <paramref name="directory"/> is a link, remembered from when it was last read.</summary>
    /// <param name="directory">An absolute folder path.</param>
    /// <returns><c>true</c> if it is a link.</returns>
    public bool IsLinkedFolder(string directory) => _linkedFolders.GetOrAdd(directory, ReadIsLinkedFolder);

    /// <summary>Lists one folder again now, after the plugin wrote or removed an archive in it.</summary>
    /// <param name="directory">An absolute folder path.</param>
    public void Relist(string directory) => List(directory);

    /// <summary>Lists a folder and every folder below it again now (a course that was just added).</summary>
    /// <param name="directory">An absolute folder path.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public void RelistTree(string directory, CancellationToken cancellationToken)
    {
        foreach (var dir in Walk(directory, cancellationToken))
        {
            List(dir);
        }
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

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Status.Running = true;
        Status.Progress = 0;
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

                Status.Progress = 100.0 * ++done / Math.Max(1, roots.Count);
                progress?.Report(Status.Progress);
            }

            // Forget folders that are gone, or that belong to libraries no longer enabled.
            foreach (var key in _folders.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _folders.TryRemove(key, out _);
            }

            _linkedFolders.Clear();
            Status.Folders = seen.Count;
            Status.Zips = _folders.Values.Sum(f => f.Zips.Count);
            Status.Milliseconds = watch.ElapsedMilliseconds;
            Status.FinishedUtc = DateTimeOffset.UtcNow;
            Rebuilt?.Invoke();

            _logger.LogInformation("Additional Material: indexed {Count} folders in {Roots} library location(s)", seen.Count, roots.Count);
        }
        finally
        {
            Status.Running = false;
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

        _linkedFolders[directory] = ReadIsLinkedFolder(directory);
        _folders[directory] = (zips, DateTimeOffset.UtcNow);
        return zips;
    }
}

/// <summary>The latest index rebuild, as the settings page shows it.</summary>
public sealed class IndexStatus
{
    /// <summary>Gets or sets a value indicating whether a rebuild is running.</summary>
    public bool Running { get; set; }

    /// <summary>Gets or sets the running rebuild's progress, 0–100.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets how many folders the last rebuild indexed.</summary>
    public int Folders { get; set; }

    /// <summary>Gets or sets how many zips the last rebuild found.</summary>
    public int Zips { get; set; }

    /// <summary>Gets or sets how long the last rebuild took.</summary>
    public long Milliseconds { get; set; }

    /// <summary>Gets or sets when the last rebuild finished, or <c>null</c> if none has yet.</summary>
    public DateTimeOffset? FinishedUtc { get; set; }
}

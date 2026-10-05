using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdditionalMaterial.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdditionalMaterial.Services;

/// <summary>
/// Plans (and, with background building on, builds) a course again as soon as something in it
/// changes, rather than at the next full library scan: Jellyfin adding items (a new course or
/// lesson), files added, changed, removed or renamed in the course folders (material, which
/// Jellyfin makes no items for), or a new VirusTotal answer about one of its files. Changes are
/// collected until none has come for <see cref="Quiet"/>, so a course still being copied is planned
/// once, when the copy is done, and at most <see cref="MaxWait"/> after the first.
/// </summary>
/// <remarks>
/// Folders are watched in the enabled libraries that have Jellyfin's "Enable real-time monitoring"
/// on, the same choice Jellyfin's own watching follows. Network shares report no changes; there,
/// library scans still do it.
/// </remarks>
public sealed class NewCourseWatcher : IHostedService, IDisposable
{
    /// <summary>How long after the last change a course is planned.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);

    /// <summary>The longest a change waits while more keep coming.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    /// <summary>How often the watched folders are brought in line with the settings.</summary>
    public static readonly TimeSpan SyncEvery = TimeSpan.FromMinutes(1);

    private static readonly HashSet<string> _jellyfinDirs = new(StringComparer.OrdinalIgnoreCase) { "extrafanart", "extrathumbs", "metadata" };

    private readonly ILibraryManager _libraryManager;
    private readonly MaterialIndex _index;
    private readonly ArchivePlans _plans;
    private readonly ArchiveBuilder _builder;
    private readonly BuiltRegistry _registry;
    private readonly MaterialRefresh _refresh;
    private readonly VirusTotal _virusTotal;
    private readonly ILogger<NewCourseWatcher> _logger;
    private readonly object _lock = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private DateTimeOffset _firstPending;
    private DateTimeOffset _lastOverflow = DateTimeOffset.MinValue;
    private int _running;
    private Timer? _timer;
    private Timer? _sync;

    /// <summary>Initializes a new instance of the <see cref="NewCourseWatcher"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="index">The folder index.</param>
    /// <param name="plans">The plans.</param>
    /// <param name="builder">The builder.</param>
    /// <param name="registry">The plugin's built archives, whose writes are not changes to react to.</param>
    /// <param name="refresh">The full re-read, for when changes were missed.</param>
    /// <param name="virusTotal">VirusTotal lookups, whose answers change what a course's archives hold.</param>
    /// <param name="logger">Logger.</param>
    public NewCourseWatcher(ILibraryManager libraryManager, MaterialIndex index, ArchivePlans plans, ArchiveBuilder builder, BuiltRegistry registry, MaterialRefresh refresh, VirusTotal virusTotal, ILogger<NewCourseWatcher> logger)
    {
        _libraryManager = libraryManager;
        _index = index;
        _plans = plans;
        _builder = builder;
        _registry = registry;
        _refresh = refresh;
        _virusTotal = virusTotal;
        _logger = logger;
    }

    /// <summary>Gets the folders being watched.</summary>
    public IReadOnlyList<string> Watched
    {
        get
        {
            lock (_watchers)
            {
                return _watchers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
        _sync = new Timer(_ => SyncWatchers(), null, TimeSpan.FromSeconds(15), SyncEvery);
        _libraryManager.ItemAdded += OnItemAdded;
        _virusTotal.Checked += Add;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _virusTotal.Checked -= Add;
        _stopping.Cancel();
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _sync?.Change(Timeout.Infinite, Timeout.Infinite);
        lock (_watchers)
        {
            foreach (var w in _watchers.Values)
            {
                w.Dispose();
            }

            _watchers.Clear();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _sync?.Dispose();
        lock (_watchers)
        {
            foreach (var w in _watchers.Values)
            {
                w.Dispose();
            }
        }

        _stopping.Dispose();
    }

    /// <summary>Whether a changed path can matter to a course's material: inside a course, not hidden (the plugin's own temporary files are), and not Jellyfin's metadata or artwork.</summary>
    /// <param name="root">The watched folder.</param>
    /// <param name="path">The changed path.</param>
    /// <returns>Whether to react to it.</returns>
    internal static bool Relevant(string root, string path)
    {
        var parts = Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar);
        if (parts.Length < 2 || parts[0] == "..")
        {
            return false;   // the library folder itself, or a file directly in it: no course
        }

        if (parts.Any(p => p.StartsWith('.')) || parts[..^1].Any(p => _jellyfinDirs.Contains(p) || p.EndsWith(".trickplay", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !parts[^1].EndsWith(".nfo", StringComparison.OrdinalIgnoreCase);
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        var path = e.Item?.Path;
        if (!string.IsNullOrEmpty(path) && !e.Item!.IsVirtualItem)
        {
            Add(path);
        }
    }

    /// <summary>Plans the course a path lies in again, once nothing more has come for <see cref="Quiet"/>.</summary>
    private void Add(string path)
    {
        // With building off only someone else's archive matters: the index learns of it.
        if (Plugin.Instance?.Configuration.BuildArchives != true && !Planner.IsTrigger(Path.GetFileName(path)))
        {
            return;
        }

        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_pending.Count == 0)
            {
                _firstPending = now;
            }

            _pending.Add(path);
            var left = _firstPending + MaxWait - now;
            var due = left < TimeSpan.Zero ? TimeSpan.Zero : left < Quiet ? left : Quiet;
            _timer?.Change(due, Timeout.InfiniteTimeSpan);   // restart the quiet period, up to MaxWait
        }
    }

    private void Run()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            lock (_lock)
            {
                _timer?.Change(Quiet, Timeout.InfiniteTimeSpan);   // the run in progress finishes first
            }

            return;
        }

        List<string> paths;
        lock (_lock)
        {
            paths = _pending.ToList();
            _pending.Clear();
        }

        var done = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var building = Plugin.Instance?.Configuration.BuildArchives == true;
            foreach (var path in paths)
            {
                if (Planner.IsTrigger(Path.GetFileName(path)))
                {
                    if (_registry.Owns(path))
                    {
                        continue;   // one the plugin built: it already knows
                    }

                    _index.Relist(Path.GetDirectoryName(path)!);
                }

                if (!building || done.Any(c => path.StartsWith(c + Path.DirectorySeparatorChar, StringComparison.Ordinal) || path == c))
                {
                    continue;
                }

                var course = _plans.ReplanCourseOf(path);
                if (course is null || !done.Add(course))
                {
                    continue;
                }

                _index.RelistTree(course, _stopping.Token);
                if (Plugin.Instance?.Configuration.BuildInBackground == true)
                {
                    _builder.BuildCourse(course, _stopping.Token);
                }
                else
                {
                    _builder.Count();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Additional Material: could not plan changed material");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Watches the enabled libraries' folders that have real-time monitoring on, and only those.</summary>
    private void SyncWatchers()
    {
        HashSet<string> wanted;
        try
        {
            var enabled = (Plugin.Instance?.Configuration.EnabledLibraryIds ?? Array.Empty<string>())
                .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty)
                .ToHashSet();
            wanted = _libraryManager.GetUserRootFolder().Children
                .OfType<CollectionFolder>()
                .Where(f => enabled.Contains(f.Id) && _libraryManager.GetLibraryOptions(f)?.EnableRealtimeMonitor == true)
                .SelectMany(f => f.PhysicalLocations.Where(p => !string.Equals(p, f.Path, StringComparison.Ordinal)))   // not the library's own definition folder
                .Where(Directory.Exists)
                .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "Additional Material: could not list the folders to watch");
            return;
        }

        var changed = false;
        lock (_watchers)
        {
            foreach (var root in _watchers.Keys.Where(r => !wanted.Contains(r)).ToList())
            {
                _watchers[root].Dispose();
                _watchers.Remove(root);
                changed = true;
            }

            foreach (var root in wanted.Where(r => !_watchers.ContainsKey(r)))
            {
                try
                {
                    _watchers[root] = Watch(root);
                    changed = true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is PlatformNotSupportedException)
                {
                    _logger.LogWarning(ex, "Additional Material: cannot watch {Folder}; changes there are noticed at library scans", root);
                }
            }
        }

        if (changed)
        {
            var watched = Watched;
            _logger.LogInformation("Additional Material: watching {Count} folder(s) for changed material: {Folders}", watched.Count, string.Join(", ", watched));
        }
    }

    private FileSystemWatcher Watch(string root)
    {
        var w = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };
        void Changed(object sender, FileSystemEventArgs e)
        {
            if (Relevant(root, e.FullPath))
            {
                Add(e.FullPath);
            }
        }

        w.Created += Changed;
        w.Changed += Changed;
        w.Deleted += Changed;
        w.Renamed += (_, e) =>
        {
            foreach (var p in new[] { e.OldFullPath, e.FullPath }.Where(p => Relevant(root, p)))
            {
                Add(p);
            }
        };
        w.Error += (_, e) => OnWatchError(root, e.GetException());
        w.EnableRaisingEvents = true;
        return w;
    }

    /// <summary>Changes were missed (too many at once): re-read everything once, and watch the folder afresh at the next sync.</summary>
    private void OnWatchError(string root, Exception ex)
    {
        lock (_watchers)
        {
            if (_watchers.Remove(root, out var w))
            {
                w.Dispose();
            }
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastOverflow < MaxWait || Plugin.Instance?.Configuration.BuildArchives != true)
        {
            _logger.LogWarning(ex, "Additional Material: changes in {Folder} may have been missed", root);
            return;
        }

        _lastOverflow = now;
        _logger.LogWarning(ex, "Additional Material: changes in {Folder} may have been missed; re-reading every library", root);
        _ = Task.Run(() => _refresh.RunAsync(null, _stopping.Token));
    }
}

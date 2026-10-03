using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdditionalMaterial.Services;

/// <summary>
/// Plans (and, with background building on, builds) a course as soon as Jellyfin adds items to it,
/// rather than at the next full library scan. Additions are collected until none has come for
/// <see cref="Quiet"/>, so a course still being copied is planned once, when the copy is done.
/// A new VirusTotal answer about one of a course's files does the same: its note changes.
/// </summary>
public sealed class NewCourseWatcher : IHostedService, IDisposable
{
    /// <summary>How long after the last addition a course is planned.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly MaterialIndex _index;
    private readonly ArchivePlans _plans;
    private readonly ArchiveBuilder _builder;
    private readonly VirusTotal _virusTotal;
    private readonly ILogger<NewCourseWatcher> _logger;
    private readonly object _lock = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private Timer? _timer;

    /// <summary>Initializes a new instance of the <see cref="NewCourseWatcher"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="index">The folder index.</param>
    /// <param name="plans">The plans.</param>
    /// <param name="builder">The builder.</param>
    /// <param name="virusTotal">VirusTotal lookups, whose answers change what a course's archives hold.</param>
    /// <param name="logger">Logger.</param>
    public NewCourseWatcher(ILibraryManager libraryManager, MaterialIndex index, ArchivePlans plans, ArchiveBuilder builder, VirusTotal virusTotal, ILogger<NewCourseWatcher> logger)
    {
        _libraryManager = libraryManager;
        _index = index;
        _plans = plans;
        _builder = builder;
        _virusTotal = virusTotal;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _timer = new Timer(_ => Run(), null, Timeout.Infinite, Timeout.Infinite);
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
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _stopping.Dispose();
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
        if (Plugin.Instance?.Configuration.BuildArchives != true)
        {
            return;
        }

        lock (_lock)
        {
            _pending.Add(path);
            _timer?.Change(Quiet, Timeout.InfiniteTimeSpan);   // restart the quiet period
        }
    }

    private void Run()
    {
        List<string> paths;
        lock (_lock)
        {
            paths = _pending.ToList();
            _pending.Clear();
        }

        var done = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var path in paths)
            {
                if (done.Any(c => path.StartsWith(c + Path.DirectorySeparatorChar, StringComparison.Ordinal) || path == c))
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
            _logger.LogWarning(ex, "Additional Material: could not plan newly added material");
        }
    }
}

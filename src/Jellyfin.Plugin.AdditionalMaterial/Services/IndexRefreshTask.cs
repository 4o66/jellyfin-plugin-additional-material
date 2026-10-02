using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AdditionalMaterial.Services;

/// <summary>Builds the folder index at startup. Also listed in Scheduled Tasks to run by hand.</summary>
public sealed class IndexRefreshTask : IScheduledTask
{
    private readonly MaterialRefresh _refresh;

    /// <summary>Initializes a new instance of the <see cref="IndexRefreshTask"/> class.</summary>
    /// <param name="refresh">Re-reads folders, plans and builds archives.</param>
    public IndexRefreshTask(MaterialRefresh refresh)
    {
        _refresh = refresh;
    }

    /// <inheritdoc />
    public string Name => "Refresh additional material";

    /// <inheritdoc />
    public string Key => "Jellyfin.Plugin.AdditionalMaterial.IndexRefresh";

    /// <inheritdoc />
    public string Description => "Re-reads the enabled libraries' folders for additional material archives, and builds archives when building is on. Also runs after every library scan.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _refresh.RunAsync(progress, cancellationToken);

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger }];
    }
}

/// <summary>Rebuilds the folder index after each library scan, while the disks are awake anyway.</summary>
public sealed class IndexPostScanTask : ILibraryPostScanTask
{
    private readonly MaterialRefresh _refresh;

    /// <summary>Initializes a new instance of the <see cref="IndexPostScanTask"/> class.</summary>
    /// <param name="refresh">Re-reads folders, plans and builds archives.</param>
    public IndexPostScanTask(MaterialRefresh refresh)
    {
        _refresh = refresh;
    }

    /// <inheritdoc />
    public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        => _refresh.RunAsync(progress, cancellationToken);
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdditionalMaterial.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdditionalMaterial.Services;

/// <summary>An archive the plugin built.</summary>
public sealed class BuiltArchive
{
    /// <summary>Gets or sets where it was written (beside the videos, or in the plugin's cache).</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>Gets or sets the fingerprint of the files it was built from.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Gets or sets its size when written, so only an untouched file is ever deleted.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets when it was built.</summary>
    public DateTimeOffset BuiltUtc { get; set; }

    /// <summary>
    /// Gets or sets earlier copies that could not be removed yet (on Windows, while being
    /// downloaded), with their sizes. They stay the plugin's, so a course is never mistaken for
    /// having someone else's archives, and are removed on a later run.
    /// </summary>
    public Dictionary<string, long> Leftovers { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The archives the plugin built, by the path the plugin and the script look for them at. Kept in
/// the plugin's data folder; it is how the plugin tells its own archives from ones made by hand or
/// by the script, which it never changes.
/// </summary>
public sealed class BuiltRegistry
{
    private readonly object _lock = new();
    private Dictionary<string, BuiltArchive>? _entries;

    /// <summary>Gets the registry file.</summary>
    public static string FilePath => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "built-archives.json");

    /// <summary>Gets the folder for archives built into the cache.</summary>
    public static string CacheFolder => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "archives");

    /// <summary>The entry for an archive path, or <c>null</c>.</summary>
    /// <param name="archivePath">The archive's path.</param>
    /// <returns>The entry.</returns>
    public BuiltArchive? Get(string archivePath)
    {
        lock (_lock)
        {
            return Load().GetValueOrDefault(archivePath);
        }
    }

    /// <summary>Whether the plugin built the archive at this path.</summary>
    /// <param name="archivePath">The archive's path.</param>
    /// <returns>Whether it is the plugin's.</returns>
    public bool Owns(string archivePath) => Get(archivePath) is { } e
        && (string.Equals(e.Location, archivePath, StringComparison.Ordinal) || e.Leftovers.ContainsKey(archivePath));

    /// <summary>Records an archive.</summary>
    /// <param name="archivePath">The archive's path.</param>
    /// <param name="entry">What was built.</param>
    public void Set(string archivePath, BuiltArchive entry)
    {
        lock (_lock)
        {
            Load()[archivePath] = entry;
            Save();
        }
    }

    /// <summary>Forgets an archive.</summary>
    /// <param name="archivePath">The archive's path.</param>
    public void Remove(string archivePath)
    {
        lock (_lock)
        {
            if (Load().Remove(archivePath))
            {
                Save();
            }
        }
    }

    /// <summary>All entries.</summary>
    /// <returns>A copy.</returns>
    public Dictionary<string, BuiltArchive> All()
    {
        lock (_lock)
        {
            return new Dictionary<string, BuiltArchive>(Load(), StringComparer.Ordinal);
        }
    }

    private Dictionary<string, BuiltArchive> Load()
    {
        if (_entries is null)
        {
            try
            {
                _entries = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, BuiltArchive>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                _entries = new();
            }

            _entries = new Dictionary<string, BuiltArchive>(_entries, StringComparer.Ordinal);
        }

        return _entries;
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

/// <summary>A planned archive and the course it belongs to.</summary>
/// <param name="Archive">The plan.</param>
/// <param name="Course">The course folder.</param>
/// <param name="Fingerprint">What it would be built from: names, sizes, times and removals.</param>
public sealed record PlanEntry(PlannedArchive Archive, string Course, string Fingerprint)
{
    /// <summary>Gets the sum of the files' sizes, an estimate of the archive's size.</summary>
    public long EstimatedSize => Archive.Files.Sum(f => SafeLength(f));

    /// <summary>
    /// The one file to hand out as is, with no archive around it, or <c>null</c>: material that is
    /// a single file, unless it is large and compresses well (settings), or was replaced by a note.
    /// </summary>
    /// <returns>The file, or <c>null</c>.</returns>
    public string? SingleFile()
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.ServeSingleFiles || Archive.Files.Count != 1 || Archive.Removed.Count > 0)
        {
            return null;
        }

        var file = Archive.Files[0];
        var big = SafeLength(file) >= Math.Max(1, config.SingleFileZipFromMegabytes) * 1024L * 1024;
        return big && ArchiveWriter.WorthCompressing(file) ? null : file;
    }

    /// <summary>The source zip when the material is exactly one zip that passed screening: it is handed out as it is.</summary>
    /// <returns>The zip, or <c>null</c>.</returns>
    public string? LoneZip()
        => Archive.Files.Count == 1 && Archive.Removed.Count == 0 && Py.Lower(Py.Suffix(Path.GetFileName(Archive.Files[0]))) == ".zip" ? Archive.Files[0] : null;

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return 0;
        }
    }
}

/// <summary>
/// What the plugin would build, for every course in the enabled libraries, using the helper
/// script's rules. Courses that already hold archives the plugin did not build are left alone.
/// </summary>
public sealed class ArchivePlans
{
    private readonly ILibraryManager _libraryManager;
    private readonly BuiltRegistry _registry;
    private readonly ILogger<ArchivePlans> _logger;
    private readonly RuleStore _rules;
    private readonly VirusTotal _virusTotal;
    private readonly object _swap = new();
    private volatile Dictionary<string, PlanEntry> _plans = new(StringComparer.Ordinal);
    private int _planning;

    /// <summary>Initializes a new instance of the <see cref="ArchivePlans"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="registry">The plugin's built archives.</param>
    /// <param name="rules">The rules in force.</param>
    /// <param name="virusTotal">VirusTotal lookups.</param>
    /// <param name="logger">Logger.</param>
    public ArchivePlans(ILibraryManager libraryManager, BuiltRegistry registry, RuleStore rules, VirusTotal virusTotal, ILogger<ArchivePlans> logger)
    {
        _libraryManager = libraryManager;
        _registry = registry;
        _rules = rules;
        _virusTotal = virusTotal;
        _logger = logger;
    }

    /// <summary>Gets every planned archive.</summary>
    public IReadOnlyDictionary<string, PlanEntry> All => _plans;

    /// <summary>Gets how many courses the last planning looked at, and how many it left alone.</summary>
    public (int Courses, int LeftAlone) LastRun { get; private set; }

    /// <summary>The plan for an archive path, from the last planning.</summary>
    /// <param name="archivePath">The archive's path.</param>
    /// <returns>The plan, or <c>null</c>.</returns>
    public PlanEntry? Get(string archivePath)
        => Plugin.Instance?.Configuration.BuildArchives == true && _plans.TryGetValue(archivePath, out var p) ? p : null;

    /// <summary>Plans every course in the enabled libraries again. Clears the plans when building is off.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public void Rebuild(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _planning, 1) == 1)
        {
            return;
        }

        try
        {
            var plans = new Dictionary<string, PlanEntry>(StringComparer.Ordinal);
            int courses = 0, leftAlone = 0;
            if (Plugin.Instance?.Configuration.BuildArchives == true)
            {
                foreach (var root in Roots())
                {
                    foreach (var course in Planner.CoursesIn(root))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        courses++;
                        if (HasOthersArchives(course))
                        {
                            leftAlone++;
                            continue;
                        }

                        foreach (var entry in PlanCourse(course))
                        {
                            plans[entry.Archive.Archive] = entry;
                        }
                    }
                }
            }

            lock (_swap)
            {
                _plans = plans;
            }

            LastRun = (courses, leftAlone);
            _logger.LogInformation("Additional Material: planned {Archives} archives in {Courses} courses ({LeftAlone} have their own archives)", plans.Count, courses, leftAlone);
        }
        finally
        {
            Interlocked.Exchange(ref _planning, 0);
        }
    }

    /// <summary>
    /// Plans one course again (one that was just added or changed), replacing its earlier plans.
    /// Returns the course folder if the path lies in a course of an enabled library.
    /// </summary>
    /// <param name="path">A path inside the course (a video, a season folder, the course itself).</param>
    /// <returns>The course folder, or <c>null</c> if the path is in no enabled library or building is off.</returns>
    public string? ReplanCourseOf(string path)
    {
        if (Plugin.Instance?.Configuration.BuildArchives != true)
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        string? course = null;
        foreach (var root in Roots())
        {
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || full == root)
            {
                course = Planner.CoursesIn(root).FirstOrDefault(c => full == c || full.StartsWith(c + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                break;
            }
        }

        if (course is null)
        {
            return null;
        }

        // Plan from the disk first (slow), then swap it into whatever the plans are by then.
        var fresh = HasOthersArchives(course) ? [] : PlanCourse(course).ToList();
        lock (_swap)
        {
            var plans = new Dictionary<string, PlanEntry>(_plans.Where(p => p.Value.Course != course), StringComparer.Ordinal);
            foreach (var entry in fresh)
            {
                plans[entry.Archive.Archive] = entry;
            }

            _plans = plans;
        }

        _logger.LogInformation("Additional Material: planned {Course} again ({Archives} archives)", course, fresh.Count);
        return course;
    }

    /// <summary>Plans one archive's course again from the disk, for building it with what is there now.</summary>
    /// <param name="archivePath">The archive's path.</param>
    /// <returns>The fresh plan, or <c>null</c> if the archive would no longer exist.</returns>
    public PlanEntry? Replan(string archivePath)
    {
        var known = Get(archivePath);
        if (known is null || HasOthersArchives(known.Course))
        {
            return null;
        }

        var fresh = PlanCourse(known.Course).FirstOrDefault(p => p.Archive.Archive == archivePath);
        lock (_swap)
        {
            var plans = new Dictionary<string, PlanEntry>(_plans, StringComparer.Ordinal);
            if (fresh is null)
            {
                plans.Remove(archivePath);
            }
            else
            {
                plans[archivePath] = fresh;
            }

            _plans = plans;
        }

        return fresh;
    }

    private IEnumerable<PlanEntry> PlanCourse(string course)
    {
        var options = new PlanOptions
        {
            Scan = _virusTotal.Lookup,
            AllowCleanExecutables = Plugin.Instance?.Configuration.AllowCleanExecutables == true,
        };
        var planner = new Planner(_rules.Current(), options);
        List<PlannedArchive> archives;
        try
        {
            archives = planner.PlanCourse(course);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Additional Material: cannot plan {Course}", course);
            yield break;
        }

        foreach (var archive in archives)
        {
            yield return new PlanEntry(archive, course, Fingerprint(archive));
        }
    }

    /// <summary>Whether a course holds archives someone else made: then the plugin builds nothing there.</summary>
    private bool HasOthersArchives(string course)
    {
        try
        {
            return Directory.EnumerateFiles(course, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.ReparsePoint })
                .Any(f => Planner.IsTrigger(Path.GetFileName(f)) && !Path.GetRelativePath(course, f).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.')) && !_registry.Owns(f));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string Fingerprint(PlannedArchive archive)
    {
        var text = new StringBuilder("v1\n");
        foreach (var f in archive.Files)
        {
            var info = new FileInfo(f);
            text.Append(archive.EntryName(f)).Append('|')
                .Append(info.Exists ? info.Length : -1).Append('|')
                .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('|')
                .Append(archive.Removed.GetValueOrDefault(f, string.Empty)).Append('|')
                .Append(archive.Removed.ContainsKey(f) ? ScanResult.Describe(archive.Scans.GetValueOrDefault(f)) : string.Empty).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    private List<string> Roots()
    {
        var enabled = (Plugin.Instance?.Configuration.EnabledLibraryIds ?? Array.Empty<string>())
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToHashSet();
        return _libraryManager.GetUserRootFolder().Children
            .OfType<CollectionFolder>()
            .Where(f => enabled.Contains(f.Id))
            .SelectMany(f => f.PhysicalLocations)
            .Where(Directory.Exists)
            .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>The latest background build, as the settings page shows it.</summary>
public sealed class BuildStatus
{
    /// <summary>Gets or sets a value indicating whether a build is running.</summary>
    public bool Running { get; set; }

    /// <summary>Gets or sets the running build's progress, 0–100.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets how many archives are planned.</summary>
    public int Planned { get; set; }

    /// <summary>Gets or sets how many planned archives are built and up to date.</summary>
    public int UpToDate { get; set; }

    /// <summary>Gets or sets how many archives the last run built.</summary>
    public int BuiltLastRun { get; set; }

    /// <summary>Gets or sets how many archives failed to build in the last run.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets how many courses the plugin leaves alone because they have their own archives.</summary>
    public int CoursesLeftAlone { get; set; }

    /// <summary>Gets or sets when the last run finished.</summary>
    public DateTimeOffset? FinishedUtc { get; set; }
}

/// <summary>Builds planned archives, beside the videos or in the plugin's cache, and tidies up the ones it no longer needs.</summary>
public sealed class ArchiveBuilder
{
    private readonly ArchivePlans _plans;
    private readonly BuiltRegistry _registry;
    private readonly MaterialIndex _index;
    private readonly ILogger<ArchiveBuilder> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private int _running;

    /// <summary>Initializes a new instance of the <see cref="ArchiveBuilder"/> class.</summary>
    /// <param name="plans">The plans.</param>
    /// <param name="registry">The plugin's built archives.</param>
    /// <param name="index">The folder index, told about archives written beside the videos.</param>
    /// <param name="logger">Logger.</param>
    public ArchiveBuilder(ArchivePlans plans, BuiltRegistry registry, MaterialIndex index, ILogger<ArchiveBuilder> logger)
    {
        _plans = plans;
        _registry = registry;
        _index = index;
        _logger = logger;
    }

    /// <summary>Gets the state of the latest build run.</summary>
    public BuildStatus Status { get; } = new();

    /// <summary>
    /// The built archive for a planned path, building or rebuilding it first if needed, from what is
    /// on the disk now. A lone zip is handed out as it is.
    /// </summary>
    /// <param name="archivePath">The planned archive's path.</param>
    /// <param name="force">Build it again even if it is up to date.</param>
    /// <returns>The file to serve, or <c>null</c> if there is nothing to build any more.</returns>
    public string? EnsureBuilt(string archivePath, bool force = false)
    {
        var gate = _locks.GetOrAdd(archivePath, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            var plan = _plans.Replan(archivePath);
            if (plan is null)
            {
                return null;
            }

            if (plan.LoneZip() is { } lone)
            {
                return lone;
            }

            if (!force && Current(archivePath, plan) is { } current)
            {
                return current;
            }

            var target = Target(archivePath);
            var size = ArchiveWriter.Write(plan.Archive, target);

            // Moved (beside the videos <-> cache): remove the old copy if it is untouched, so it is
            // neither left behind nor mistaken later for an archive someone else made. One that
            // cannot be removed yet (Windows, while it downloads) is kept as the plugin's leftover.
            var leftovers = new Dictionary<string, long>(StringComparer.Ordinal);
            if (_registry.Get(archivePath) is { } previous)
            {
                foreach (var (path, length) in previous.Leftovers)
                {
                    leftovers[path] = length;
                }

                if (!string.Equals(previous.Location, target, StringComparison.Ordinal))
                {
                    leftovers[previous.Location] = previous.Size;
                }
            }

            leftovers.Remove(target);
            RemoveLeftovers(leftovers);
            _registry.Set(archivePath, new BuiltArchive { Location = target, Fingerprint = plan.Fingerprint, Size = size, BuiltUtc = DateTimeOffset.UtcNow, Leftovers = leftovers });
            _index.Relist(Path.GetDirectoryName(archivePath)!);
            _logger.LogInformation("Additional Material: built {Archive} ({Files} files)", target, plan.Archive.Files.Count);
            return target;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Whether a planned archive is built and up to date.</summary>
    /// <param name="archivePath">The planned archive's path.</param>
    /// <param name="plan">Its plan.</param>
    /// <returns>The built file, or <c>null</c>.</returns>
    public string? Current(string archivePath, PlanEntry plan)
    {
        var entry = _registry.Get(archivePath);
        if (entry is null || entry.Fingerprint != plan.Fingerprint)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(entry.Location);
            if (!info.Exists || info.Length != entry.Size)
            {
                return null;
            }

            // Built where the setting now says? Otherwise it is rebuilt there (and the old copy removed).
            var inCache = entry.Location.StartsWith(BuiltRegistry.CacheFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            var cacheWanted = Plugin.Instance?.Configuration.BuiltArchiveLocation == "cache";
            if (cacheWanted != inCache && (cacheWanted || CanWrite(Path.GetDirectoryName(archivePath)!)))
            {
                return null;
            }

            return entry.Location;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds every planned archive that is missing or outdated (all of them with <paramref name="force"/>),
    /// then deletes archives the plugin built that are no longer planned, but only files it wrote
    /// and nobody changed since.
    /// </summary>
    /// <param name="force">Rebuild even up-to-date archives.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public void BuildAll(bool force, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        Status.Running = true;
        Status.Progress = 0;
        int built = 0, failed = 0, done = 0;
        try
        {
            var plans = _plans.All.Values.ToList();
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = plan.Archive.Archive;
                if (plan.LoneZip() is null && plan.SingleFile() is null && (force || Current(path, plan) is null))
                {
                    try
                    {
                        // The registry entry stays while rebuilding: without it the archive beside the
                        // videos would look like someone else's and the course would be left alone.
                        if (EnsureBuilt(path, force) is not null)
                        {
                            built++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                    {
                        failed++;
                        _logger.LogWarning(ex, "Additional Material: could not build {Archive}", path);
                    }
                }

                Status.Progress = 100.0 * ++done / Math.Max(1, plans.Count);
            }

            Tidy();
            Status.Planned = _plans.All.Count;
            Status.UpToDate = _plans.All.Values.Count(p => p.LoneZip() is not null || p.SingleFile() is not null || Current(p.Archive.Archive, p) is not null);
            Status.BuiltLastRun = built;
            Status.Failed = failed;
            Status.CoursesLeftAlone = _plans.LastRun.LeftAlone;
            Status.FinishedUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            Status.Running = false;
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Builds the missing or outdated archives of one course (one that was just added).</summary>
    /// <param name="course">The course folder.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public void BuildCourse(string course, CancellationToken cancellationToken)
    {
        foreach (var plan in _plans.All.Values.Where(p => p.Course == course).ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (plan.LoneZip() is null && plan.SingleFile() is null && Current(plan.Archive.Archive, plan) is null)
            {
                try
                {
                    EnsureBuilt(plan.Archive.Archive);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
                {
                    _logger.LogWarning(ex, "Additional Material: could not build {Archive}", plan.Archive.Archive);
                }
            }
        }

        Count();
    }

    /// <summary>Refreshes the counts without building anything.</summary>
    public void Count()
    {
        Status.Planned = _plans.All.Count;
        Status.UpToDate = _plans.All.Values.Count(p => p.LoneZip() is not null || p.SingleFile() is not null || Current(p.Archive.Archive, p) is not null);
        Status.CoursesLeftAlone = _plans.LastRun.LeftAlone;
    }

    /// <summary>Removes earlier copies that are still as the plugin wrote them; keeps the ones that cannot be removed yet.</summary>
    private void RemoveLeftovers(Dictionary<string, long> leftovers)
    {
        foreach (var (path, length) in leftovers.ToList())
        {
            try
            {
                var old = new FileInfo(path);
                if (old.Exists && old.Length == length)
                {
                    old.Delete();
                    _index.Relist(Path.GetDirectoryName(path)!);
                }

                leftovers.Remove(path);   // gone, or changed by someone: no longer ours to remove
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Additional Material: could not remove the old copy {Archive} yet; will retry", path);
            }
        }
    }

    private void Tidy()
    {
        var planned = _plans.All;
        foreach (var (path, entry) in _registry.All())
        {
            if (entry.Leftovers.Count > 0)
            {
                var before = entry.Leftovers.Count;
                RemoveLeftovers(entry.Leftovers);
                if (entry.Leftovers.Count != before)
                {
                    _registry.Set(path, entry);
                }
            }

            if (planned.ContainsKey(path))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(entry.Location);
                if (info.Exists && info.Length == entry.Size)
                {
                    info.Delete();
                    _index.Relist(Path.GetDirectoryName(path)!);
                    _logger.LogInformation("Additional Material: removed {Archive}, no longer needed", entry.Location);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Additional Material: could not remove {Archive}", entry.Location);
                continue;
            }

            if (entry.Leftovers.Count == 0)
            {
                _registry.Remove(path);   // otherwise kept until the last copy is gone
            }
        }
    }

    private static string Target(string archivePath)
    {
        var beside = Plugin.Instance?.Configuration.BuiltArchiveLocation != "cache";
        if (beside && CanWrite(Path.GetDirectoryName(archivePath)!))
        {
            return archivePath;
        }

        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(archivePath)))[..24].ToLowerInvariant() + ".zip";
        return Path.Combine(BuiltRegistry.CacheFolder, name);
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".am-probe-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Re-reads folders, plans archives and (if enabled) builds them: after startup, library scans and Re-read folders.</summary>
public sealed class MaterialRefresh
{
    private readonly MaterialIndex _index;
    private readonly ArchivePlans _plans;
    private readonly ArchiveBuilder _builder;

    /// <summary>Initializes a new instance of the <see cref="MaterialRefresh"/> class.</summary>
    /// <param name="index">The folder index.</param>
    /// <param name="plans">The plans.</param>
    /// <param name="builder">The builder.</param>
    public MaterialRefresh(MaterialIndex index, ArchivePlans plans, ArchiveBuilder builder)
    {
        _index = index;
        _plans = plans;
        _builder = builder;
    }

    /// <summary>Runs the whole refresh.</summary>
    /// <param name="progress">Progress, 0–100.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public async Task RunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _index.RebuildAsync(new Progress<double>(p => progress?.Report(p * 0.4)), cancellationToken).ConfigureAwait(false);
        _plans.Rebuild(cancellationToken);
        progress?.Report(60);
        if (Plugin.Instance?.Configuration is { BuildArchives: true, BuildInBackground: true })
        {
            _builder.BuildAll(force: false, cancellationToken);
        }
        else
        {
            _builder.Count();
        }

        progress?.Report(100);
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdditionalMaterial.Services;

/// <summary>An archive of additional material found for an item.</summary>
/// <param name="FullPath">Absolute path on the server. Never sent to clients.</param>
/// <param name="FileName">File name offered to the browser.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="Format">Always <c>zip</c> in this version.</param>
public sealed record MaterialFile(string FullPath, string FileName, long Size, string Format)
{
    /// <summary>Gets the plan when the plugin builds this archive (it may not exist yet); <c>null</c> for an archive made by hand or by the script.</summary>
    public PlanEntry? Plan { get; init; }
}

/// <summary>
/// Finds the material archive for an item by file name. The client only ever sends an item ID;
/// every path here is derived from the item itself and checked to stay inside its library.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A folder item (course/series or section/season) uses <c>additional-material.zip</c> in its folder.</item>
/// <item>A video item (lesson/episode or movie) uses <c>&lt;video name&gt;.material.zip</c> beside the video.</item>
/// <item>Only .zip is recognized in this version; .7z support may follow.</item>
/// </list>
/// </remarks>
public sealed class MaterialLocator
{
    /// <summary>Base name of a folder-level archive.</summary>
    public const string FolderArchiveName = "additional-material";

    /// <summary>Suffix added to a video's base name for a lesson-level archive.</summary>
    public const string VideoArchiveSuffix = ".material";

    private static readonly string[] _extensions = [".zip"];

    private readonly ILibraryManager _libraryManager;
    private readonly MaterialIndex _index;
    private readonly ArchivePlans _plans;
    private readonly BuiltRegistry _registry;
    private readonly ILogger<MaterialLocator> _logger;

    /// <summary>Initializes a new instance of the <see cref="MaterialLocator"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="index">The folder index.</param>
    /// <param name="plans">Archives the plugin builds.</param>
    /// <param name="registry">Archives the plugin has built.</param>
    /// <param name="logger">Logger.</param>
    public MaterialLocator(ILibraryManager libraryManager, MaterialIndex index, ArchivePlans plans, BuiltRegistry registry, ILogger<MaterialLocator> logger)
    {
        _libraryManager = libraryManager;
        _index = index;
        _plans = plans;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Content type for an archive format.</summary>
    /// <param name="format">The archive format.</param>
    /// <returns>The MIME type.</returns>
    public static string ContentTypeFor(string format) => "application/zip";

    /// <summary>
    /// Finds the archive for <paramref name="item"/> from the folder index, without touching the
    /// disk. Good for showing icons; may be up to the index's refresh age out of date.
    /// </summary>
    /// <param name="item">An item the caller is already allowed to see.</param>
    /// <returns>The archive, or <c>null</c> if there is none or the item's library is not enabled.</returns>
    public MaterialFile? Find(BaseItem item) => Find(item, verify: false);

    /// <summary>Finds the archive for <paramref name="item"/>, checking the file itself on disk. Use before serving it.</summary>
    /// <param name="item">An item the caller is already allowed to see.</param>
    /// <returns>The archive, or <c>null</c>.</returns>
    public MaterialFile? FindVerified(BaseItem item) => Find(item, verify: true);

    private MaterialFile? Find(BaseItem item, bool verify)
    {
        if (item is CollectionFolder || item is AggregateFolder || string.IsNullOrEmpty(item.Path))
        {
            return null;
        }

        var libraries = _libraryManager.GetCollectionFolders(item);
        if (!IsEnabled(libraries))
        {
            return null;
        }

        string? directory;
        string baseName;
        if (item is Video && !item.IsFolder)
        {
            directory = Path.GetDirectoryName(item.Path);
            baseName = Path.GetFileNameWithoutExtension(item.Path) + VideoArchiveSuffix;
        }
        else if (item is Folder)
        {
            directory = item.Path;
            baseName = FolderArchiveName;
        }
        else
        {
            return null;
        }

        if (directory is null)
        {
            return null;
        }

        var zips = _index.Zips(directory);
        IndexedZip? zip = null;
        foreach (var ext in _extensions)
        {
            if (zips.TryGetValue((baseName + ext).ToLowerInvariant(), out zip))
            {
                break;
            }
        }

        // An archive the plugin built is handled through its plan (rebuilt when its files change),
        // unless building has been turned off: then it is served as it is, like any other archive.
        var ours = zip is not null && Plugin.Instance?.Configuration.BuildArchives == true && _registry.Owns(Path.GetFullPath(Path.Combine(directory, zip.Name)));
        if (zip is null || ours)
        {
            // No archive made by hand or by the script: one the plugin builds (or built), if planned.
            var planned = Path.GetFullPath(Path.Combine(directory, baseName + _extensions[0]));
            var plan = _plans.Get(planned);
            if (plan is null || !InsideLibrary(planned, libraries, verify ? MaterialIndex.ReadIsLinkedFolder : _index.IsLinkedFolder))
            {
                return null;
            }

            // Material that is one file is handed out as that file: say so, with its own name and size.
            if (plan.SingleFile() is { } single)
            {
                var ext = Path.GetExtension(single).TrimStart('.').ToLowerInvariant();
                return new MaterialFile(planned, Path.GetFileName(single), new FileInfo(single).Length, ext.Length > 0 ? ext : "file") { Plan = plan };
            }

            return new MaterialFile(planned, Path.GetFileName(planned), plan.EstimatedSize, "zip") { Plan = plan };
        }

        var candidate = Path.Combine(directory, zip.Name);
        if (verify)
        {
            return Validate(candidate, libraries);
        }

        if (zip.IsLink || !InsideLibrary(Path.GetFullPath(candidate), libraries, _index.IsLinkedFolder))
        {
            return null;
        }

        return new MaterialFile(Path.GetFullPath(candidate), zip.Name, zip.Size, Path.GetExtension(zip.Name).TrimStart('.').ToLowerInvariant());
    }

    private static bool IsEnabled(IEnumerable<Folder> libraries)
    {
        var configured = Plugin.Instance?.Configuration.EnabledLibraryIds;
        if (configured is null || configured.Length == 0)
        {
            return false;
        }

        var enabled = new HashSet<Guid>();
        foreach (var id in configured)
        {
            if (Guid.TryParse(id, out var guid))
            {
                enabled.Add(guid);
            }
        }

        return libraries.Any(l => enabled.Contains(l.Id));
    }

    private MaterialFile? Validate(string candidate, IEnumerable<Folder> libraries)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(candidate);
            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                _logger.LogWarning("Additional Material: refusing {File}: missing, or a link", candidate);
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Additional Material: cannot read {File}", candidate);
            return null;
        }

        var full = Path.GetFullPath(info.FullName);
        if (!InsideLibrary(full, libraries, MaterialIndex.ReadIsLinkedFolder))
        {
            _logger.LogWarning("Additional Material: refusing {File}: outside the library's folders, or under a linked folder", full);
            return null;
        }

        var format = info.Extension.TrimStart('.').ToLowerInvariant();
        return new MaterialFile(full, info.Name, info.Length, format);
    }

    /// <summary>
    /// Whether <paramref name="full"/> lies inside one of the libraries' folders with no symbolic link
    /// or junction on the way down. Path.GetFullPath does not resolve links, so a linked folder inside
    /// the library would otherwise let a path that looks inside point anywhere. The library root
    /// itself may be a link (that is the administrator's choice); only folders below it are checked.
    /// </summary>
    private static bool InsideLibrary(string full, IEnumerable<Folder> libraries, Func<string, bool> isLinkedFolder)
    {
        var roots = libraries
            .SelectMany(l => l.PhysicalLocations)
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)) + Path.DirectorySeparatorChar);
        foreach (var root in roots)
        {
            if (!full.StartsWith(root, StringComparison.Ordinal))
            {
                continue;
            }

            var linked = false;
            for (var dir = Path.GetDirectoryName(full); dir is not null && dir.Length >= root.Length; dir = Path.GetDirectoryName(dir))
            {
                if (isLinkedFolder(dir))
                {
                    linked = true;
                    break;
                }
            }

            if (!linked)
            {
                return true;
            }
        }

        return false;
    }
}

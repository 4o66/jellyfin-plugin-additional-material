using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AdditionalMaterial.Planning;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdditionalMaterial.Services;

/// <summary>Everything a "Download everything" bundle holds, for one user and one course or section.</summary>
/// <param name="Entries">The files, with their paths in the bundle.</param>
/// <param name="Length">The bundle's exact size.</param>
/// <param name="Key">Identifies the content: the same files, sizes and dates give the same key.</param>
/// <param name="Downloads">How many downloads (archives or single files) it gathers.</param>
public sealed record BundlePlan(IReadOnlyList<StoredEntry> Entries, long Length, string Key, int Downloads);

/// <summary>
/// "Download everything": one zip holding all the material the picker lists for a course or a
/// section, each archive unpacked into a folder that mirrors the course on disk
/// (<c>Course/Section/Lesson/…</c>), single files as they are. Entries are stored, not compressed,
/// so the exact size is known before anything is written.
/// </summary>
public sealed class BundleBuilder
{
    private static readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly ILibraryManager _libraryManager;
    private readonly MaterialLocator _locator;
    private readonly ArchiveContents _contents;
    private readonly ILogger<BundleBuilder> _logger;

    /// <summary>Initializes a new instance of the <see cref="BundleBuilder"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="locator">Finds each item's material.</param>
    /// <param name="contents">Opens files inside archives.</param>
    /// <param name="logger">Logger.</param>
    public BundleBuilder(ILibraryManager libraryManager, MaterialLocator locator, ArchiveContents contents, ILogger<BundleBuilder> logger)
    {
        _libraryManager = libraryManager;
        _locator = locator;
        _contents = contents;
        _logger = logger;
    }

    /// <summary>
    /// Whether an item offers "Download everything": a course or section, with the setting on, and
    /// the "show material from lower levels" setting reaching below it.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether it may.</returns>
    public static bool Offered(BaseItem item)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.OfferDownloadEverything)
        {
            return false;
        }

        var scope = config.ShowOnParents ?? "all";
        return (item is Series && scope == "all") || (item is Season && (scope == "all" || scope == "section"));
    }

    /// <summary>A Windows-safe file or folder name: no reserved characters or names, no trailing dot or space.</summary>
    /// <param name="name">A name.</param>
    /// <returns>The safe name.</returns>
    public static string SafeSegment(string name)
    {
        var chars = name.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal) ? '_' : c).ToArray();
        var safe = new string(chars).TrimEnd(' ', '.');
        if (safe.Length == 0)
        {
            return "_";
        }

        var stem = safe.Split('.')[0];
        return _reserved.Contains(stem) ? "_" + safe : safe;
    }

    /// <summary>The bundle for an item, as a user may download it, or <c>null</c> if it gathers fewer than two downloads.</summary>
    /// <param name="item">A course (series) or section (season).</param>
    /// <param name="user">The user: only items they can see are included.</param>
    /// <returns>The plan.</returns>
    public BundlePlan? Plan(BaseItem item, User user)
    {
        if (!Offered(item) || string.IsNullOrEmpty(item.Path) || !Directory.Exists(item.Path))
        {
            return null;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path));
        var top = item is Season season && season.Series?.Path is { Length: > 0 } seriesPath
            ? SafeSegment(Path.GetFileName(Path.TrimEndingDirectorySeparator(seriesPath)) + " - " + Path.GetFileName(root))
            : SafeSegment(Path.GetFileName(root));

        var members = new List<(BaseItem Item, MaterialFile Material)>();
        if (_locator.FindVerified(item) is { } own)
        {
            members.Add((item, own));
        }

        var query = new InternalItemsQuery(user)
        {
            AncestorIds = new[] { item.Id },
            IncludeItemTypes = item is Series ? new[] { BaseItemKind.Season, BaseItemKind.Episode } : new[] { BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false,
        };
        foreach (var child in _libraryManager.GetItemList(query).Where(c => c is Season || c is Episode))
        {
            if (_locator.FindVerified(child) is { } material)
            {
                members.Add((child, material));
            }
        }

        if (members.Count < 2)
        {
            return null;
        }

        var entries = new List<StoredEntry>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (member, material) in members.OrderBy(m => Folder(root, m.Item), StringComparer.Ordinal))
        {
            var folder = top + Folder(root, member);
            foreach (var entry in EntriesOf(material))
            {
                var name = Unique(folder + "/" + entry.Name, taken);
                entries.Add(entry with { Name = name });
            }
        }

        var identity = new StringBuilder("bundle v1\n");
        foreach (var e in entries)
        {
            identity.Append(e.Name).Append('\0').Append(e.Size).Append('\0').Append(e.Identity).Append('\n');
        }

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())))[..32].ToLowerInvariant();
        return new BundlePlan(entries, StoredZip.Length(entries), key, members.Count);
    }

    /// <summary>A path inside the bundle, its parts made safe; <c>..</c>, <c>.</c> and empty parts are dropped.</summary>
    private static string? SafePath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/').Where(p => p.Length > 0 && p != "." && p != "..").Select(SafeSegment).ToList();
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        if (taken.Add(name))
        {
            return name;
        }

        var slash = name.LastIndexOf('/');
        var leaf = name[(slash + 1)..];
        var dot = leaf.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (leaf[..dot], leaf[dot..]) : (leaf, string.Empty);
        for (var i = 2; ; i++)
        {
            var candidate = name[..(slash + 1)] + stem + " (" + i + ")" + ext;
            if (taken.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Where a member's material goes, below the bundle's top folder: "" for the bundle's own item, else "/Section" or "/Section/Lesson", as on disk.</summary>
    private static string Folder(string root, BaseItem member)
    {
        if (string.IsNullOrEmpty(member.Path))
        {
            return string.Empty;
        }

        var full = Path.GetFullPath(member.Path);
        string relative;
        if (member is Video && !member.IsFolder)
        {
            var dir = Path.GetRelativePath(root, Path.GetDirectoryName(full)!);
            relative = Path.Combine(dir == "." ? string.Empty : dir, Path.GetFileNameWithoutExtension(full));
        }
        else
        {
            relative = Path.GetRelativePath(root, full);
            if (relative == ".")
            {
                return string.Empty;
            }
        }

        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            relative = member.Name;   // outside the folder (it should not be): by name instead
        }

        var safe = SafePath(relative);
        return safe is null ? string.Empty : "/" + safe;
    }

    /// <summary>One download's files: a planned archive's files and notes, an archive's entries, or a single file.</summary>
    private List<StoredEntry> EntriesOf(MaterialFile material)
    {
        if (material.Plan is { } plan)
        {
            if ((plan.SingleFile() ?? plan.LoneZip()) is { } lone)
            {
                return [FileEntry(lone, Path.GetFileName(lone))];
            }

            var list = new List<StoredEntry>();
            foreach (var file in plan.Archive.Files)
            {
                var name = SafePath(plan.Archive.EntryName(file));
                if (name is null)
                {
                    continue;
                }

                if (plan.Archive.Removed.TryGetValue(file, out var why))
                {
                    var note = Encoding.UTF8.GetBytes(ArchiveWriter.RemovalNote(file, plan.Archive.Base, why, plan.Archive.Scans.GetValueOrDefault(file)));
                    list.Add(new StoredEntry(name, note.Length, DateTime.UtcNow.Date, () => new MemoryStream(note, writable: false), "note:" + Convert.ToHexString(SHA256.HashData(note))));
                }
                else
                {
                    list.Add(FileEntry(file, name));
                }
            }

            return list;
        }

        if (!string.Equals(material.Format, "zip", StringComparison.OrdinalIgnoreCase))
        {
            return [FileEntry(material.FullPath, material.FileName)];
        }

        try
        {
            var info = new FileInfo(material.FullPath);
            using var zip = ZipFile.OpenRead(material.FullPath);
            var files = zip.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
            if (files.Any(e => e.IsEncrypted))
            {
                return [FileEntry(material.FullPath, material.FileName)];   // cannot be unpacked: the archive as it is
            }

            var list = new List<StoredEntry>();
            foreach (var e in files)
            {
                var name = SafePath(e.FullName);
                if (name is null)
                {
                    continue;
                }

                var archive = material.FullPath;
                var entryName = e.FullName.Replace('\\', '/');
                list.Add(new StoredEntry(name, e.Length, e.LastWriteTime.UtcDateTime, () => OpenEntry(archive, entryName),
                    $"zip:{archive}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{e.FullName}"));
            }

            return list;
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Additional Material: cannot read {Archive}; the bundle carries it as it is", material.FullPath);
            return [FileEntry(material.FullPath, material.FileName)];
        }
    }

    private Stream OpenEntry(string archive, string entryName)
        => _contents.Open(archive, entryName)?.Stream ?? throw new InvalidDataException($"{entryName} is no longer in {archive}");

    private static StoredEntry FileEntry(string path, string name)
    {
        var info = new FileInfo(path);
        return new StoredEntry(SafePath(name) ?? "file", info.Length, info.LastWriteTimeUtc,
            () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan),
            $"file:{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
    }
}

/// <summary>How a bundle that is being prepared is getting on.</summary>
public sealed class BundleProgress
{
    /// <summary>Gets or sets the bytes written so far.</summary>
    public long Written { get; set; }

    /// <summary>Gets or sets the bundle's size.</summary>
    public long Total { get; set; }

    /// <summary>Gets or sets a value indicating whether preparing it failed.</summary>
    public bool Failed { get; set; }
}

/// <summary>
/// Bundles kept on disk, in the plugin's data folder: ones prepared before download (from the
/// configured size), and streamed ones when keeping them is on. Each is named by its content key,
/// so changed material is never served from an old one. Unused for <see cref="KeepFor"/>, removed.
/// </summary>
public sealed class BundleStore
{
    /// <summary>How long a kept bundle stays after its last download.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    private readonly ConcurrentDictionary<string, BundleProgress> _preparing = new(StringComparer.Ordinal);
    private readonly ILogger<BundleStore> _logger;
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="BundleStore"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public BundleStore(ILogger<BundleStore> logger)
    {
        _logger = logger;
    }

    /// <summary>Gets the folder.</summary>
    public static string Folder => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "bundles");

    /// <summary>The kept bundle with this key, its last use set to now, or <c>null</c>.</summary>
    /// <param name="key">The content key.</param>
    /// <returns>Its path.</returns>
    public string? Ready(string key)
    {
        Sweep();
        var path = PathOf(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }

        return path;
    }

    /// <summary>How preparing a bundle is going, or <c>null</c> if it is not being prepared.</summary>
    /// <param name="key">The content key.</param>
    /// <returns>The progress.</returns>
    public BundleProgress? Progress(string key) => _preparing.GetValueOrDefault(key);

    /// <summary>Starts preparing a bundle on disk in the background, unless it is ready or already being prepared.</summary>
    /// <param name="plan">The bundle.</param>
    /// <returns>Its progress.</returns>
    public BundleProgress Prepare(BundlePlan plan)
    {
        if (Ready(plan.Key) is not null)
        {
            return new BundleProgress { Written = plan.Length, Total = plan.Length };
        }

        var progress = new BundleProgress { Total = plan.Length };
        if (_preparing.TryGetValue(plan.Key, out var running) && !running.Failed)
        {
            return running;
        }

        _preparing[plan.Key] = progress;
        _ = Task.Run(async () =>
        {
            string? tmp = null;
            try
            {
                var sink = Open(plan.Key, out var path);
                tmp = path;
                await using (sink.ConfigureAwait(false))
                {
                    var counting = new CountingStream(sink, n => progress.Written = n);
                    await StoredZip.WriteAsync(plan.Entries, counting, CancellationToken.None).ConfigureAwait(false);
                }

                Keep(plan, tmp);
                _preparing.TryRemove(plan.Key, out _);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Additional Material: could not prepare a bundle ({Size} bytes)", plan.Length);
                progress.Failed = true;
                if (tmp is not null)
                {
                    Discard(tmp);
                }
            }
        });
        return progress;
    }

    /// <summary>Opens a temporary file to write a bundle into; <see cref="Keep"/> puts it in place once complete.</summary>
    /// <param name="key">The content key.</param>
    /// <param name="tmp">The temporary file's path.</param>
    /// <returns>The stream.</returns>
    public FileStream Open(string key, out string tmp)
    {
        Directory.CreateDirectory(Folder);
        tmp = Path.Combine(Folder, key + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        return new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous);
    }

    /// <summary>Puts a completely written bundle in place, or discards it if it is not the expected size.</summary>
    /// <param name="plan">The bundle.</param>
    /// <param name="tmp">The temporary file.</param>
    public void Keep(BundlePlan plan, string tmp)
    {
        try
        {
            if (new FileInfo(tmp).Length != plan.Length)
            {
                File.Delete(tmp);
                return;
            }

            File.Move(tmp, PathOf(plan.Key), overwrite: true);
            _logger.LogInformation("Additional Material: kept a bundle of {Downloads} downloads ({Size} bytes)", plan.Downloads, plan.Length);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Additional Material: could not keep a bundle");
            Discard(tmp);
        }
    }

    /// <summary>Removes a temporary file that will not be kept (an interrupted download).</summary>
    /// <param name="tmp">The temporary file.</param>
    public static void Discard(string tmp)
    {
        try
        {
            File.Delete(tmp);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
        }
    }

    private static string PathOf(string key) => Path.Combine(Folder, key + ".zip");

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastSweep < TimeSpan.FromMinutes(10) || !Directory.Exists(Folder))
        {
            return;
        }

        _lastSweep = now;
        foreach (var file in Directory.EnumerateFiles(Folder))
        {
            try
            {
                var age = now - File.GetLastWriteTimeUtc(file);
                if ((file.EndsWith(".zip", StringComparison.Ordinal) && age > KeepFor) || (file.EndsWith(".tmp", StringComparison.Ordinal) && age > TimeSpan.FromDays(1)))
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Writes through to another stream, counting bytes.</summary>
public sealed class CountingStream : Stream
{
    private readonly Stream _inner;
    private readonly Action<long> _report;
    private long _count;

    /// <summary>Initializes a new instance of the <see cref="CountingStream"/> class.</summary>
    /// <param name="inner">Where the bytes go.</param>
    /// <param name="report">Told the count after each write.</param>
    public CountingStream(Stream inner, Action<long> report)
    {
        _inner = inner;
        _report = report;
    }

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => _count;

    /// <inheritdoc />
    public override long Position { get => _count; set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override void Flush() => _inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner.Write(buffer, offset, count);
        _report(_count += count);
    }

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _report(_count += buffer.Length);
    }
}

/// <summary>Writes to two streams: the download, and a copy being kept. The copy failing never fails the download.</summary>
public sealed class TeeStream : Stream
{
    private readonly Stream _main;
    private Stream? _copy;

    /// <summary>Initializes a new instance of the <see cref="TeeStream"/> class.</summary>
    /// <param name="main">The download.</param>
    /// <param name="copy">The copy.</param>
    public TeeStream(Stream main, Stream copy)
    {
        _main = main;
        _copy = copy;
    }

    /// <summary>Gets a value indicating whether the copy received every byte.</summary>
    public bool CopyComplete => _copy is not null;

    /// <inheritdoc />
    public override bool CanRead => false;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => true;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <inheritdoc />
    public override void Flush() => _main.Flush();

    /// <inheritdoc />
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _main.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (_copy is not null)
        {
            try
            {
                await _copy.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                _copy = null;
            }
        }
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _main.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (_copy is not null)
        {
            try
            {
                await _copy.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                _copy = null;   // disk full, say: the download carries on, nothing is kept
            }
        }
    }
}

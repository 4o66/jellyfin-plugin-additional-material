using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdditionalMaterial.Services;

/// <summary>One file in an archive's listing.</summary>
public sealed class ContentsEntry
{
    /// <summary>Gets or sets the path inside the archive, with <c>/</c> separators.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the uncompressed size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the listing of a zip inside the archive, or <c>null</c> for an ordinary file.</summary>
    public List<ContentsEntry>? Children { get; set; }

    /// <summary>Gets or sets a value indicating whether a nested zip was too large to list.</summary>
    public bool TooLargeToList { get; set; }

    /// <summary>Gets or sets why the file was replaced by a note, or (with <see cref="LeftOut"/>) left out.</summary>
    public string? Reason { get; set; }

    /// <summary>Gets or sets a value indicating whether a rule left the file out of the archive (shown, not downloadable).</summary>
    public bool LeftOut { get; set; }
}

/// <summary>An archive's listing.</summary>
/// <param name="Entries">The files, in archive order.</param>
/// <param name="Truncated">Whether the archive had more entries than are listed.</param>
public sealed record ContentsListing(IReadOnlyList<ContentsEntry> Entries, bool Truncated);

/// <summary>
/// Reads what is inside material archives, from the zip's central directory (nothing is unpacked,
/// except a nested zip that is listed or served, and only in memory). Listings are cached until
/// the archive's size or modification time changes, or the folder index is rebuilt.
/// </summary>
public sealed class ArchiveContents
{
    /// <summary>Separates an outer entry (a nested zip) from the path inside it, as in <c>labs.zip!/lab1.pkt</c>.</summary>
    public const string NestedSeparator = "!/";

    /// <summary>Most entries listed per archive.</summary>
    public const int MaxEntries = 5000;

    /// <summary>Largest nested zip read into memory to list or serve it.</summary>
    public const long MaxNestedBytes = 64L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified, bool Nested, ContentsListing Listing)> _cache = new(StringComparer.Ordinal);
    private readonly ILogger<ArchiveContents> _logger;

    /// <summary>Initializes a new instance of the <see cref="ArchiveContents"/> class.</summary>
    /// <param name="index">The folder index; a rebuild clears the cached listings.</param>
    /// <param name="logger">Logger.</param>
    public ArchiveContents(MaterialIndex index, ILogger<ArchiveContents> logger)
    {
        _logger = logger;
        index.Rebuilt += () => _cache.Clear();
    }

    /// <summary>Lists an archive.</summary>
    /// <param name="archivePath">The verified archive path.</param>
    /// <returns>The listing, or <c>null</c> if the file is not a readable zip.</returns>
    public ContentsListing? List(string archivePath)
    {
        var nested = Plugin.Instance?.Configuration.ListNestedZips ?? true;
        FileInfo info;
        try
        {
            info = new FileInfo(archivePath);
            if (_cache.TryGetValue(archivePath, out var hit) && hit.Size == info.Length && hit.Modified == info.LastWriteTimeUtc && hit.Nested == nested)
            {
                return hit.Listing;
            }

            using var zip = ZipFile.OpenRead(archivePath);
            var listing = Read(zip, nested);
            _cache[archivePath] = (info.Length, info.LastWriteTimeUtc, nested, listing);
            if (_cache.Count > 20000)
            {
                _cache.Clear();
            }

            return listing;
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Additional Material: cannot read {Archive}", archivePath);
            return null;
        }
    }

    /// <summary>
    /// Opens one file inside an archive for reading. <paramref name="entryPath"/> must name an entry
    /// exactly as the listing shows it; nothing is ever written to disk.
    /// </summary>
    /// <param name="archivePath">The verified archive path.</param>
    /// <param name="entryPath">The entry, e.g. <c>slides/intro.pdf</c> or <c>labs.zip!/lab1.pkt</c>.</param>
    /// <returns>The stream (dispose it) and the entry's size, or <c>null</c> if there is no such file.</returns>
    public (Stream Stream, long Size)? Open(string archivePath, string entryPath)
    {
        ZipArchive? outer = null;
        try
        {
            outer = ZipFile.OpenRead(archivePath);
            var entry = FindFile(outer, entryPath);
            if (entry is not null)
            {
                return (new OwningStream(entry.Open(), outer), entry.Length);
            }

            // A file inside a nested zip, if listing them is on.
            var split = entryPath.IndexOf(NestedSeparator, StringComparison.Ordinal);
            if ((Plugin.Instance?.Configuration.ListNestedZips ?? true) && split > 0)
            {
                var container = FindFile(outer, entryPath[..split]);
                if (container is not null && IsZipName(container.FullName) && container.Length <= MaxNestedBytes)
                {
                    var inner = OpenNested(container);
                    var innerEntry = inner is null ? null : FindFile(inner, entryPath[(split + NestedSeparator.Length)..]);
                    if (inner is not null && innerEntry is not null)
                    {
                        return (new OwningStream(innerEntry.Open(), inner, outer), innerEntry.Length);
                    }

                    inner?.Dispose();
                }
            }

            outer.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
        {
            outer?.Dispose();
            _logger.LogWarning(ex, "Additional Material: cannot read {Entry} in {Archive}", entryPath, archivePath);
            return null;
        }
    }

    private static ContentsListing Read(ZipArchive zip, bool nested)
    {
        var entries = new List<ContentsEntry>();
        var truncated = false;
        foreach (var entry in zip.Entries)
        {
            if (IsFolder(entry))
            {
                continue;
            }

            if (entries.Count >= MaxEntries)
            {
                truncated = true;
                break;
            }

            var item = new ContentsEntry { Path = Normalize(entry.FullName), Size = entry.Length };
            if (nested && IsZipName(entry.FullName))
            {
                if (entry.Length > MaxNestedBytes)
                {
                    item.TooLargeToList = true;
                }
                else
                {
                    using var inner = OpenNested(entry);
                    if (inner is not null)
                    {
                        // One level only: a zip inside a nested zip is listed as a plain file.
                        item.Children = inner.Entries.Where(e => !IsFolder(e)).Take(MaxEntries)
                            .Select(e => new ContentsEntry { Path = Normalize(e.FullName), Size = e.Length }).ToList();
                    }
                }
            }

            entries.Add(item);
        }

        return new ContentsListing(entries, truncated);
    }

    private static ZipArchive? OpenNested(ZipArchiveEntry entry)
    {
        var buffer = new MemoryStream(checked((int)entry.Length));
        using (var source = entry.Open())
        {
            source.CopyTo(buffer);
        }

        buffer.Position = 0;
        try
        {
            return new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException)
        {
            buffer.Dispose();
            return null;   // named .zip but is not one: listed as a plain file
        }
    }

    private static ZipArchiveEntry? FindFile(ZipArchive zip, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return zip.Entries.FirstOrDefault(e => !IsFolder(e) && string.Equals(Normalize(e.FullName), path, StringComparison.Ordinal));
    }

    private static bool IsFolder(ZipArchiveEntry entry) => entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

    private static bool IsZipName(string name) => name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string name) => name.Replace('\\', '/');

    /// <summary>A read-only stream that disposes the archives it came from when it is disposed.</summary>
    private sealed class OwningStream : Stream
    {
        private readonly Stream _inner;
        private readonly IDisposable[] _owners;

        public OwningStream(Stream inner, params IDisposable[] owners)
        {
            _inner = inner;
            _owners = owners;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                foreach (var owner in _owners)
                {
                    owner.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}

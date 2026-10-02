using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.AdditionalMaterial.Planning;

/// <summary>
/// Writes a planned archive: each file stored or deflated depending on whether compressing it is
/// worth it, and blocked files replaced by the same note the helper script writes.
/// </summary>
public static class ArchiveWriter
{
    private const int SampleBytes = 64 * 1024;

    // Formats that are compressed already: deflating them again costs time and saves nothing.
    private static readonly HashSet<string> _compressedExt = [".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".lz", ".cab",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".avif", ".jp2",
        ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".flac", ".wma",
        ".docx", ".xlsx", ".pptx", ".vsdx", ".odt", ".ods", ".odp", ".epub", ".pkt", ".pka", ".apk", ".jar",
        .. ActiveContent.VideoExt];

    private static readonly Lazy<Dictionary<string, string>> _noteText = new(LoadNoteText);

    /// <summary>
    /// Whether deflating a file is worth it: not for formats that are compressed already (by
    /// extension or first bytes), otherwise only if a 64 KB sample shrinks by at least 10%.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>Whether to deflate it.</returns>
    public static bool WorthCompressing(string path)
    {
        if (_compressedExt.Contains(Py.Lower(Py.Suffix(Path.GetFileName(path)))))
        {
            return false;
        }

        try
        {
            var sample = new byte[SampleBytes];
            int n;
            using (var f = File.OpenRead(path))
            {
                n = f.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);
            }

            if (n < 512)
            {
                return n > 0;   // tiny: compressing costs nothing either way
            }

            var head = sample.AsSpan(0, n);
            if (head.StartsWith("PK\x03\x04"u8) || head.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c]) || head.StartsWith("Rar!"u8)
                || head.StartsWith((ReadOnlySpan<byte>)[0x1f, 0x8b]) || head.StartsWith((ReadOnlySpan<byte>)[0xff, 0xd8, 0xff])
                || head.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
            {
                return false;
            }

            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                deflate.Write(sample, 0, n);
            }

            return output.Length <= n * 0.9;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Writes the archive to <paramref name="target"/>, through a temporary file in the same folder.</summary>
    /// <param name="archive">The plan.</param>
    /// <param name="target">Where to write it.</param>
    /// <returns>The written archive's size.</returns>
    public static long Write(PlannedArchive archive, string target)
    {
        var dir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, ".am-" + Guid.NewGuid().ToString("N") + ".zip.tmp");
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                foreach (var file in archive.Files)
                {
                    var name = archive.EntryName(file);
                    if (archive.Removed.TryGetValue(file, out var why))
                    {
                        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                        w.Write(RemovalNote(file, archive.Base, why));
                    }
                    else
                    {
                        var entry = zip.CreateEntry(name, WorthCompressing(file) ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
                        entry.LastWriteTime = File.GetLastWriteTime(file);
                        using var input = File.OpenRead(file);
                        using var output = entry.Open();
                        input.CopyTo(output);
                    }
                }
            }

            File.Move(tmp, target, overwrite: true);
            return new FileInfo(target).Length;
        }
        catch
        {
            File.Delete(tmp);
            throw;
        }
    }

    /// <summary>The note that replaces a blocked file: the script's removal_note, word for word.</summary>
    /// <param name="path">The blocked file.</param>
    /// <param name="baseDir">The archive's base folder.</param>
    /// <param name="why">Why it was blocked.</param>
    /// <returns>The note.</returns>
    public static string RemovalNote(string path, string baseDir, string why)
    {
        string digest;
        using (var f = File.OpenRead(path))
        {
            digest = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
        }

        string T(string key, params (string Name, string Value)[] values)
        {
            var text = _noteText.Value.GetValueOrDefault(key, key);
            foreach (var (n, v) in values)
            {
                text = text.Replace("{" + n + "}", v, StringComparison.Ordinal);
            }

            return text;
        }

        var labels = new[] { T("note.file"), T("note.size"), "SHA-256", T("note.scan") };
        var width = labels.Max(l => l.Length) + 2;
        string Row(string label, string value) => "  " + (label + ":").PadRight(width) + value + "\n";
        var rel = Path.GetRelativePath(baseDir, path).Replace('\\', '/');
        return T("note.title", ("name", Path.GetFileName(path))) + "\n\n"
            + T("note.because", ("why", why)) + "\n"
            + T("note.risk") + "\n\n"
            + Row(T("note.file"), rel)
            + Row(T("note.size"), T("note.bytes", ("n", new FileInfo(path).Length.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            + Row("SHA-256", digest)
            + Row(T("note.scan"), "not checked")
            + "\n" + T("note.lookup") + "\n"
            + "  https://www.virustotal.com/gui/search/" + digest + "\n\n"
            + T("note.override") + "\n";
    }

    /// <summary>The rule files built into the plugin (tools/rules), sorted by name as the script reads them.</summary>
    /// <returns>File names and contents.</returns>
    public static List<(string FileName, string Text)> BuiltInRules()
    {
        var assembly = typeof(ArchiveWriter).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Rules.", StringComparison.Ordinal) && n.EndsWith(".toml", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => (n["Rules.".Length..], ReadResource(assembly, n)))
            .ToList();
    }

    private static Dictionary<string, string> LoadNoteText()
    {
        var assembly = typeof(ArchiveWriter).Assembly;
        var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n == "Notes.en.json");
        return name is null ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(ReadResource(assembly, name)) ?? new();
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

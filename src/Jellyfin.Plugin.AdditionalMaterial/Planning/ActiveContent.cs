using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AdditionalMaterial.Planning;

/// <summary>
/// Finds active content in documents (macros, embedded objects, external templates, DDE fields,
/// PDF JavaScript, launch actions and embedded files, RTF objects) and executable content in
/// archives. A port of the helper script's checks; the two must agree.
/// </summary>
public static class ActiveContent
{
    /// <summary>Video file extensions.</summary>
    public static readonly HashSet<string> VideoExt = [".mp4", ".mkv", ".avi", ".m4v", ".mov", ".wmv", ".flv", ".webm", ".ts", ".m2ts",
        ".mpg", ".mpeg", ".3gp", ".ogv", ".vob"];

    /// <summary>Subtitle file extensions.</summary>
    public static readonly HashSet<string> SubtitleExt = [".srt", ".vtt", ".ass", ".ssa", ".sub", ".idx", ".sup", ".smi"];

    /// <summary>Executable and script extensions, macro-enabled Office files included.</summary>
    public static readonly HashSet<string> ExecutableExt = [".exe", ".dll", ".msi", ".msp", ".bat", ".cmd", ".com", ".scr", ".pif", ".cpl",
        ".hta", ".lnk", ".vbs", ".vbe", ".wsf", ".jar", ".reg", ".apk", ".dmg", ".pkg",
        ".ps1", ".psm1", ".sys", ".ocx", ".appx", ".msix", ".deb", ".rpm", ".app",
        ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppsm", ".ppam", ".sldm"];

    /// <summary>Archive extensions whose members are checked.</summary>
    public static readonly HashSet<string> NestedArchiveExt = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz"];

    private const long MaxScan = 200L * 1024 * 1024;
    private const int MaxInflate = 50 * 1024 * 1024;

    private static readonly HashSet<string> _ooxmlExt = [".docx", ".dotx", ".xlsx", ".xltx", ".xlsb", ".pptx", ".potx", ".ppsx", ".vsdx"];
    private static readonly HashSet<string> _ole2Ext = [".doc", ".dot", ".xls", ".xlt", ".ppt", ".pot", ".pps", ".msg"];
    private static readonly HashSet<string> _documentExt = [".pdf", ".rtf", .. _ooxmlExt, .. _ole2Ext];

    private static readonly Regex _riskyRel = Rx(@"/(attachedTemplate|oleObject|frame|subDocument|control|package)$");
    private static readonly (string Token, string Label)[] _pdfTokens =
    [
        ("/JavaScript", "JavaScript"), ("/JS", "JavaScript"), ("/Launch", "a Launch action (runs a program)"),
        ("/EmbeddedFile", "embedded files"), ("/RichMedia", "RichMedia (Flash) content"),
    ];

    private static readonly Regex[] _pdfTokenRegex = _pdfTokens.Select(t => Rx(Regex.Escape(t.Token) + @"(?=[ \t\n\r\f\v/()<>\[\]{}%]|$)")).ToArray();
    private static readonly Regex _pdfNameHex = Rx("/[A-Za-z0-9#]*#[0-9A-Fa-f]{2}[A-Za-z0-9#]*");
    private static readonly Regex _hexEscape = Rx("#([0-9A-Fa-f]{2})");
    private static readonly Regex _stream = Rx("stream\r?\n");
    private static readonly Regex _binaryStream = Rx(@"/(Image|Font|FontFile\d?|XObject)\b|/Subtype[ \t\n\r\f\v]*/(Image|Type1C|CIDFontType0C|OpenType)");
    private static readonly Regex _embeddings = Rx(@"/embeddings/(oleObject[^/]*\.bin|[^/]*\.(bin|exe|dll|scr|js|vbs|bat|cmd|ps1|hta))$", true);
    private static readonly Regex _relationship = Rx(@"<Relationship\b[^>]*>");
    private static readonly Regex _relType = Rx("Type=\"([^\"]+)\"");
    private static readonly Regex _relTarget = Rx("Target=\"([^\"]+)\"");
    private static readonly Regex _wordPart = Rx(@"\A(word/(document|header\d*|footer\d*)\.xml)$");
    private static readonly Regex _dde = Rx(@"\bDDE(AUTO)?\b");
    private static readonly Regex _rtfObject = Rx(@"\\obj(data|emb|link|autlink|update)\b|\\object\b");

    static ActiveContent()
    {
        // Zip entry names without the UTF-8 flag are CP437, as Python's zipfile reads them.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Gets CP437, the encoding of zip entry names that lack the UTF-8 flag.</summary>
    public static Encoding ZipNames => Encoding.GetEncoding(437);

    /// <summary>Why a document's content is active, or <c>null</c>.</summary>
    /// <param name="name">The file name (its extension counts).</param>
    /// <param name="data">The file's bytes.</param>
    /// <returns>The reason, or <c>null</c>.</returns>
    public static string? Check(string name, byte[] data)
    {
        var ext = Py.Lower(Py.Suffix(name));
        if (StartsWith(data, "%PDF"u8) || ext == ".pdf")
        {
            return Pdf(data);
        }

        if (StartsWith(data, "{\\rtf"u8) || ext == ".rtf")
        {
            return _rtfObject.IsMatch(Py.Bytes(data)) ? "the RTF contains embedded objects" : null;
        }

        if (StartsWith(data, [0xd0, 0xcf, 0x11, 0xe0]) && (_ole2Ext.Contains(ext) || ext.Length == 0))
        {
            return Ole2(data);
        }

        if (StartsWith(data, "PK\x03\x04"u8) && (_ooxmlExt.Contains(ext) || ext.Length == 0))
        {
            try
            {
                using var z = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read, false, ZipNames);
                if (z.Entries.Any(e => e.FullName == "[Content_Types].xml"))
                {
                    return Ooxml(z);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is NotSupportedException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The script's <c>file_active_content</c>: checks documents (and files without an extension) up to 200 MB.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The reason, or <c>null</c>.</returns>
    public static string? CheckFile(string path)
    {
        var ext = Py.Lower(Py.Suffix(Path.GetFileName(path)));
        if (!_documentExt.Contains(ext) && ext.Length > 0)
        {
            return null;
        }

        byte[] data;
        try
        {
            if (new FileInfo(path).Length > MaxScan)
            {
                return null;
            }

            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return null;
        }

        return Check(Path.GetFileName(path), data);
    }

    /// <summary>Documents inside a zip (one level) that carry active content, as "name: reason".</summary>
    /// <param name="path">The zip.</param>
    /// <returns>The findings.</returns>
    public static List<string> ZipDocuments(string path)
    {
        var found = new List<string>();
        try
        {
            using var z = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read, false, ZipNames);
            foreach (var entry in z.Entries)
            {
                var ext = Py.Lower(Py.Suffix(entry.FullName));
                if (!_documentExt.Contains(ext) || entry.Length > MaxScan)
                {
                    continue;
                }

                string? why;
                try
                {
                    why = Check(entry.FullName, Read(entry));
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is NotSupportedException)
                {
                    continue;
                }

                if (why is not null)
                {
                    found.Add($"{entry.FullName}: {why.Replace("the document contains ", string.Empty, StringComparison.Ordinal).Replace("the PDF contains ", string.Empty, StringComparison.Ordinal)}");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
        {
        }

        return found;
    }

    /// <summary>Names inside an archive and inside zips nested in a zip; for formats other than zip, a "not checked" marker.</summary>
    /// <param name="path">The archive.</param>
    /// <returns>The member names.</returns>
    public static List<string> ArchiveMembers(string path)
    {
        var ext = Py.Lower(Py.Suffix(Path.GetFileName(path)));
        if (!NestedArchiveExt.Contains(ext))
        {
            return [];
        }

        if (ext != ".zip")
        {
            // The script lists these with a 7z program when it has one; the plugin has none.
            return ["(contents not checked: no 7z program to read this archive)"];
        }

        var names = new List<string>();
        try
        {
            using var z = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read, false, ZipNames);
            foreach (var entry in z.Entries)
            {
                names.Add(entry.FullName);
                if (Py.Lower(entry.FullName).EndsWith(".zip", StringComparison.Ordinal) && entry.Length < 512L * 1024 * 1024)
                {
                    try
                    {
                        using var inner = new ZipArchive(new MemoryStream(Read(entry)), ZipArchiveMode.Read, false, ZipNames);
                        names.AddRange(inner.Entries.Select(e => entry.FullName + "/" + e.FullName));
                    }
                    catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is NotSupportedException)
                    {
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is NotSupportedException)
        {
        }

        return names;
    }

    /// <summary>
    /// The name a file gets inside the archive: its path below <paramref name="baseDir"/>. A file
    /// that lost its extension ("Plan200301docx") gets one back, detected from its content.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="baseDir">The folder entries are stored relative to.</param>
    /// <returns>The entry name, with <c>/</c> separators.</returns>
    public static string ArchiveName(string path, string baseDir)
    {
        var rel = Path.GetRelativePath(baseDir, path).Replace('\\', '/');
        if (Py.Suffix(Path.GetFileName(path)).Length > 0)
        {
            return rel;
        }

        string? ext = null;
        try
        {
            var head = new byte[8];
            int got;
            using (var f = File.OpenRead(path))
            {
                got = f.ReadAtLeast(head, 8, throwOnEndOfStream: false);
            }

            var h = head.AsSpan(0, got);
            if (h.StartsWith("%PDF"u8))
            {
                ext = ".pdf";
            }
            else if (h.StartsWith("PK\x03\x04"u8))
            {
                ext = ".zip";
                using var z = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read, false, ZipNames);
                var listing = z.Entries.Select(e => e.FullName).ToList();
                if (listing.Contains("[Content_Types].xml"))
                {
                    ext = new[] { ("word/", ".docx"), ("xl/", ".xlsx"), ("ppt/", ".pptx") }
                        .Where(d => listing.Any(n => n.StartsWith(d.Item1, StringComparison.Ordinal)))
                        .Select(d => d.Item2).FirstOrDefault() ?? ".zip";
                }
            }
            else if (h.StartsWith((ReadOnlySpan<byte>)[0xd0, 0xcf, 0x11, 0xe0]))
            {
                ext = ".doc";
            }
            else if (got >= 4 && (h[..4].SequenceEqual((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G'])
                || h[..4].SequenceEqual((ReadOnlySpan<byte>)[0xff, 0xd8, 0xff, 0xe0]) || h[..4].SequenceEqual((ReadOnlySpan<byte>)[0xff, 0xd8, 0xff, 0xe1])))
            {
                ext = h[0] == 0x89 ? ".png" : ".jpg";
            }
        }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
        {
            return rel;
        }

        if (ext is null)
        {
            return rel;
        }

        var hint = ext[1..];
        if (Py.Lower(rel).EndsWith(hint, StringComparison.Ordinal))
        {
            return rel[..^hint.Length].TrimEnd('.') + ext;
        }

        return rel + ext;
    }

    private static string? Pdf(byte[] data)
    {
        var text = Py.Bytes(data);
        var chunks = new List<string> { text };

        // Look inside compressed streams too, but only object streams or streams that inflate to
        // PDF syntax: image and font data is binary, where "/JS" can occur by chance.
        foreach (Match m in _stream.Matches(text))
        {
            var start = m.Index + m.Length;
            var end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0 || end - start > MaxInflate)
            {
                continue;
            }

            var header = text[Math.Max(0, m.Index - 512)..m.Index];
            var open = header.LastIndexOf("<<", StringComparison.Ordinal);
            if (open >= 0)
            {
                header = header[open..];
            }

            if (_binaryStream.IsMatch(header))
            {
                continue;
            }

            var inflated = Inflate(data, start, end - start);
            if (inflated is null)
            {
                continue;
            }

            if (header.Contains("/ObjStm", StringComparison.Ordinal) || LooksLikePdfSyntax(inflated))
            {
                chunks.Add(Py.Bytes(inflated));
            }
        }

        var found = new List<string>();
        foreach (var chunk in chunks)
        {
            var decoded = _pdfNameHex.Replace(chunk, n => _hexEscape.Replace(n.Value, h => ((char)Convert.ToInt32(h.Groups[1].Value, 16)).ToString()));
            for (var i = 0; i < _pdfTokens.Length; i++)
            {
                if (_pdfTokenRegex[i].IsMatch(decoded) && !found.Contains(_pdfTokens[i].Label))
                {
                    found.Add(_pdfTokens[i].Label);
                }
            }
        }

        return found.Count > 0 ? "the PDF contains " + string.Join(", ", found) : null;
    }

    private static byte[]? Inflate(byte[] data, int offset, int count)
    {
        try
        {
            using var z = new ZLibStream(new MemoryStream(data, offset, count, writable: false), CompressionMode.Decompress);
            var output = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while (output.Length < MaxInflate && (n = z.Read(buffer, 0, (int)Math.Min(buffer.Length, MaxInflate - output.Length))) > 0)
            {
                output.Write(buffer, 0, n);
            }

            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
        {
            return null;   // corrupt stream (zlib.error in the script): skipped
        }
    }

    private static bool LooksLikePdfSyntax(byte[] chunk)
    {
        var n = Math.Min(chunk.Length, 4096);
        if (n == 0)
        {
            return false;
        }

        var printable = 0;
        for (var i = 0; i < n; i++)
        {
            var b = chunk[i];
            if ((b >= 32 && b < 127) || b is 9 or 10 or 13)
            {
                printable++;
            }
        }

        return (double)printable / n > 0.9;
    }

    private static string? Ooxml(ZipArchive z)
    {
        var found = new List<string>();
        var names = z.Entries.Select(e => e.FullName).ToList();
        if (names.Any(n => Py.Lower(n).EndsWith("vbaproject.bin", StringComparison.Ordinal)))
        {
            found.Add("macros");
        }

        if (names.Any(n => _embeddings.IsMatch(n)))
        {
            found.Add("embedded OLE objects");
        }

        if (names.Any(n => Py.Lower(n).Contains("/activex/", StringComparison.Ordinal)))
        {
            found.Add("ActiveX controls");
        }

        foreach (var entry in z.Entries.Where(e => e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            string xml;
            try
            {
                xml = Encoding.UTF8.GetString(Read(entry));
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is NotSupportedException)
            {
                continue;
            }

            foreach (Match rel in _relationship.Matches(xml))
            {
                var tag = rel.Value;
                var kind = _relType.Match(tag);
                if (tag.Contains("TargetMode=\"External\"", StringComparison.Ordinal) && kind.Success && _riskyRel.IsMatch(kind.Groups[1].Value))
                {
                    var target = _relTarget.Match(tag);
                    var what = kind.Groups[1].Value[(kind.Groups[1].Value.LastIndexOf('/') + 1)..];
                    var shown = target.Success ? target.Groups[1].Value : "?";
                    var label = $"an external {what} ({(shown.Length > 80 ? shown[..80] : shown)})";
                    if (!found.Contains(label))
                    {
                        found.Add(label);
                    }
                }
            }
        }

        foreach (var entry in z.Entries.Where(e => _wordPart.IsMatch(e.FullName)))
        {
            try
            {
                if (_dde.IsMatch(Py.Bytes(Read(entry))))
                {
                    found.Add("a DDE field (asks to run a command)");
                    break;
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is NotSupportedException)
            {
            }
        }

        return found.Count > 0 ? "the document contains " + string.Join(", ", found) : null;
    }

    private static string? Ole2(byte[] data)
    {
        var found = new List<string>();
        static byte[] U(string s) => Encoding.Unicode.GetBytes(s);
        var span = data.AsSpan();
        if (span.IndexOf(U("_VBA_PROJECT")) >= 0 || span.IndexOf((ReadOnlySpan<byte>)[.. U("VBA"), 0, 0]) >= 0 || span.IndexOf(U("Macros")) >= 0)
        {
            found.Add("macros");
        }

        if (span.IndexOf(U("ObjectPool")) >= 0 || span.IndexOf(U("\x01Ole10Native")) >= 0)
        {
            found.Add("embedded objects");
        }

        return found.Count > 0 ? "the document contains " + string.Join(", ", found) : null;
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        var buffer = new MemoryStream();
        s.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static bool StartsWith(byte[] data, ReadOnlySpan<byte> prefix) => data.AsSpan().StartsWith(prefix);

    private static Regex Rx(string pattern, bool ignoreCase = false) => Py.Compile(pattern, ignoreCase);
}

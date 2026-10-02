using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AdditionalMaterial.Planning;

/// <summary>How the planner decides, the script's command-line options as settings.</summary>
public sealed class PlanOptions
{
    /// <summary>Gets or sets the levels to build: lesson, section, course.</summary>
    public HashSet<string> Levels { get; set; } = ["lesson", "section", "course"];

    /// <summary>Gets or sets <c>auto</c>, <c>always</c> or <c>never</c>: one archive for the whole course.</summary>
    public string Package { get; set; } = "auto";

    /// <summary>Gets or sets <c>number</c> (same name or lesson number) or <c>name</c> (same name only).</summary>
    public string Match { get; set; } = "number";

    /// <summary>Gets or sets a value indicating whether executable content is included as is.</summary>
    public bool AllowExecutables { get; set; }

    /// <summary>Gets or sets a value indicating whether documents with active content are included as is.</summary>
    public bool AllowActiveDocuments { get; set; }

    /// <summary>Gets or sets globs of files to leave out (name, or path within the course).</summary>
    public List<string> Excludes { get; set; } = [];
}

/// <summary>One archive the planner would build.</summary>
public sealed class PlannedArchive
{
    /// <summary>Gets or sets lesson, section or course.</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Gets or sets the archive's path.</summary>
    public string Archive { get; set; } = string.Empty;

    /// <summary>Gets or sets the folder entries are stored relative to.</summary>
    public string Base { get; set; } = string.Empty;

    /// <summary>Gets the files, in the order they go into the archive.</summary>
    public List<string> Files { get; } = [];

    /// <summary>Gets the files a rule left out that would otherwise be in this archive, and why (not in the script's report).</summary>
    public List<(string File, string Reason)> LeftOut { get; } = [];

    /// <summary>Gets, for left-out redirect placeholders, the website each one sends the browser to.</summary>
    public Dictionary<string, string> Links { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the files replaced by a note, and why.</summary>
    public Dictionary<string, string> Removed { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets why each file is in this archive.</summary>
    public Dictionary<string, string> Placement { get; } = new(StringComparer.Ordinal);

    /// <summary>The entry name a file gets: its path below <see cref="Base"/>, with a note's suffix for removed files.</summary>
    /// <param name="file">One of <see cref="Files"/>.</param>
    /// <returns>The entry name.</returns>
    public string EntryName(string file)
    {
        var name = ActiveContent.ArchiveName(file, Base);
        return Removed.ContainsKey(file) ? name + ".REMOVED.txt" : name;
    }
}

/// <summary>
/// Decides which files go into which archive: the helper script's planner
/// (tools/make_additional_material.py), ported so the plugin can build archives itself and show
/// what would be built. The two must make the same decisions; tools/planner-compare checks that.
/// </summary>
public sealed class Planner
{
    /// <summary>The folder-level archive's name.</summary>
    public const string TriggerFolder = "additional-material.zip";

    /// <summary>The suffix of a lesson's archive.</summary>
    public const string TriggerSuffix = ".material.zip";

    private static readonly Regex _jellyfinImage = Py.Compile(
        @"^(folder|poster|cover|fanart|backdrop\d*|banner|logo|clearart|clearlogo|landscape|thumb|disc|art|"
        + @"season\d*-(poster|banner|fanart|landscape)|season-specials-poster|.*-(thumb|poster|fanart|landscape))"
        + @"\.(jpe?g|png|webp|gif|tbn)$", ignoreCase: true);

    private static readonly HashSet<string> _jellyfinDirs = ["extrafanart", "extrathumbs", "metadata"];
    private static readonly Regex _lessonNumber = Py.Compile(@"^\s*0*(\d{1,4})(?=[\s._\-)\]]|$)", ignoreCase: false);
    private static readonly HashSet<string> _quietSkips = ["video, subtitle or nfo", "Jellyfin artwork", "hidden or Jellyfin data (dot) file"];

    private readonly PlanOptions _options;
    private readonly List<Rule> _rules;
    private readonly List<Rule> _skipRules;
    private readonly HashSet<string> _attachmentDirs;
    private readonly HashSet<string> _quietReasons;
    private readonly Dictionary<string, string> _blocked = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="Planner"/> class.</summary>
    /// <param name="rules">The loaded rules.</param>
    /// <param name="options">The options.</param>
    public Planner(List<Rule> rules, PlanOptions options)
    {
        _rules = rules;
        _options = options;
        _skipRules = rules.Where(r => r.Action == "skip").ToList();
        _attachmentDirs = rules.Where(r => r.Action == "attachment-folder").SelectMany(r => r.FolderNames).ToHashSet(StringComparer.Ordinal);
        _quietReasons = _skipRules.Where(r => r.Quiet).Select(r => $"{r.Reason} [rule {r.Id}]").ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Gets files left out, with the reason (quiet reasons are not listed).</summary>
    public List<(string File, string Reason)> Skipped { get; } = [];

    /// <summary>Gets where each left-out redirect placeholder points (rules with <c>show_link</c>).</summary>
    public Dictionary<string, string> Links { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets, per course, the video-less subfolders that made it one archive (empty when it was not packaged).</summary>
    public Dictionary<string, List<string>> Packaged { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether a name is one of the plugin's trigger archives.</summary>
    /// <param name="name">A file name.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsTrigger(string name)
    {
        var low = Py.Lower(name);
        return low == TriggerFolder || low.EndsWith(TriggerSuffix, StringComparison.Ordinal)
            || low == "additional-material.7z" || low.EndsWith(".material.7z", StringComparison.Ordinal);
    }

    /// <summary>The course folders in <paramref name="target"/>: the folder itself if it looks like one course, else its subfolders with videos.</summary>
    /// <param name="target">A course or library folder.</param>
    /// <param name="mode">auto, course or library.</param>
    /// <returns>The course folders.</returns>
    public static List<string> CoursesIn(string target, string mode = "auto")
    {
        if (mode == "course" || (mode == "auto" && LooksLikeCourse(target)))
        {
            return [target];
        }

        return Subdirs(target).Where(HasVideos).ToList();
    }

    /// <summary>Plans one course.</summary>
    /// <param name="course">The course folder.</param>
    /// <returns>The archives, sorted by path.</returns>
    public List<PlannedArchive> PlanCourse(string course)
    {
        course = Path.TrimEndingDirectorySeparator(Path.GetFullPath(course));
        var videosByDir = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var material = new List<string>();
        var firstSkip = Skipped.Count;
        Walk(course, course, videosByDir, material);
        var walkSkips = Skipped.Skip(firstSkip).ToList();

        var allVideos = videosByDir.Values.SelectMany(v => v).ToList();
        var subdirs = Subdirs(course);
        var sections = subdirs.Where(d => allVideos.Any(v => Under(v, d))).ToList();

        // Subfolders that hold material but no videos: Jellyfin shows no page for them, so their
        // files could only ride on the course page while the rest sat in section and lesson zips.
        // When a course has any, it is packaged as one archive (package auto, the default).
        var videoLess = subdirs.Where(d => !sections.Contains(d) && material.Any(m => Under(m, d))).OrderBy(d => d, Py.Order).ToList();
        var single = _options.Package == "always" || (_options.Package == "auto" && videoLess.Count > 0);
        Packaged[course] = single ? videoLess.Select(Path.GetFileName).Cast<string>().ToList() : [];
        var levels = single ? new HashSet<string> { "course" } : _options.Levels;
        var groups = new Dictionary<string, PlannedArchive>(StringComparer.Ordinal);

        void Add(string level, string archive, string baseDir, string path, string how)
        {
            if (!groups.TryGetValue(archive, out var g))
            {
                g = new PlannedArchive { Level = level, Archive = archive, Base = baseDir };
                groups[archive] = g;
            }

            g.Files.Add(path);
            g.Placement[path] = how;
            if (_blocked.TryGetValue(path, out var why))
            {
                g.Removed[path] = why;
            }
        }

        string? packagedNote = null;
        if (single)
        {
            packagedNote = videoLess.Count > 0
                ? "whole course packaged as one archive: no videos in " + string.Join(", ", videoLess.Select(Path.GetFileName))
                : "whole course packaged as one archive (--package always)";
        }

        foreach (var path in material)
        {
            Screen(path);
            var match = levels.Contains("lesson") ? MatchLesson(path, videosByDir) : null;
            if (match is not null)
            {
                var (lesson, how) = match.Value;
                Add("lesson", Path.Combine(Path.GetDirectoryName(lesson)!, Py.Stem(Path.GetFileName(lesson)) + TriggerSuffix), Path.GetDirectoryName(lesson)!, path, how);
                continue;
            }

            var section = sections.FirstOrDefault(s => Under(path, s));
            var parent = Path.GetDirectoryName(path)!;
            if (section is not null && levels.Contains("section"))
            {
                Add("section", Path.Combine(section, TriggerFolder), section, path, "in the section, not tied to one lesson");
            }
            else if (levels.Contains("course"))
            {
                string how;
                if (packagedNote is not null)
                {
                    how = packagedNote;
                }
                else if (section is null && parent != course)
                {
                    how = "in a folder with no videos (no Jellyfin page for it)";
                }
                else if (parent == course)
                {
                    how = "in the course folder";
                }
                else
                {
                    how = "rolled up to the course (--levels " + string.Join(",", new[] { "lesson", "section", "course" }.Where(_options.Levels.Contains)) + ")";
                }

                Add("course", Path.Combine(course, TriggerFolder), course, path, how);
            }
            else
            {
                Skipped.Add((path, "its level is not enabled (--levels)"));
            }
        }

        // Where each left-out file would have gone, so the contents view can show it (C# only).
        foreach (var (file, reason) in walkSkips)
        {
            if (reason == "existing Additional Material archive")
            {
                continue;
            }

            var lesson = levels.Contains("lesson") ? MatchLesson(file, videosByDir) : null;
            string? target = null;
            if (lesson is not null)
            {
                target = Path.Combine(Path.GetDirectoryName(lesson.Value.Lesson)!, Py.Stem(Path.GetFileName(lesson.Value.Lesson)) + TriggerSuffix);
            }

            if (target is null || !groups.ContainsKey(target))
            {
                var section = sections.FirstOrDefault(sd => Under(file, sd));
                target = section is not null && levels.Contains("section") ? Path.Combine(section, TriggerFolder) : null;
            }

            if (target is null || !groups.ContainsKey(target))
            {
                target = Path.Combine(course, TriggerFolder);
            }

            if (groups.TryGetValue(target, out var into))
            {
                into.LeftOut.Add((file, reason));
                if (Links.TryGetValue(file, out var link))
                {
                    into.Links[file] = link;
                }
            }
        }

        return groups.Values.OrderBy(g => g.Archive, Py.Order).ToList();
    }

    private void Walk(string dir, string course, Dictionary<string, List<string>> videosByDir, List<string> material)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return;
        }

        // os.walk: a link to a folder counts as a folder (not walked into); everything else is a file.
        var files = new List<FileSystemInfo>();
        var dirs = new List<DirectoryInfo>();
        foreach (var e in entries)
        {
            if (e is DirectoryInfo d && (!IsLink(d) || Directory.Exists(d.FullName)))
            {
                dirs.Add(d);
            }
            else if (e is FileInfo || IsLink(e))
            {
                files.Add(e);
            }
        }

        foreach (var f in files.OrderBy(f => f.Name, Py.Order))
        {
            var path = f.FullName;
            if (IsLink(f))
            {
                Skipped.Add((path, "symbolic link"));
                continue;
            }

            if (ActiveContent.VideoExt.Contains(Py.Lower(Py.Suffix(f.Name))))
            {
                if (!videosByDir.TryGetValue(dir, out var list))
                {
                    list = [];
                    videosByDir[dir] = list;
                }

                list.Add(path);
                continue;
            }

            var reason = Excluded(path, course);
            if (reason is not null)
            {
                if (!_quietSkips.Contains(reason) && !_quietReasons.Contains(reason))
                {
                    Skipped.Add((path, reason));
                }

                continue;
            }

            material.Add(path);
        }

        foreach (var d in dirs.Where(d => !d.Name.StartsWith('.')).OrderBy(d => d.Name, Py.Order))
        {
            if (!IsLink(d))
            {
                Walk(d.FullName, course, videosByDir, material);
            }
        }
    }

    private string? Excluded(string path, string course)
    {
        var rel = Path.GetRelativePath(course, path).Replace('\\', '/');
        var parts = rel.Split('/');
        var name = parts[^1];
        if (parts.Any(p => p.StartsWith('.')))
        {
            return "hidden or Jellyfin data (dot) file";
        }

        if (parts[..^1].Any(p => _jellyfinDirs.Contains(Py.Lower(p)) || Py.Lower(p).EndsWith(".trickplay", StringComparison.Ordinal)))
        {
            return "Jellyfin artwork";
        }

        var ext = Py.Lower(Py.Suffix(name));
        if (ActiveContent.VideoExt.Contains(ext) || ActiveContent.SubtitleExt.Contains(ext) || ext == ".nfo")
        {
            return "video, subtitle or nfo";
        }

        if (_jellyfinImage.IsMatch(name))
        {
            return "Jellyfin artwork";
        }

        if (_skipRules.Count > 0)
        {
            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                size = 0;
            }

            byte[]? Read()
            {
                try
                {
                    using var f = File.OpenRead(path);
                    var buffer = new byte[Math.Min(1024 * 1024, Math.Max(0, f.Length))];
                    var n = f.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                    return n == buffer.Length ? buffer : buffer[..n];
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    return null;
                }
            }

            foreach (var rule in _skipRules)
            {
                if (rule.Matches(name, size, Read))
                {
                    if (rule.ShowLink && Rule.RedirectTarget(Read() ?? []) is { } link)
                    {
                        Links[path] = link;
                    }

                    return $"{rule.Reason} [rule {rule.Id}]";
                }
            }
        }

        if (IsTrigger(name))
        {
            return "existing Additional Material archive";
        }

        foreach (var pattern in _options.Excludes)
        {
            if (Py.FnMatch(name, pattern) || Py.FnMatch(rel, pattern))
            {
                return $"excluded by {pattern}";
            }
        }

        return null;
    }

    private string? ExecutableReason(string path)
    {
        var ext = Py.Lower(Py.Suffix(Path.GetFileName(path)));
        if (_options.AllowExecutables)
        {
            if (_options.AllowActiveDocuments)
            {
                return null;
            }

            return ext != ".zip" ? ActiveContent.CheckFile(path) : null;
        }

        if (ActiveContent.ExecutableExt.Contains(ext))
        {
            return $"{ext} files are executable or script content";
        }

        var inner = ActiveContent.ArchiveMembers(path);
        var bad = inner.Where(m => ActiveContent.ExecutableExt.Contains(Py.Lower(Py.Suffix(m.TrimEnd('/'))))).ToList();
        if (bad.Count > 0)
        {
            var shown = string.Join(", ", bad.Take(5)) + (bad.Count > 5 ? $" and {bad.Count - 5} more" : string.Empty);
            return $"the archive contains executable or script content ({shown})";
        }

        if (inner.Count > 0 && inner[0].StartsWith("(contents not checked", StringComparison.Ordinal))
        {
            return "it is an archive whose contents could not be checked for executable content";
        }

        if (!_options.AllowActiveDocuments)
        {
            var why = ActiveContent.CheckFile(path);
            if (why is not null)
            {
                return why;
            }

            if (ext == ".zip")
            {
                var docs = ActiveContent.ZipDocuments(path);
                if (docs.Count > 0)
                {
                    return "the archive holds documents with active content (" + string.Join("; ", docs.Take(3)) + (docs.Count > 3 ? " and more" : string.Empty) + ")";
                }
            }
        }

        return null;
    }

    private void Screen(string path)
    {
        var why = ExecutableReason(path);
        if (why is not null)
        {
            _blocked[path] = why;
        }
    }

    private (string Lesson, string How)? MatchLesson(string path, Dictionary<string, List<string>> videosByDir)
    {
        (string, string)? InFolder(string folder, string stem)
        {
            var videos = videosByDir.TryGetValue(folder, out var v) ? v : [];
            foreach (var video in videos)
            {
                if (Py.Lower(Py.Stem(Path.GetFileName(video))) == Py.Lower(stem))
                {
                    return (video, $"same name as the video {Path.GetFileName(video)}");
                }
            }

            if (_options.Match == "name")
            {
                return null;
            }

            var num = LessonNumber(stem);
            var hits = videos.Where(video => num is not null && LessonNumber(Py.Stem(Path.GetFileName(video))) == num).ToList();
            return hits.Count == 1 ? (hits[0], $"lesson number {num}, the only video {Path.GetFileName(hits[0])}") : null;
        }

        var name = Path.GetFileName(path);
        var parent = Path.GetDirectoryName(path)!;
        var hit = InFolder(parent, Py.Stem(name)) ?? InFolder(parent, name.Split('.')[0]);
        if (hit is not null)
        {
            return hit;
        }

        // attached_files/<lesson>/...: parts as pathlib gives them, the root first.
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var parts = new List<string> { root };
        parts.AddRange(path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
        for (var i = parts.Count - 3; i > 0; i--)
        {
            if (_attachmentDirs.Contains(Py.Lower(parts[i])))
            {
                var folder = Path.Combine([.. parts.Take(i)]);
                var found = InFolder(folder, parts[i + 1]);
                if (found is not null)
                {
                    var rule = _rules.FirstOrDefault(r => r.Action == "attachment-folder" && r.FolderNames.Contains(Py.Lower(parts[i])))?.Id ?? "?";
                    return (found.Value.Item1, $"in {parts[i]}/{parts[i + 1]}/ [rule {rule}], lesson {Path.GetFileName(found.Value.Item1)}");
                }
            }
        }

        return null;
    }

    private static string? LessonNumber(string name)
    {
        var m = _lessonNumber.Match(name);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool Under(string path, string dir)
        => path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal) || path == dir;

    private static bool IsLink(FileSystemInfo info) => info.LinkTarget is not null;

    private static List<string> Subdirs(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateDirectories()
                .Where(d => !d.Name.StartsWith('.') && Directory.Exists(d.FullName))
                .Select(d => d.FullName).OrderBy(d => d, Py.Order).ToList();
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool DirectVideos(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles().Any(f => ActiveContent.VideoExt.Contains(Py.Lower(Py.Suffix(f.Name))) && File.Exists(f.FullName));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasVideos(string folder)
    {
        if (DirectVideos(folder))
        {
            return true;
        }

        try
        {
            return new DirectoryInfo(folder).EnumerateDirectories()
                .Where(d => !d.Name.StartsWith('.') && d.LinkTarget is null)
                .Any(d => HasVideos(d.FullName));
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool LooksLikeCourse(string folder)
    {
        if (DirectVideos(folder))
        {
            return true;
        }

        var kids = Subdirs(folder);
        return kids.Any(DirectVideos) && !kids.Any(k => Subdirs(k).Any(DirectVideos));
    }
}

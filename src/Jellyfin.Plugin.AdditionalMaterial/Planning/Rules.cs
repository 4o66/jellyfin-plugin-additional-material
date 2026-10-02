using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace Jellyfin.Plugin.AdditionalMaterial.Planning;

/// <summary>A rule file is invalid; the message says which and why, as the script's would.</summary>
public sealed class RuleException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RuleException"/> class.</summary>
    /// <param name="message">What is wrong.</param>
    public RuleException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// One rule file (tools/rules/*.toml), the same files the helper script reads. Every condition
/// under <c>[match]</c> must hold. See CONTRIBUTING.md for the format.
/// </summary>
public sealed class Rule
{
    private static readonly HashSet<string> _ruleKeys = ["id", "description", "action", "reason", "quiet", "match", "test"];
    private static readonly HashSet<string> _matchKeys = ["names", "name_regex", "extensions", "max_size", "content_regex", "max_visible_text",
        "lines_regex", "max_lines", "allow_caption_lines", "caption_max_length", "folder_names"];
    private static readonly HashSet<string> _testKeys = ["name", "content", "folder", "expect"];
    private static readonly Regex _tags = new("<script.*?</script>|<style.*?</style>|<[^>]+>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex _space = new(@"[ \t\n\r\f\v]+", RegexOptions.CultureInvariant);

    /// <summary>Gets the rule's id (also its file name).</summary>
    public string Id { get; private init; } = string.Empty;

    /// <summary>Gets what the rule is for.</summary>
    public string Description { get; private init; } = string.Empty;

    /// <summary>Gets <c>skip</c> or <c>attachment-folder</c>.</summary>
    public string Action { get; private init; } = string.Empty;

    /// <summary>Gets the reason reported for files the rule skips.</summary>
    public string Reason { get; private init; } = string.Empty;

    /// <summary>Gets a value indicating whether skips are left out of reports.</summary>
    public bool Quiet { get; private init; }

    /// <summary>Gets the file the rule came from.</summary>
    public string Source { get; private init; } = string.Empty;

    /// <summary>Gets the lower-cased folder names of an attachment-folder rule.</summary>
    public IReadOnlySet<string> FolderNames { get; private init; } = new HashSet<string>();

    /// <summary>Gets the rule's own test cases.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Tests { get; private init; } = [];

    private List<string> Names { get; init; } = [];

    private Regex? NameRegex { get; init; }

    private HashSet<string>? Extensions { get; init; }

    private long? MaxSize { get; init; }

    private Regex? ContentRegex { get; init; }

    private long? MaxVisibleText { get; init; }

    private Regex? LinesRegex { get; init; }

    private long MaxLines { get; init; } = 40;

    private long AllowCaptionLines { get; init; }

    private long CaptionMaxLength { get; init; } = 60;

    /// <summary>Whether a skip rule matches a file.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="size">Its size.</param>
    /// <param name="read">Reads up to the first 1 MB of the file, or returns null.</param>
    /// <returns>Whether the rule matches.</returns>
    public bool Matches(string name, long size, Func<byte[]?> read)
    {
        if (Action != "skip")
        {
            return false;
        }

        var low = Py.Lower(name);
        if (Names.Count > 0 && !Names.Any(n => Py.FnMatch(low, Py.Lower(n))))
        {
            return false;
        }

        if (NameRegex is not null && !NameRegex.IsMatch(name))
        {
            return false;
        }

        if (Extensions is not null && !Extensions.Contains(Py.Lower(Py.Suffix(name))))
        {
            return false;
        }

        if (MaxSize is not null && size > MaxSize)
        {
            return false;
        }

        if (ContentRegex is null && MaxVisibleText is null && LinesRegex is null)
        {
            return true;
        }

        var data = read();
        if (data is null)
        {
            return false;
        }

        var bytes = Py.Bytes(data);
        if (ContentRegex is not null && !ContentRegex.IsMatch(bytes))
        {
            return false;
        }

        if (MaxVisibleText is not null && _space.Replace(_tags.Replace(bytes, " "), string.Empty).Length > MaxVisibleText)
        {
            return false;
        }

        if (LinesRegex is not null)
        {
            var lines = Py.SplitLines(Encoding.UTF8.GetString(data)).Where(l => Py.Strip(l).Length > 0).ToList();
            if (lines.Count == 0 || lines.Count > MaxLines)
            {
                return false;
            }

            var other = lines.Where(l => !LinesRegex.IsMatch(l)).ToList();
            if (other.Count > 0 && !(lines.Count >= 3 && other.Count <= AllowCaptionLines && other.All(o => Py.CodePoints(Py.Strip(o)) <= CaptionMaxLength)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Runs the rule's own <c>[[test]]</c> cases.</summary>
    /// <returns>The failures, if any.</returns>
    public List<string> RunTests()
    {
        var failures = new List<string>();
        foreach (var t in Tests)
        {
            bool got;
            if (Action == "attachment-folder")
            {
                got = FolderNames.Contains(Py.Lower(t.GetValueOrDefault("folder", string.Empty)));
            }
            else
            {
                var data = Encoding.UTF8.GetBytes(t.GetValueOrDefault("content", string.Empty));
                got = Matches(t.GetValueOrDefault("name", "file"), data.Length, () => data);
            }

            if (got != (t["expect"] == "match"))
            {
                failures.Add($"{Py.Leaf(Source)}: expected {t["expect"]} for '{t.GetValueOrDefault("name") ?? t.GetValueOrDefault("folder")}'");
            }
        }

        return failures;
    }

    /// <summary>Reads one rule file.</summary>
    /// <param name="fileName">Its file name (<c>&lt;id&gt;.toml</c>).</param>
    /// <param name="text">Its contents.</param>
    /// <returns>The rule.</returns>
    public static Rule Load(string fileName, string text)
    {
        TomlTable doc;
        try
        {
            doc = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
        }
        catch (Exception ex) when (ex is not RuleException)
        {
            throw new RuleException($"{fileName}: {ex.Message}");
        }

        var unknown = doc.Keys.Where(k => !_ruleKeys.Contains(k)).OrderBy(k => k, Py.Order).ToList();
        if (unknown.Count > 0)
        {
            throw new RuleException($"{fileName}: unknown key(s) {string.Join(", ", unknown)}");
        }

        var match = doc.TryGetValue("match", out var m) && m is TomlTable mt ? mt : new TomlTable();
        unknown = match.Keys.Where(k => !_matchKeys.Contains(k)).OrderBy(k => k, Py.Order).ToList();
        if (unknown.Count > 0)
        {
            throw new RuleException($"{fileName}: unknown [match] key(s) {string.Join(", ", unknown)}");
        }

        foreach (var k in new[] { "id", "description", "action" })
        {
            if (Get(doc, k) is not string s || s.Trim().Length == 0)
            {
                throw new RuleException($"{fileName}: '{k}' is required");
            }
        }

        var id = (string)doc["id"];
        if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]*$"))
        {
            throw new RuleException($"{fileName}: id must be lowercase letters, digits and dashes");
        }

        if (Py.Stem(fileName) != id)
        {
            throw new RuleException($"{fileName}: the file must be named after its id ({id}.toml)");
        }

        var action = (string)doc["action"];
        if (action is not ("skip" or "attachment-folder"))
        {
            throw new RuleException($"{fileName}: action must be one of attachment-folder, skip");
        }

        if (action == "skip" && !match.Keys.Any(k => k != "folder_names"))
        {
            throw new RuleException($"{fileName}: a skip rule needs at least one [match] condition");
        }

        if (action == "attachment-folder" && Strings(match, "folder_names").Count == 0)
        {
            throw new RuleException($"{fileName}: an attachment-folder rule needs [match] folder_names");
        }

        var tests = new List<IReadOnlyDictionary<string, string>>();
        if (doc.TryGetValue("test", out var tv) && tv is TomlTableArray testArray)
        {
            foreach (TomlTable t in testArray)
            {
                if (t.Keys.Any(k => !_testKeys.Contains(k)) || Get(t, "expect") is not ("match" or "no-match"))
                {
                    throw new RuleException($"{fileName}: each [[test]] takes content, expect, folder, name and expect = \"match\" or \"no-match\"");
                }

                tests.Add(t.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? string.Empty));
            }
        }

        return new Rule
        {
            Id = id,
            Description = (string)doc["description"],
            Action = action,
            Reason = Get(doc, "reason") as string ?? (string)doc["description"],
            Quiet = Get(doc, "quiet") is true,
            Source = fileName,
            Names = Strings(match, "names"),
            NameRegex = Get(match, "name_regex") is string nr ? Pattern(nr, fileName, "name_regex") : null,
            Extensions = match.ContainsKey("extensions") ? Strings(match, "extensions").Select(Py.Lower).ToHashSet(StringComparer.Ordinal) : null,
            MaxSize = Get(match, "max_size") as long?,
            ContentRegex = Get(match, "content_regex") is string cr ? Pattern(cr, fileName, "content_regex") : null,
            MaxVisibleText = Get(match, "max_visible_text") as long?,
            LinesRegex = Get(match, "lines_regex") is string lr ? Pattern(@"\A(?:" + lr + ")", fileName, "lines_regex") : null,
            MaxLines = Get(match, "max_lines") as long? ?? 40,
            AllowCaptionLines = Get(match, "allow_caption_lines") as long? ?? 0,
            CaptionMaxLength = Get(match, "caption_max_length") as long? ?? 60,
            FolderNames = Strings(match, "folder_names").Select(Py.Lower).ToHashSet(StringComparer.Ordinal),
            Tests = tests,
        };
    }

    /// <summary>Loads rule files, refusing duplicate ids, and leaves out disabled ones.</summary>
    /// <param name="files">File names and contents, in the order the script reads them (sorted by name per folder).</param>
    /// <param name="disabled">Ids to leave out.</param>
    /// <returns>The rules.</returns>
    public static List<Rule> LoadAll(IEnumerable<(string FileName, string Text)> files, IEnumerable<string>? disabled = null)
    {
        var rules = new List<Rule>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (fileName, text) in files)
        {
            var rule = Load(fileName, text);
            if (seen.TryGetValue(rule.Id, out var other))
            {
                throw new RuleException($"{fileName}: id '{rule.Id}' is already used by {other}");
            }

            seen[rule.Id] = fileName;
            rules.Add(rule);
        }

        var off = new HashSet<string>(disabled ?? [], StringComparer.Ordinal);
        var unknown = off.Where(d => !seen.ContainsKey(d)).OrderBy(d => d, Py.Order).ToList();
        if (unknown.Count > 0)
        {
            throw new RuleException($"no rule named {string.Join(", ", unknown)}");
        }

        return rules.Where(r => !off.Contains(r.Id)).ToList();
    }

    private static object? Get(TomlTable table, string key) => table.TryGetValue(key, out var value) ? value : null;

    private static List<string> Strings(TomlTable table, string key)
        => Get(table, key) is TomlArray a ? a.Select(x => x?.ToString() ?? string.Empty).ToList() : [];

    private static Regex Pattern(string value, string fileName, string key)
    {
        try
        {
            return Py.Compile(value, ignoreCase: true, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            throw new RuleException($"{fileName}: {key}: bad regular expression: {ex.Message}");
        }
    }
}

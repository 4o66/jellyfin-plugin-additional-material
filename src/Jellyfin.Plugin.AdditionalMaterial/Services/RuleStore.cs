using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.AdditionalMaterial.Configuration;
using Jellyfin.Plugin.AdditionalMaterial.Planning;
using Microsoft.Extensions.Logging;
using Tomlyn;
using Tomlyn.Model;

namespace Jellyfin.Plugin.AdditionalMaterial.Services;

/// <summary>One rule as the settings page lists it.</summary>
public sealed class RuleInfo
{
    /// <summary>Gets or sets the rule's id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets what the rule is for.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets <c>skip</c> or <c>attachment-folder</c>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the rule is on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the plugin ships a rule with this id.</summary>
    public bool BuiltIn { get; set; }

    /// <summary>Gets or sets a value indicating whether the administrator's text is used (an edited built-in rule, or their own).</summary>
    public bool Custom { get; set; }

    /// <summary>Gets or sets the text in use.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Gets or sets the built-in text, for an edited built-in rule (to compare or reset).</summary>
    public string? BuiltInText { get; set; }

    /// <summary>Gets or sets why the administrator's text cannot be used, if it cannot (the built-in rule, or nothing, is used instead).</summary>
    public string? Error { get; set; }
}

/// <summary>The result of checking a rule's text.</summary>
public sealed class RuleCheck
{
    /// <summary>Gets or sets a value indicating whether the rule can be saved.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets the rule's id, if it has a usable one.</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets what the rule is for.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the rule's action.</summary>
    public string? Action { get; set; }

    /// <summary>Gets or sets how many of the rule's own [[test]] cases ran.</summary>
    public int Tests { get; set; }

    /// <summary>Gets or sets what is wrong.</summary>
    public List<string> Errors { get; set; } = [];
}

/// <summary>
/// The rules in force: the built-in rule files (tools/rules), with the administrator's own rules
/// from the settings added, edited built-in rules replacing the originals, and turned-off rules
/// left out. Read again whenever the settings change.
/// </summary>
public sealed class RuleStore
{
    private readonly ILogger<RuleStore> _logger;
    private readonly object _lock = new();
    private (string Signature, List<Rule> Rules)? _current;

    /// <summary>Initializes a new instance of the <see cref="RuleStore"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public RuleStore(ILogger<RuleStore> logger)
    {
        _logger = logger;
    }

    /// <summary>The rules in force. A rule of the administrator's that cannot be read is left out (the built-in one is used instead) and logged.</summary>
    /// <returns>The rules.</returns>
    public List<Rule> Current()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var signature = Signature(config);
        lock (_lock)
        {
            if (_current is { } c && c.Signature == signature)
            {
                return c.Rules;
            }

            var (files, errors) = Effective(config);
            foreach (var (id, error) in errors)
            {
                _logger.LogError("Additional Material: rule {Id} from the settings cannot be used: {Error}", id, error);
            }

            var known = files.Select(f => Path.GetFileNameWithoutExtension(f.FileName)).ToHashSet(StringComparer.Ordinal);
            List<Rule> rules;
            try
            {
                rules = Rule.LoadAll(files, (config.DisabledRules ?? []).Where(known.Contains));
            }
            catch (RuleException ex)
            {
                _logger.LogError("Additional Material: the rules from the settings cannot be used, so only the built-in ones are: {Error}", ex.Message);
                rules = Rule.LoadAll(ArchiveWriter.BuiltInRules());
            }

            _current = (signature, rules);
            return rules;
        }
    }

    /// <summary>Every rule, built-in and the administrator's, for the settings page.</summary>
    /// <returns>The rules, by id.</returns>
    public static List<RuleInfo> List()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var disabled = new HashSet<string>(config.DisabledRules ?? [], StringComparer.Ordinal);
        var builtIn = ArchiveWriter.BuiltInRules().ToDictionary(f => Path.GetFileNameWithoutExtension(f.FileName), f => f.Text, StringComparer.Ordinal);
        var custom = (config.CustomRules ?? []).Where(r => !string.IsNullOrWhiteSpace(r.Id)).GroupBy(r => r.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Text, StringComparer.Ordinal);
        var list = new List<RuleInfo>();
        foreach (var id in builtIn.Keys.Union(custom.Keys).OrderBy(i => i, StringComparer.Ordinal))
        {
            var info = new RuleInfo { Id = id, Enabled = !disabled.Contains(id), BuiltIn = builtIn.ContainsKey(id), Custom = custom.ContainsKey(id) };
            info.Text = custom.GetValueOrDefault(id) ?? builtIn[id];
            info.BuiltInText = info.Custom ? builtIn.GetValueOrDefault(id) : null;
            try
            {
                var rule = Rule.Load(id + ".toml", info.Text);
                info.Description = rule.Description;
                info.Action = rule.Action;
            }
            catch (RuleException ex)
            {
                info.Error = ex.Message;
                if (builtIn.TryGetValue(id, out var original))
                {
                    var rule = Rule.Load(id + ".toml", original);
                    info.Description = rule.Description;
                    info.Action = rule.Action;
                }
            }

            list.Add(info);
        }

        return list;
    }

    /// <summary>Checks a rule's text the way loading it would, and runs its own test cases.</summary>
    /// <param name="text">The rule file's text.</param>
    /// <param name="originalId">The id it had when editing began, if it is an existing rule (it may keep that id).</param>
    /// <param name="otherCustomIds">The ids of the administrator's other rules, which a new rule may not reuse.</param>
    /// <returns>What was found.</returns>
    public static RuleCheck Check(string text, string? originalId, IEnumerable<string> otherCustomIds)
    {
        var result = new RuleCheck();
        string? id = null;
        try
        {
            var doc = TomlSerializer.Deserialize<TomlTable>(text ?? string.Empty);
            id = doc is not null && doc.TryGetValue("id", out var v) ? v as string : null;
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
            return result;
        }

        Rule rule;
        try
        {
            rule = Rule.Load((string.IsNullOrWhiteSpace(id) ? "rule" : id) + ".toml", text ?? string.Empty);
        }
        catch (RuleException ex)
        {
            result.Errors.Add(ex.Message);
            return result;
        }

        result.Id = rule.Id;
        result.Description = rule.Description;
        result.Action = rule.Action;
        result.Tests = rule.Tests.Count;
        if (rule.Id != originalId && otherCustomIds.Contains(rule.Id, StringComparer.Ordinal))
        {
            result.Errors.Add($"{rule.Id}.toml: another of your rules already has the id '{rule.Id}'");
        }

        result.Errors.AddRange(rule.RunTests());
        result.Ok = result.Errors.Count == 0;
        return result;
    }

    /// <summary>The rules that are on, as rule files in a zip: for the helper script's <c>--no-default-rules --rules DIR</c>.</summary>
    /// <returns>The zip's bytes.</returns>
    public byte[] Export()
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var disabled = new HashSet<string>(config.DisabledRules ?? [], StringComparer.Ordinal);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var (fileName, text) in Effective(config).Files.Where(f => !disabled.Contains(Path.GetFileNameWithoutExtension(f.FileName))))
            {
                using var w = new StreamWriter(zip.CreateEntry(fileName, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        }

        return stream.ToArray();
    }

    /// <summary>The rule files in force, in the order the script reads one folder of them, and the administrator's rules that could not be read.</summary>
    private static (List<(string FileName, string Text)> Files, List<(string Id, string Error)> Errors) Effective(PluginConfiguration config)
    {
        var files = ArchiveWriter.BuiltInRules().ToDictionary(f => f.FileName, f => f.Text, StringComparer.Ordinal);
        var errors = new List<(string, string)>();
        foreach (var custom in config.CustomRules ?? [])
        {
            var fileName = custom.Id + ".toml";
            try
            {
                Rule.Load(fileName, custom.Text ?? string.Empty);
                files[fileName] = custom.Text!;
            }
            catch (RuleException ex)
            {
                errors.Add((custom.Id, ex.Message));
            }
        }

        return (files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => (f.Key, f.Value)).ToList(), errors);
    }

    private static string Signature(PluginConfiguration config)
    {
        var text = new StringBuilder();
        foreach (var id in config.DisabledRules ?? [])
        {
            text.Append("-\0").Append(id).Append('\0');
        }

        foreach (var custom in config.CustomRules ?? [])
        {
            text.Append("+\0").Append(custom.Id).Append('\0').Append(custom.Text).Append('\0');
        }

        return text.ToString();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AdditionalMaterial.Planning;

// planner-compare <folder> --rules <dir> [--json <file>]     plan, as make_additional_material.py --json does
// planner-compare --self-test --rules <dir>                  run the rule files' own [[test]] cases
var argv = args.ToList();
string? Opt(string name)
{
    var i = argv.IndexOf(name);
    if (i < 0 || i + 1 >= argv.Count)
    {
        return null;
    }

    var value = argv[i + 1];
    argv.RemoveRange(i, 2);
    return value;
}

var rulesDir = Opt("--rules") ?? throw new ArgumentException("--rules DIR is required");
var jsonOut = Opt("--json");
var selfTest = argv.Remove("--self-test");
var rules = Rule.LoadAll(Directory.GetFiles(rulesDir, "*.toml").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
    .Select(f => (Path.GetFileName(f), File.ReadAllText(f))));
if (selfTest)
{
    var failures = rules.SelectMany(r => r.RunTests()).ToList();
    failures.ForEach(Console.WriteLine);
    Console.WriteLine($"{rules.Count} rules, {rules.Sum(r => r.Tests.Count)} tests, {failures.Count} failed");
    return failures.Count == 0 ? 0 : 1;
}

var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(argv.Single()));
var planner = new Planner(rules, new PlanOptions());
var courses = new JsonArray();
foreach (var course in Planner.CoursesIn(target))
{
    var groups = planner.PlanCourse(course);
    var why = planner.Packaged.GetValueOrDefault(course);
    var archives = new JsonArray();
    foreach (var g in groups)
    {
        var placement = new JsonObject();
        foreach (var f in g.Files)
        {
            placement[f] = g.Placement.GetValueOrDefault(f, string.Empty);
        }

        archives.Add(new JsonObject
        {
            ["level"] = g.Level,
            ["status"] = File.Exists(g.Archive) ? "kept" : "would write",
            ["archive"] = g.Archive,
            ["files"] = new JsonArray(g.Files.Where(f => !g.Removed.ContainsKey(f)).Select(f => (JsonNode)f).ToArray()),
            ["entries"] = new JsonArray(g.Files.Select(f => (JsonNode)g.EntryName(f)).ToArray()),
            ["placement"] = placement,
            ["removed"] = new JsonArray(g.Removed.Select(r => (JsonNode)new JsonObject { ["file"] = r.Key, ["reason"] = r.Value, ["scan"] = null }).ToArray()),
        });
    }

    courses.Add(new JsonObject { ["course"] = course, ["packaged_as_one"] = why is { Count: > 0 }, ["archives"] = archives });
}

var report = new JsonObject
{
    ["folder"] = target,
    ["courses"] = courses,
    ["skipped"] = new JsonArray(planner.Skipped.Select(s =>
    {
        var o = new JsonObject { ["file"] = s.File, ["reason"] = s.Reason };
        if (planner.Links.TryGetValue(s.File, out var link))
        {
            o["link"] = link;
        }

        return (JsonNode)o;
    }).ToArray()),
};
var text = report.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
if (jsonOut is null or "-")
{
    Console.WriteLine(text);
}
else
{
    File.WriteAllText(jsonOut, text + "\n");
}

Console.Error.WriteLine($"{courses.Count} course(s), {courses.Sum(c => c!["archives"]!.AsArray().Count)} archive(s)");
return 0;

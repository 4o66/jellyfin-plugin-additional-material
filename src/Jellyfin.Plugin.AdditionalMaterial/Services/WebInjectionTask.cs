using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.AdditionalMaterial.Services;

/// <summary>
/// At server start, asks the File Transformation plugin to add our script to the web client's
/// index.html. Without File Transformation the API still works; only the button is missing.
/// </summary>
public sealed class WebInjectionTask : IScheduledTask
{
    private const string TransformationId = "00897653-d5a8-4579-b885-c2d7136f50af";
    private readonly ILogger<WebInjectionTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="WebInjectionTask"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public WebInjectionTask(ILogger<WebInjectionTask> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Additional Material: add the web button";

    /// <inheritdoc />
    public string Key => "Jellyfin.Plugin.AdditionalMaterial.WebInjection";

    /// <inheritdoc />
    public string Description => "Registers the Additional Material button with the File Transformation plugin.";

    /// <inheritdoc />
    public string Category => "Startup Services";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var assembly = AssemblyLoadContext.All
            .SelectMany(c => c.Assemblies)
            .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
        var register = assembly?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")?.GetMethod("RegisterTransformation");
        if (register is null)
        {
            _logger.LogWarning("Additional Material: File Transformation plugin not found; the web button will not appear. Downloads still work through the API.");
            return Task.CompletedTask;
        }

        // Register exactly "index.html": File Transformation runs only one pattern's pipeline per file,
        // and an exact key is what other plugins (Plugin Pages, Home Screen Sections) use, so ours chains with theirs.
        var payload = new JObject
        {
            { "id", TransformationId },
            { "fileNamePattern", "index.html" },
            { "callbackAssembly", typeof(IndexHtmlPatch).Assembly.FullName },
            { "callbackClass", typeof(IndexHtmlPatch).FullName },
            { "callbackMethod", nameof(IndexHtmlPatch.Inject) },
        };
        register.Invoke(null, [payload]);
        _logger.LogInformation("Additional Material: web button registered with File Transformation");
        progress.Report(100);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return [new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger }];
    }
}

/// <summary>The index.html rewrite File Transformation calls back into.</summary>
public static class IndexHtmlPatch
{
    private const string Marker = "plugin=\"AdditionalMaterial\"";

    /// <summary>Adds the script tag before <c>&lt;/body&gt;</c>.</summary>
    /// <param name="payload">The file contents, as File Transformation passes them.</param>
    /// <returns>The new contents, or <c>null</c> to leave the file unchanged.</returns>
    public static string? Inject(PatchPayload payload)
    {
        var html = payload?.Contents;
        if (string.IsNullOrEmpty(html) || html.Contains(Marker, StringComparison.Ordinal))
        {
            return null;
        }

        var index = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        // The build's module ID changes with every build, so browsers never keep an old script
        // when the plugin is updated (the version number alone may stay the same between builds).
        var assembly = typeof(IndexHtmlPatch).Assembly;
        var version = (assembly.GetName().Version?.ToString() ?? "0") + "-" + assembly.ManifestModule.ModuleVersionId.ToString("N")[..8];
        // Relative to /web/, so it works under any base URL.
        var tag = $"<script {Marker} src=\"../AdditionalMaterial/web/additional-material.js?v={version}\" defer></script>";
        return html.Insert(index, tag);
    }
}

/// <summary>The object File Transformation hands to the callback.</summary>
public sealed class PatchPayload
{
    /// <summary>Gets or sets the file contents.</summary>
    public string? Contents { get; set; }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdditionalMaterial.Planning;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdditionalMaterial.Services;

/// <summary>A remembered VirusTotal answer.</summary>
public sealed class StoredScan
{
    /// <summary>Gets or sets the answer.</summary>
    public ScanResult Result { get; set; } = new();

    /// <summary>Gets or sets when it was asked.</summary>
    public DateTimeOffset CheckedUtc { get; set; }
}

/// <summary>
/// Looks blocked files up on VirusTotal by SHA-256, as the helper script does with a key: only the
/// fingerprint is sent, nothing is uploaded. Lookups run one at a time in the background, at the
/// configured rate, so planning never waits for them; answers are kept in the plugin's data folder,
/// and when one arrives <see cref="Checked"/> is raised so the file's course is planned again.
/// </summary>
public sealed class VirusTotal : IDisposable
{
    /// <summary>A file VirusTotal knows is asked about again after this long.</summary>
    public static readonly TimeSpan KnownFor = TimeSpan.FromDays(30);

    /// <summary>A file VirusTotal did not know is asked about again after this long.</summary>
    public static readonly TimeSpan UnknownFor = TimeSpan.FromDays(1);

    /// <summary>After a failed lookup, the file is not asked about again for this long.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    private readonly ILogger<VirusTotal> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly object _lock = new();
    private readonly Queue<(string Digest, string Path)> _queue = new();
    private readonly HashSet<string> _queued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _failed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (long Length, DateTime Modified, string Digest)> _digests = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private Dictionary<string, StoredScan>? _store;
    private bool _working;
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="VirusTotal"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public VirusTotal(ILogger<VirusTotal> logger)
    {
        _logger = logger;
    }

    /// <summary>Raised, with the file's path, when a lookup brings an answer that differs from the one before.</summary>
    public event Action<string>? Checked;

    /// <summary>Gets the API address; <c>AM_VT_BASE</c> replaces it (tests use a fake).</summary>
    public static string BaseUrl => (Environment.GetEnvironmentVariable("AM_VT_BASE") is { Length: > 0 } b ? b : "https://www.virustotal.com/api/v3").TrimEnd('/');

    /// <summary>Gets the remembered answers' file.</summary>
    public static string FilePath => Path.Combine(Plugin.Instance?.DataFolderPath ?? Path.GetTempPath(), "virustotal.json");

    private static string Key => Plugin.Instance?.Configuration.VirusTotalApiKey?.Trim() ?? string.Empty;

    /// <summary>
    /// What VirusTotal is known to say about a file, or <c>null</c> if it has not been asked yet or
    /// no key is set. Asks (in the background) when there is no answer or it is old.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The remembered answer.</returns>
    public ScanResult? Lookup(string path)
    {
        if (Key.Length == 0)
        {
            return null;
        }

        var digest = Digest(path);
        if (digest is null)
        {
            return null;
        }

        StoredScan? known;
        lock (_lock)
        {
            known = Store().GetValueOrDefault(digest);
        }

        var age = known is null ? TimeSpan.MaxValue : DateTimeOffset.UtcNow - known.CheckedUtc;
        if (age > (known?.Result.Status == "unknown" ? UnknownFor : KnownFor)
            && DateTimeOffset.UtcNow >= _pausedUntil
            && !(_failed.TryGetValue(digest, out var failedAt) && DateTimeOffset.UtcNow - failedAt < RetryAfter))
        {
            Enqueue(digest, path);
        }

        return known?.Result;
    }

    /// <summary>Checks a key by looking up a file every engine knows (the EICAR test file): any answer but an error means the key works.</summary>
    /// <param name="key">The key to try.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>Whether it works, and what VirusTotal said.</returns>
    public async Task<(bool Ok, string Message)> TestAsync(string key, CancellationToken cancellationToken)
    {
        const string Eicar = "275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f";
        var (code, body) = await RequestAsync(key, Eicar, cancellationToken).ConfigureAwait(false);
        return code is HttpStatusCode.OK or HttpStatusCode.NotFound ? (true, "OK") : (false, ErrorText(code, body));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        _http.Dispose();
    }

    private static string ErrorText(HttpStatusCode? code, JsonElement? body)
    {
        var message = body is { } b && b.ValueKind == JsonValueKind.Object && b.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
            && e.TryGetProperty("message", out var m) ? m.GetString() : null;
        return code is null ? message ?? "no answer" : $"HTTP {(int)code} {message}".Trim();
    }

    private static ScanResult Parse(string digest, HttpStatusCode code, JsonElement? body)
    {
        var link = "https://www.virustotal.com/gui/file/" + digest;
        if (code == HttpStatusCode.NotFound)
        {
            return new ScanResult { Status = "unknown", Link = link };
        }

        int malicious = 0, suspicious = 0, engines = 0;
        if (body is { } b && b.TryGetProperty("data", out var data) && data.TryGetProperty("attributes", out var attributes)
            && attributes.TryGetProperty("last_analysis_stats", out var stats) && stats.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in stats.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number))
            {
                var n = p.Value.GetInt32();
                engines += n;
                malicious += p.Name == "malicious" ? n : 0;
                suspicious += p.Name == "suspicious" ? n : 0;
            }
        }

        return new ScanResult
        {
            Status = malicious > 0 || suspicious > 0 ? "flagged" : engines > 0 ? "clean" : "unknown",
            Malicious = malicious,
            Suspicious = suspicious,
            Engines = engines,
            Link = link,
        };
    }

    private string? Digest(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (_digests.TryGetValue(path, out var d) && d.Length == info.Length && d.Modified == info.LastWriteTimeUtc)
            {
                return d.Digest;
            }

            using var stream = info.OpenRead();
            var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            _digests[path] = (info.Length, info.LastWriteTimeUtc, digest);
            return digest;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Enqueue(string digest, string path)
    {
        lock (_lock)
        {
            if (!_queued.Add(digest))
            {
                return;
            }

            _queue.Enqueue((digest, path));
            if (_working)
            {
                return;
            }

            _working = true;
        }

        _ = Task.Run(WorkAsync);
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            (string Digest, string Path) next;
            lock (_lock)
            {
                if (_queue.Count == 0 || _stopping.IsCancellationRequested)
                {
                    _working = false;
                    return;
                }

                next = _queue.Dequeue();
            }

            try
            {
                await CheckAsync(next.Digest, next.Path).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !_stopping.IsCancellationRequested)
            {
                _failed[next.Digest] = DateTimeOffset.UtcNow;
                _logger.LogWarning(ex, "Additional Material: VirusTotal lookup of {File} failed", next.Path);
            }
            finally
            {
                lock (_lock)
                {
                    _queued.Remove(next.Digest);
                }
            }
        }
    }

    private async Task CheckAsync(string digest, string path)
    {
        var key = Key;
        if (key.Length == 0)
        {
            return;
        }

        var perMinute = Math.Max(1, Plugin.Instance?.Configuration.VirusTotalRequestsPerMinute ?? 4);
        var wait = _last + TimeSpan.FromSeconds(60.0 / perMinute) - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, _stopping.Token).ConfigureAwait(false);
        }

        _last = DateTimeOffset.UtcNow;
        var (code, body) = await RequestAsync(key, digest, _stopping.Token).ConfigureAwait(false);
        if (code == HttpStatusCode.TooManyRequests)
        {
            // Out of quota (the free API allows 500 a day): stop asking for a while, and drop what
            // is queued; the next planning asks again.
            _pausedUntil = DateTimeOffset.UtcNow + RetryAfter;
            lock (_lock)
            {
                _queue.Clear();
                _queued.Clear();
            }

            _logger.LogWarning("Additional Material: VirusTotal quota used up; lookups resume after {Time}", _pausedUntil);
            return;
        }

        if (code is not (HttpStatusCode.OK or HttpStatusCode.NotFound))
        {
            _failed[digest] = DateTimeOffset.UtcNow;
            _logger.LogWarning("Additional Material: VirusTotal lookup of {File} failed: {Error}", path, ErrorText(code, body));
            return;
        }

        var result = Parse(digest, code.Value, body);
        _failed.TryRemove(digest, out _);
        bool changed;
        lock (_lock)
        {
            var store = Store();
            var before = store.GetValueOrDefault(digest)?.Result;
            changed = before is null || ScanResult.Describe(before) != ScanResult.Describe(result);
            store[digest] = new StoredScan { Result = result, CheckedUtc = DateTimeOffset.UtcNow };
            Save();
        }

        _logger.LogInformation("Additional Material: {File}: {Scan}", path, ScanResult.Describe(result));
        if (changed)
        {
            Checked?.Invoke(path);
        }
    }

    private async Task<(HttpStatusCode? Code, JsonElement? Body)> RequestAsync(string key, string digest, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/files/" + digest);
        request.Headers.Add("x-apikey", key);
        request.Headers.Add("accept", "application/json");
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonElement? body = null;
            try
            {
                body = string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text).RootElement.Clone();
            }
            catch (JsonException)
            {
            }

            return (response.StatusCode, body);
        }
        catch (HttpRequestException ex)
        {
            return (null, JsonDocument.Parse(JsonSerializer.Serialize(new { error = new { message = ex.Message } })).RootElement.Clone());
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, JsonDocument.Parse("{\"error\":{\"message\":\"timed out\"}}").RootElement.Clone());
        }
    }

    private Dictionary<string, StoredScan> Store()
    {
        if (_store is null)
        {
            try
            {
                _store = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<Dictionary<string, StoredScan>>(File.ReadAllText(FilePath)) ?? new()
                    : new();
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                _store = new();
            }

            _store = new Dictionary<string, StoredScan>(_store, StringComparer.Ordinal);
        }

        return _store;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_store));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Additional Material: could not save VirusTotal answers");
        }
    }
}

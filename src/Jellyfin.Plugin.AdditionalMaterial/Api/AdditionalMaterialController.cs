using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AdditionalMaterial.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AdditionalMaterial.Api;

/// <summary>Material lookup and download endpoints.</summary>
[ApiController]
[Route("AdditionalMaterial")]
public class AdditionalMaterialController : ControllerBase
{
    private const string UserIdClaim = "Jellyfin-UserId";
    private static readonly ConcurrentDictionary<string, (MaterialStatus? Status, DateTimeOffset Expires)> _statusCache = new();

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly MaterialLocator _locator;
    private readonly LinkSigner _signer;

    /// <summary>Initializes a new instance of the <see cref="AdditionalMaterialController"/> class.</summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="locator">Archive locator.</param>
    /// <param name="signer">Download token signer.</param>
    public AdditionalMaterialController(ILibraryManager libraryManager, IUserManager userManager, MaterialLocator locator, LinkSigner signer)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _locator = locator;
        _signer = signer;
    }

    /// <summary>Tells the caller whether an item has additional material.</summary>
    /// <param name="itemId">Item ID.</param>
    /// <returns>Availability, size and whether this user may download it.</returns>
    [HttpGet("Items/{itemId}")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<MaterialInfo> GetInfo([FromRoute] Guid itemId)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }

        var material = _locator.Find(item);
        var config = Plugin.Instance?.Configuration;
        var style = config?.ButtonStyle == "mono" ? "mono" : "color";
        var accent = config?.AccentColor is { } c && System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9A-Fa-f]{6}$") ? c : "#00A4DC";
        if (material is null)
        {
            return new MaterialInfo { ButtonStyle = style, AccentColor = accent };
        }

        return new MaterialInfo
        {
            ButtonStyle = style,
            AccentColor = accent,
            Available = true,
            FileName = material.FileName,
            Size = material.Size,
            Format = material.Format,
            CanDownload = CanDownload(user),
        };
    }

    /// <summary>
    /// The item's own material and, depending on the "show on parents" setting, the material of
    /// the sections and lessons below it, grouped by section. Only items the user can see appear.
    /// </summary>
    /// <param name="itemId">A series, season, episode or movie.</param>
    /// <returns>The listing.</returns>
    [HttpGet("Items/{itemId}/Tree")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<MaterialTree> GetTree([FromRoute] Guid itemId)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null)
        {
            return NotFound();
        }
        var tree = new MaterialTree { Name = item.Name, Type = item.GetBaseItemKind().ToString(), CanDownload = CanDownload(user), Self = Row(item, _locator.Find(item), "self") };
        foreach (var (row, season) in Below(item, user))
        {
            var key = season?.Id ?? Guid.Empty;
            var group = tree.Groups.FirstOrDefault(g => g.SeasonId == key);
            if (group is null)
            {
                group = new MaterialGroup { SeasonId = key, Name = season?.Name ?? string.Empty, IndexNumber = season?.IndexNumber };
                tree.Groups.Add(group);
            }

            group.Items.Add(row);
        }

        tree.Groups.Sort((a, b) => (a.IndexNumber ?? int.MaxValue).CompareTo(b.IndexNumber ?? int.MaxValue));
        foreach (var g in tree.Groups)
        {
            g.Items.Sort((a, b) => string.CompareOrdinal(a.Level, b.Level) != 0
                ? string.CompareOrdinal(b.Level, a.Level)   // "section" before "lesson"
                : (a.IndexNumber ?? int.MaxValue).CompareTo(b.IndexNumber ?? int.MaxValue));
        }

        return tree;
    }

    /// <summary>How the web script should draw the icon. Signed-in users only.</summary>
    /// <returns>The display settings.</returns>
    [HttpGet("web/settings")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<DisplaySettings> GetDisplaySettings()
    {
        var c = Plugin.Instance?.Configuration;
        return new DisplaySettings
        {
            ButtonStyle = c?.ButtonStyle == "mono" ? "mono" : "color",
            AccentColor = c?.AccentColor is { } a && System.Text.RegularExpressions.Regex.IsMatch(a, "^#[0-9A-Fa-f]{6}$") ? a : "#00A4DC",
            ShowOnParents = c?.ShowOnParents is "item" or "section" ? c.ShowOnParents : "all",
            ShowOnCards = c?.ShowOnCards ?? true,
            ShowInLists = c?.ShowInLists ?? true,
        };
    }

    /// <summary>For grids and lists: which of these items have material of their own or below them.</summary>
    /// <param name="request">Up to 200 item IDs.</param>
    /// <returns>Per item: whether it has its own archive and how many archives are below it.</returns>
    [HttpPost("Items/Status")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<Dictionary<string, MaterialStatus>> GetStatus([FromBody] StatusRequest request)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var result = new Dictionary<string, MaterialStatus>(StringComparer.Ordinal);
        var config = Plugin.Instance?.Configuration;
        if (config is not null && !config.ShowInLists && !config.ShowOnCards)
        {
            return result;
        }
        foreach (var id in (request?.Ids ?? Array.Empty<Guid>()).Distinct().Take(200))
        {
            var key = user.Id.ToString("N", CultureInfo.InvariantCulture) + id.ToString("N", CultureInfo.InvariantCulture);
            if (_statusCache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
            {
                if (cached.Status is not null)
                {
                    result[id.ToString("N", CultureInfo.InvariantCulture)] = cached.Status;
                }

                continue;
            }

            var item = _libraryManager.GetItemById<BaseItem>(id, user);
            MaterialStatus? status = null;
            if (item is not null)
            {
                var own = _locator.Find(item) is not null;
                var below = Below(item, user).Count();
                if (own || below > 0)
                {
                    status = new MaterialStatus { Own = own, Below = below };
                }
            }

            _statusCache[key] = (status, DateTimeOffset.UtcNow.AddMinutes(2));
            if (status is not null)
            {
                result[id.ToString("N", CultureInfo.InvariantCulture)] = status;
            }
        }

        if (_statusCache.Count > 20000)
        {
            _statusCache.Clear();
        }

        return result;
    }

    private IEnumerable<(MaterialRow Row, Season? Season)> Below(BaseItem item, User user)
    {
        var scope = Plugin.Instance?.Configuration.ShowOnParents ?? "all";
        BaseItemKind[] kinds;
        if (item is Series && scope == "all")
        {
            kinds = new[] { BaseItemKind.Season, BaseItemKind.Episode };
        }
        else if (item is Season && (scope == "all" || scope == "section"))
        {
            kinds = new[] { BaseItemKind.Episode };
        }
        else
        {
            yield break;
        }

        var query = new InternalItemsQuery(user)
        {
            AncestorIds = new[] { item.Id },
            IncludeItemTypes = kinds,
            Recursive = true,
            IsVirtualItem = false,
        };
        foreach (var child in _libraryManager.GetItemList(query))
        {
            var material = _locator.Find(child);
            if (material is null)
            {
                continue;
            }

            if (child is Season season)
            {
                yield return (Row(child, material, "section")!, season);
            }
            else if (child is Episode episode)
            {
                yield return (Row(child, material, "lesson")!, episode.Season);
            }
        }
    }

    private static MaterialRow? Row(BaseItem item, MaterialFile? material, string level) =>
        material is null ? null : new MaterialRow
        {
            ItemId = item.Id.ToString("N", CultureInfo.InvariantCulture),
            Name = item.Name,
            IndexNumber = item.IndexNumber,
            Level = level,
            FileName = material.FileName,
            Size = material.Size,
        };

    /// <summary>Issues a short-lived download link for an item's material.</summary>
    /// <param name="itemId">Item ID.</param>
    /// <returns>The token to use with <see cref="Download"/>.</returns>
    [HttpPost("Items/{itemId}/Link")]
    [Authorize]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<MaterialLink> CreateLink([FromRoute] Guid itemId)
    {
        var user = CurrentUser();
        if (user is null)
        {
            return Unauthorized();
        }

        var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        if (item is null || _locator.FindVerified(item) is null)
        {
            return NotFound();
        }

        if (!CanDownload(user))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        var minutes = Math.Clamp(Plugin.Instance?.Configuration.LinkLifetimeMinutes ?? 10, 1, 1440);
        var expires = DateTimeOffset.UtcNow.AddMinutes(minutes);
        return new MaterialLink { Token = _signer.Create(item.Id, user.Id, expires), ExpiresUtc = expires };
    }

    /// <summary>Downloads an item's material. Authorized by the token, then re-checked against the user's current access.</summary>
    /// <param name="token">Token from <see cref="CreateLink"/>.</param>
    /// <returns>The archive, always as an attachment.</returns>
    [HttpGet("Download/{token}")]
    [AllowAnonymous]
    public ActionResult Download([FromRoute] string token)
    {
        if (!_signer.TryValidate(token, out var itemId, out var userId))
        {
            return NotFound();
        }

        var user = _userManager.GetUserById(userId);
        if (user is null || user.HasPermission(PermissionKind.IsDisabled) || !CanDownload(user))
        {
            return NotFound();
        }

        var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        var material = item is null ? null : _locator.FindVerified(item);
        if (material is null)
        {
            return NotFound();
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "private, no-store";
        return PhysicalFile(material.FullPath, MaterialLocator.ContentTypeFor(material.Format), DownloadName(item!), enableRangeProcessing: true);
    }

    /// <summary>The web-client script that adds the button. Public, like the web client itself.</summary>
    /// <returns>JavaScript.</returns>
    [HttpGet("web/additional-material.js")]
    [AllowAnonymous]
    public ActionResult Script()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var stream = assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".Web.additional-material.js");
        if (stream is null)
        {
            return NotFound();
        }

        // Always revalidate: the address already changes per build, and an old copy must never linger.
        Response.Headers["Cache-Control"] = "no-cache";
        return File(stream, "application/javascript; charset=utf-8");
    }

    /// <summary>
    /// The web script's and settings page's text in the requested language, with English for
    /// anything a translation lacks. Translations are Web/i18n/&lt;language&gt;.json.
    /// </summary>
    /// <param name="lang">A language tag such as <c>de-DE</c>; tries <c>de-DE</c>, then <c>de</c>, then <c>en</c>.</param>
    /// <returns>A key-to-text map.</returns>
    [HttpGet("web/strings")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<Dictionary<string, string>> Strings([FromQuery] string? lang)
    {
        var result = ReadStrings("en") ?? new Dictionary<string, string>();
        var tag = (lang ?? string.Empty).Trim().Replace('_', '-');
        var candidates = new List<string>();
        if (tag.Length > 0 && tag.Length <= 20 && System.Text.RegularExpressions.Regex.IsMatch(tag, "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$"))
        {
            var dash = tag.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0)
            {
                candidates.Add(tag[..dash].ToLowerInvariant());
            }

            candidates.Add(tag.ToLowerInvariant());
        }

        foreach (var candidate in candidates)
        {
            var strings = ReadStrings(candidate);
            if (strings is not null)
            {
                foreach (var pair in strings)
                {
                    result[pair.Key] = pair.Value;
                }
            }
        }

        Response.Headers["Cache-Control"] = "public, max-age=3600";
        return result;
    }

    private static Dictionary<string, string>? ReadStrings(string language)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(".Web.i18n." + language + ".json", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return null;
        }

        using var stream = assembly.GetManifestResourceStream(name);
        return stream is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
    }

    /// <summary>
    /// A file name that says what the archive is, e.g.
    /// "Course - S10E01 - Lesson title - Additional Material.zip".
    /// </summary>
    internal static string DownloadName(BaseItem item)
    {
        string name = item switch
        {
            Episode e => string.Join(" - ", new[]
            {
                e.SeriesName,
                e.ParentIndexNumber is int s && e.IndexNumber is int n
                    ? string.Format(CultureInfo.InvariantCulture, "S{0:00}E{1:00}", s, n)
                    : null,
                e.Name,
            }.Where(x => !string.IsNullOrWhiteSpace(x))),
            Season s => string.Join(" - ", new[]
            {
                s.SeriesName,
                s.IndexNumber is int n && !s.Name.StartsWith(n.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    ? n.ToString(CultureInfo.InvariantCulture) + " " + s.Name
                    : s.Name,
            }.Where(x => !string.IsNullOrWhiteSpace(x))),
            _ => item.Name,
        };
        var cleaned = new string((name + " - Additional Material").Select(c => Array.IndexOf(BadFileNameChars, c) >= 0 || char.IsControl(c) ? ' ' : c).ToArray());
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim().TrimEnd('.');
        if (cleaned.Length > 180)
        {
            cleaned = cleaned[..180].TrimEnd();
        }

        return cleaned + ".zip";
    }

    // Characters Windows, macOS and Linux file systems refuse, plus the path separators.
    private static readonly char[] BadFileNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private static bool CanDownload(User user) =>
        !(Plugin.Instance?.Configuration.RequireDownloadPermission ?? true)
        || user.HasPermission(PermissionKind.EnableContentDownloading);

    private User? CurrentUser()
    {
        var value = User.FindFirst(UserIdClaim)?.Value;
        return Guid.TryParse(value, CultureInfo.InvariantCulture, out var id) ? _userManager.GetUserById(id) : null;
    }
}

/// <summary>Whether an item has material.</summary>
public sealed class MaterialInfo
{
    /// <summary>Gets or sets a value indicating whether an archive exists.</summary>
    public bool Available { get; set; }

    /// <summary>Gets or sets the archive's file name.</summary>
    public string? FileName { get; set; }

    /// <summary>Gets or sets the size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Gets or sets the archive format (<c>zip</c>).</summary>
    public string? Format { get; set; }

    /// <summary>Gets or sets a value indicating whether this user may download it.</summary>
    public bool CanDownload { get; set; }

    /// <summary>Gets or sets the button style, <c>mono</c> or <c>color</c>.</summary>
    public string ButtonStyle { get; set; } = "mono";

    /// <summary>Gets or sets the badge color for the <c>color</c> style.</summary>
    public string AccentColor { get; set; } = "#00A4DC";
}

/// <summary>A download token.</summary>
public sealed class MaterialLink
{
    /// <summary>Gets or sets the token for <c>/AdditionalMaterial/Download/{token}</c>.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets when the token expires.</summary>
    public DateTimeOffset ExpiresUtc { get; set; }
}

/// <summary>Item IDs to look up.</summary>
public sealed class StatusRequest
{
    /// <summary>Gets or sets the item IDs.</summary>
    public Guid[] Ids { get; set; } = Array.Empty<Guid>();
}

/// <summary>Whether an item in a grid or list has material.</summary>
public sealed class MaterialStatus
{
    /// <summary>Gets or sets a value indicating whether the item has its own archive.</summary>
    public bool Own { get; set; }

    /// <summary>Gets or sets how many archives are below it (sections and lessons).</summary>
    public int Below { get; set; }
}

/// <summary>An item's material and the material below it.</summary>
public sealed class MaterialTree
{
    /// <summary>Gets or sets the item's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's type (Series, Season, Episode, Movie…).</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's own archive, if any.</summary>
    public MaterialRow? Self { get; set; }

    /// <summary>Gets the archives below the item, grouped by section.</summary>
    public List<MaterialGroup> Groups { get; } = new();

    /// <summary>Gets or sets a value indicating whether this user may download.</summary>
    public bool CanDownload { get; set; }
}

/// <summary>One section's archives.</summary>
public sealed class MaterialGroup
{
    /// <summary>Gets or sets the season ID (empty for items not in a season).</summary>
    public Guid SeasonId { get; set; }

    /// <summary>Gets or sets the section name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the season number.</summary>
    public int? IndexNumber { get; set; }

    /// <summary>Gets the archives.</summary>
    public List<MaterialRow> Items { get; } = new();
}

/// <summary>One archive in a listing.</summary>
public sealed class MaterialRow
{
    /// <summary>Gets or sets the item the archive belongs to.</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode or season number.</summary>
    public int? IndexNumber { get; set; }

    /// <summary>Gets or sets <c>self</c>, <c>section</c> or <c>lesson</c>.</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Gets or sets the archive's file name.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets its size in bytes.</summary>
    public long Size { get; set; }
}

/// <summary>Display settings for the web script.</summary>
public sealed class DisplaySettings
{
    /// <summary>Gets or sets <c>mono</c> or <c>color</c>.</summary>
    public string ButtonStyle { get; set; } = "color";

    /// <summary>Gets or sets the accent color.</summary>
    public string AccentColor { get; set; } = "#00A4DC";

    /// <summary>Gets or sets <c>item</c>, <c>section</c> or <c>all</c>.</summary>
    public string ShowOnParents { get; set; } = "all";

    /// <summary>Gets or sets a value indicating whether grid cards show the icon.</summary>
    public bool ShowOnCards { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether list rows show the icon.</summary>
    public bool ShowInLists { get; set; } = true;
}

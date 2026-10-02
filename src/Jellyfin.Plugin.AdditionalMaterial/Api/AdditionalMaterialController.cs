using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AdditionalMaterial.Services;
using MediaBrowser.Controller.Entities;
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
        if (material is null)
        {
            return new MaterialInfo();
        }

        return new MaterialInfo
        {
            Available = true,
            FileName = material.FileName,
            Size = material.Size,
            Format = material.Format,
            CanDownload = CanDownload(user),
        };
    }

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
        if (item is null || _locator.Find(item) is null)
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
        var material = item is null ? null : _locator.Find(item);
        if (material is null)
        {
            return NotFound();
        }

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Cache-Control"] = "private, no-store";
        return PhysicalFile(material.FullPath, MaterialLocator.ContentTypeFor(material.Format), material.FileName, enableRangeProcessing: true);
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

        Response.Headers["Cache-Control"] = "public, max-age=3600";
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
}

/// <summary>A download token.</summary>
public sealed class MaterialLink
{
    /// <summary>Gets or sets the token for <c>/AdditionalMaterial/Download/{token}</c>.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets when the token expires.</summary>
    public DateTimeOffset ExpiresUtc { get; set; }
}

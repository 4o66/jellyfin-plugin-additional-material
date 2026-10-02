using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdditionalMaterial.Services;

/// <summary>
/// Issues and checks short-lived download tokens, so the browser can download through a plain
/// link without putting the user's Jellyfin access token in a URL (where logs and proxies keep it).
/// A token names one item and one user; the download re-checks that user's access every time.
/// </summary>
public sealed class LinkSigner
{
    private const int MacLength = 16;
    private const int PayloadLength = 16 + 16 + 8;
    private readonly Lazy<byte[]> _key;
    private readonly ILogger<LinkSigner> _logger;

    /// <summary>Initializes a new instance of the <see cref="LinkSigner"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public LinkSigner(ILogger<LinkSigner> logger)
    {
        _logger = logger;
        _key = new Lazy<byte[]>(LoadOrCreateKey);
    }

    /// <summary>Creates a token.</summary>
    /// <param name="itemId">The item whose material is downloaded.</param>
    /// <param name="userId">The user the token is issued to.</param>
    /// <param name="expires">When the token stops working.</param>
    /// <returns>A URL-safe token.</returns>
    public string Create(Guid itemId, Guid userId, DateTimeOffset expires)
    {
        var buffer = new byte[PayloadLength + MacLength];
        itemId.TryWriteBytes(buffer.AsSpan(0, 16));
        userId.TryWriteBytes(buffer.AsSpan(16, 16));
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(32, 8), expires.ToUnixTimeSeconds());
        Mac(buffer.AsSpan(0, PayloadLength)).CopyTo(buffer.AsSpan(PayloadLength));
        return WebEncoders.Base64UrlEncode(buffer);
    }

    /// <summary>Checks a token's signature and expiry.</summary>
    /// <param name="token">The token from the URL.</param>
    /// <param name="itemId">The item it names.</param>
    /// <param name="userId">The user it was issued to.</param>
    /// <returns><c>true</c> if the token is genuine and unexpired.</returns>
    public bool TryValidate(string token, out Guid itemId, out Guid userId)
    {
        itemId = Guid.Empty;
        userId = Guid.Empty;
        byte[] buffer;
        try
        {
            buffer = WebEncoders.Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return false;
        }

        // Only the canonical spelling is accepted: base64url leaves unused bits in the last
        // character, so several strings can decode to the same bytes.
        if (buffer.Length != PayloadLength + MacLength || !string.Equals(WebEncoders.Base64UrlEncode(buffer), token, StringComparison.Ordinal))
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(Mac(buffer.AsSpan(0, PayloadLength)), buffer.AsSpan(PayloadLength)))
        {
            return false;
        }

        var expires = BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(32, 8));
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires)
        {
            return false;
        }

        itemId = new Guid(buffer.AsSpan(0, 16));
        userId = new Guid(buffer.AsSpan(16, 16));
        return true;
    }

    private byte[] Mac(ReadOnlySpan<byte> payload)
    {
        var full = HMACSHA256.HashData(_key.Value, payload);
        return full.AsSpan(0, MacLength).ToArray();
    }

    private byte[] LoadOrCreateKey()
    {
        var folder = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("Additional Material plugin is not initialized.");
        var path = Path.Combine(folder, "signing.key");
        try
        {
            Directory.CreateDirectory(folder);
            if (File.Exists(path))
            {
                var existing = File.ReadAllBytes(path);
                if (existing.Length == 32 && IsPrivate(path))
                {
                    return existing;
                }

                // Wrong size, or readable by others (it may already have been read): replace it.
                // Links issued with the old key stop working; they only live for minutes anyway.
                _logger.LogWarning("Additional Material: replacing {Path}: it was readable by other accounts or malformed", path);
                File.Delete(path);
            }

            var key = RandomNumberGenerator.GetBytes(32);
            using (var stream = CreatePrivate(path))
            {
                stream.Write(key);
            }

            return key;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SystemException)
        {
            // E.g. the service now runs as an account that cannot read the old key, or the file
            // system has no ACLs. Download links still work, until the next restart.
            _logger.LogError(ex, "Additional Material: cannot read or write {Path}; using a temporary key until restart", path);
            return RandomNumberGenerator.GetBytes(32);
        }
    }

    /// <summary>Creates the file readable and writable by this account only (plus SYSTEM and Administrators on Windows).</summary>
    private static FileStream CreatePrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Set at creation, not afterwards, so there is no moment with the folder's inherited ACL
            // (C:\ProgramData gives BUILTIN\Users read access).
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in PrivateSids())
            {
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            }

            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.ReadData, FileShare.None, 4096, FileOptions.None, security);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        return new FileStream(path, options);
    }

    /// <summary>Whether only this account (plus SYSTEM and Administrators on Windows) can open the file.</summary>
    private static bool IsPrivate(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(path).GetAccessControl();
            if (!security.AreAccessRulesProtected)
            {
                return false;
            }

            var allowed = PrivateSids();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow && !(rule.IdentityReference is SecurityIdentifier sid && allowed.Contains(sid)))
                {
                    return false;
                }
            }

            return true;
        }

        var others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & others) == 0;
    }

    [SupportedOSPlatform("windows")]
    private static List<SecurityIdentifier> PrivateSids()
    {
        var sids = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        var self = WindowsIdentity.GetCurrent().User;
        if (self is not null && !sids.Contains(self))
        {
            sids.Add(self);
        }

        return sids;
    }
}

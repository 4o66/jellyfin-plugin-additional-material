using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

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

    /// <summary>Initializes a new instance of the <see cref="LinkSigner"/> class.</summary>
    public LinkSigner()
    {
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

        if (buffer.Length != PayloadLength + MacLength)
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

    private static byte[] LoadOrCreateKey()
    {
        var folder = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("Additional Material plugin is not initialized.");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "signing.key");
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length == 32)
            {
                return existing;
            }
        }

        var key = RandomNumberGenerator.GetBytes(32);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(path, options))
        {
            stream.Write(key);
        }

        return key;
    }
}

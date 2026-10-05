using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AdditionalMaterial.Planning;

/// <summary>One file in a stored (uncompressed) zip: its name, exact size and date, and how to read it.</summary>
/// <param name="Name">The entry name, with <c>/</c> separators.</param>
/// <param name="Size">Its exact size in bytes; the source must yield exactly this many.</param>
/// <param name="Modified">Its date.</param>
/// <param name="Open">Opens its content.</param>
/// <param name="Identity">What identifies the content (path, size, date, or a hash), for telling bundles apart.</param>
public sealed record StoredEntry(string Name, long Size, DateTime Modified, Func<Stream> Open, string Identity);

/// <summary>
/// Writes a zip whose entries are stored, not compressed, in one pass to a stream that need not
/// seek, and knows its exact length before writing a byte: CRCs go in data descriptors after each
/// entry. Zip64 is used where a size, offset or count needs it.
/// </summary>
public static class StoredZip
{
    private const uint Max32 = 0xFFFFFFFF;
    private const ushort Flags = (1 << 3) | (1 << 11);   // sizes and CRC in a data descriptor; UTF-8 names
    private static readonly uint[] _crcTable = MakeCrcTable();

    /// <summary>The exact length of the zip <see cref="WriteAsync"/> writes for these entries.</summary>
    /// <param name="entries">The entries.</param>
    /// <returns>The length in bytes.</returns>
    public static long Length(IReadOnlyList<StoredEntry> entries)
    {
        long offset = 0, central = 0;
        foreach (var e in entries)
        {
            var name = Encoding.UTF8.GetByteCount(e.Name);
            var big = e.Size >= Max32;
            var farOffset = offset >= Max32;
            offset += 30 + name + (big ? 20 : 0) + e.Size + (big ? 24 : 16);
            central += 46 + name + CentralExtraLength(big, farOffset);
        }

        return offset + central + EndLength(entries.Count, offset, central);
    }

    /// <summary>Writes the zip. Throws if a source yields a different number of bytes than its size.</summary>
    /// <param name="entries">The entries, in order.</param>
    /// <param name="output">Where to write.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(IReadOnlyList<StoredEntry> entries, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        var crcs = new uint[entries.Count];
        var offsets = new long[entries.Count];
        long offset = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var name = Encoding.UTF8.GetBytes(e.Name);
            var big = e.Size >= Max32;
            var (time, date) = DosTime(e.Modified);
            offsets[i] = offset;

            var head = new BinaryBuffer();
            head.U32(0x04034b50).U16((ushort)(big ? 45 : 20)).U16(Flags).U16(0).U16(time).U16(date)
                .U32(0).U32(big ? Max32 : 0).U32(big ? Max32 : 0).U16((ushort)name.Length).U16((ushort)(big ? 20 : 0)).Bytes(name);
            if (big)
            {
                head.U16(0x0001).U16(16).U64(0).U64(0);
            }

            await head.WriteToAsync(output, cancellationToken).ConfigureAwait(false);
            offset += head.Length;

            var crc = Max32;
            long copied = 0;
            using (var source = e.Open())
            {
                int n;
                while ((n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (copied + n > e.Size)
                    {
                        throw new InvalidDataException($"{e.Name} is larger than {e.Size} bytes now");
                    }

                    crc = Crc(crc, buffer.AsSpan(0, n));
                    await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    copied += n;
                }
            }

            if (copied != e.Size)
            {
                throw new InvalidDataException($"{e.Name} is {copied} bytes now, not {e.Size}");
            }

            crcs[i] = ~crc;
            offset += copied;
            var descriptor = new BinaryBuffer().U32(0x08074b50).U32(crcs[i]);
            if (big)
            {
                descriptor.U64((ulong)e.Size).U64((ulong)e.Size);
            }
            else
            {
                descriptor.U32((uint)e.Size).U32((uint)e.Size);
            }

            await descriptor.WriteToAsync(output, cancellationToken).ConfigureAwait(false);
            offset += descriptor.Length;
        }

        var centralStart = offset;
        var dir = new BinaryBuffer();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var name = Encoding.UTF8.GetBytes(e.Name);
            var big = e.Size >= Max32;
            var farOffset = offsets[i] >= Max32;
            var (time, date) = DosTime(e.Modified);
            var extra = CentralExtraLength(big, farOffset);
            dir.U32(0x02014b50).U16((ushort)((3 << 8) | 45)).U16((ushort)(big || farOffset ? 45 : 20)).U16(Flags).U16(0).U16(time).U16(date)
                .U32(crcs[i]).U32(big ? Max32 : (uint)e.Size).U32(big ? Max32 : (uint)e.Size)
                .U16((ushort)name.Length).U16((ushort)extra).U16(0).U16(0).U16(0).U32(0x81A40000)   // unix mode 0644, regular file
                .U32(farOffset ? Max32 : (uint)offsets[i]).Bytes(name);
            if (extra > 0)
            {
                dir.U16(0x0001).U16((ushort)(extra - 4));
                if (big)
                {
                    dir.U64((ulong)e.Size).U64((ulong)e.Size);
                }

                if (farOffset)
                {
                    dir.U64((ulong)offsets[i]);
                }
            }

            if (dir.Length > 1024 * 1024)
            {
                offset += dir.Length;
                await dir.WriteToAsync(output, cancellationToken).ConfigureAwait(false);
                dir = new BinaryBuffer();
            }
        }

        offset += dir.Length;
        var centralSize = offset - centralStart;
        var zip64 = NeedsZip64End(entries.Count, centralStart, centralSize);
        if (zip64)
        {
            dir.U32(0x06064b50).U64(44).U16(45).U16(45).U32(0).U32(0).U64((ulong)entries.Count).U64((ulong)entries.Count)
                .U64((ulong)centralSize).U64((ulong)centralStart);
            dir.U32(0x07064b50).U32(0).U64((ulong)offset).U32(1);
        }

        var count = (ushort)Math.Min(entries.Count, 0xFFFF);
        dir.U32(0x06054b50).U16(0).U16(0).U16(zip64 ? (ushort)0xFFFF : count).U16(zip64 ? (ushort)0xFFFF : count)
            .U32(zip64 ? Max32 : (uint)centralSize).U32(zip64 ? Max32 : (uint)centralStart).U16(0);
        await dir.WriteToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static int CentralExtraLength(bool big, bool farOffset) => big || farOffset ? 4 + (big ? 16 : 0) + (farOffset ? 8 : 0) : 0;

    private static bool NeedsZip64End(int count, long centralStart, long centralSize) => count >= 0xFFFF || centralStart >= Max32 || centralSize >= Max32;

    private static long EndLength(int count, long centralStart, long centralSize) => 22 + (NeedsZip64End(count, centralStart, centralSize) ? 56 + 20 : 0);

    private static (ushort Time, ushort Date) DosTime(DateTime value)
    {
        var t = value.Kind == DateTimeKind.Utc ? value.ToLocalTime() : value;
        if (t.Year < 1980)
        {
            t = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        }
        else if (t.Year > 2107)
        {
            t = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Local);
        }

        return ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)), (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    /// <summary>Little-endian bytes for zip records.</summary>
    private sealed class BinaryBuffer
    {
        private readonly MemoryStream _stream = new();

        public long Length => _stream.Length;

        public BinaryBuffer U16(ushort v)
        {
            _stream.WriteByte((byte)v);
            _stream.WriteByte((byte)(v >> 8));
            return this;
        }

        public BinaryBuffer U32(uint v) => U16((ushort)v).U16((ushort)(v >> 16));

        public BinaryBuffer U64(ulong v) => U32((uint)v).U32((uint)(v >> 32));

        public BinaryBuffer Bytes(byte[] b)
        {
            _stream.Write(b);
            return this;
        }

        public Task WriteToAsync(Stream output, CancellationToken cancellationToken)
            => output.WriteAsync(_stream.GetBuffer().AsMemory(0, (int)_stream.Length), cancellationToken).AsTask();
    }
}

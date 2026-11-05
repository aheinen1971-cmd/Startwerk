using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace Startwerk;

// SharpCompress 1.0.0 decodes XZ but does not verify the checks on decoded blocks.
sealed class XzChecks
{
    static readonly ulong[] Crc32 = MakeTable(0xEDB88320);
    static readonly ulong[] Crc64 = MakeTable(0xC96C5795D7870F42);
    readonly List<Block> blocks = [];
    readonly int checkSize;
    public long Size { get; private set; }
    public long StreamEnd { get; }

    readonly record struct Block(long Size, long CheckOffset);

    XzChecks(int checkSize, long streamEnd)
    {
        this.checkSize = checkSize;
        StreamEnd = streamEnd;
    }

    public static XzChecks Read(Stream source, long limit)
    {
        Span<byte> header = stackalloc byte[12];
        source.ReadExactly(header);
        if (!header[..6].SequenceEqual(new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0 })
            || header[6] != 0 || !ValidCrc32(header[6..8], header[8..]))
            throw Invalid();
        int checkSize = header[7] switch
        {
            1 => 4,
            4 => 8,
            10 => 32,
            _ => throw new IOException("XZ muss eine CRC32-, CRC64- oder SHA-256-Prüfsumme enthalten.")
        };

        long end = source.Length;
        Span<byte> footer = stackalloc byte[12];
        // XZ permits zero padding after the footer in groups of four bytes.
        Span<byte> tail = stackalloc byte[4096];
        while (end >= 24)
        {
            int length = (int)Math.Min(end - header.Length, tail.Length);
            source.Position = end - length;
            source.ReadExactly(tail[..length]);
            int last = tail[..length].LastIndexOfAnyExcept((byte)0);
            if (last >= 0)
            {
                int padding = length - last - 1;
                if (padding % 4 != 0) throw Invalid();
                end -= padding;
                break;
            }
            end -= length;
        }
        if (end < 32) throw Invalid();
        source.Position = end - footer.Length;
        source.ReadExactly(footer);
        if (!footer[10..].SequenceEqual("YZ"u8) || !footer[8..10].SequenceEqual(header[6..8])
            || !ValidCrc32(footer[4..10], footer[..4]))
            throw Invalid();

        long indexSize = (BinaryPrimitives.ReadUInt32LittleEndian(footer[4..8]) + 1L) * 4;
        long indexStart = end - footer.Length - indexSize;
        if (indexStart < header.Length || indexSize < 8 || indexSize > 16 * 1024 * 1024) throw Invalid();
        byte[] index = new byte[(int)indexSize];
        source.Position = indexStart;
        source.ReadExactly(index);
        ReadOnlySpan<byte> records = index.AsSpan(0, index.Length - 4);
        if (records[0] != 0 || !ValidCrc32(records, index.AsSpan(index.Length - 4))) throw Invalid();

        var checks = new XzChecks(checkSize, end);
        int position = 1;
        long count = ReadVli(records, ref position);
        if (count <= 0 || count > (records.Length - position) / 2) throw Invalid();
        long offset = header.Length;
        Span<byte> blockHeader = stackalloc byte[1024];
        for (long i = 0; i < count; i++)
        {
            long unpadded = ReadVli(records, ref position);
            long size = ReadVli(records, ref position);
            if (unpadded < 9 + checkSize || unpadded > indexStart - offset) throw Invalid();
            long next = offset + ((unpadded + 3) & ~3L);
            if (next > indexStart) throw Invalid();
            if (size > limit - checks.Size)
                throw new IOException("Das entpackte Abbild überschreitet den freien Platz oder 64 GiB.");
            ValidateBlock(source, offset, next, unpadded, size, checkSize, blockHeader);
            checks.blocks.Add(new Block(size, next - checkSize));
            checks.Size += size;
            offset = next;
        }
        if (offset != indexStart || records.Length - position > 3 || records[position..].ContainsAnyExcept((byte)0))
            throw Invalid();
        source.Position = 0;
        return checks;
    }

    static void ValidateBlock(Stream source, long offset, long next, long unpadded, long size, int checkSize, Span<byte> scratch)
    {
        source.Position = offset;
        int sizeByte = source.ReadByte();
        int headerSize = (sizeByte + 1) * 4;
        if (headerSize < 8 || headerSize >= unpadded - checkSize) throw Invalid();
        Span<byte> header = scratch[..headerSize];
        header[0] = (byte)sizeByte;
        source.ReadExactly(header[1..]);
        ReadOnlySpan<byte> fields = header[..^4];
        int flags = fields[1];
        if ((flags & 0x3C) != 0 || !ValidCrc32(fields, header[^4..])) throw Invalid();
        int position = 2;
        if ((flags & 0x40) != 0 && ReadVli(fields, ref position) != unpadded - headerSize - checkSize)
            throw Invalid();
        if ((flags & 0x80) != 0 && ReadVli(fields, ref position) != size) throw Invalid();
        int lastFilter = flags & 3;
        for (int i = 0; i <= lastFilter; i++)
        {
            long id = ReadVli(fields, ref position);
            long length = ReadVli(fields, ref position);
            if (length > fields.Length - position) throw Invalid();
            if (i == lastFilter && (id != 0x21 || length != 1 || fields[position] > 30))
                throw new IOException("Dieses XZ-Abbild benötigt einen unbekannten Filter oder mehr als 128 MiB Wörterbuchspeicher.");
            position += (int)length;
        }
        if (fields[position..].ContainsAnyExcept((byte)0)) throw Invalid();
        int padding = (int)(next - offset - unpadded);
        source.Position = next - checkSize - padding;
        source.ReadExactly(scratch[..padding]);
        if (scratch[..padding].ContainsAnyExcept((byte)0)) throw Invalid();
    }

    public void Verify(Stream source, Stream image, byte[] buffer)
    {
        if (image.Length != Size) throw Invalid();
        image.Position = 0;
        Span<byte> expected = stackalloc byte[32];
        Span<byte> actual = stackalloc byte[32];
        using var sha = checkSize == 32 ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        ulong[] table = checkSize == 4 ? Crc32 : Crc64;
        ulong mask = checkSize == 4 ? uint.MaxValue : ulong.MaxValue;
        foreach (var block in blocks)
        {
            ulong crc = mask;
            long remaining = block.Size;
            while (remaining > 0)
            {
                int count = (int)Math.Min(remaining, buffer.Length);
                image.ReadExactly(buffer.AsSpan(0, count));
                if (sha is null) crc = UpdateCrc(crc, buffer.AsSpan(0, count), table);
                else sha.AppendData(buffer, 0, count);
                remaining -= count;
            }
            if (sha is null) BinaryPrimitives.WriteUInt64LittleEndian(actual, crc ^ mask);
            else sha.GetHashAndReset(actual);
            source.Position = block.CheckOffset;
            source.ReadExactly(expected[..checkSize]);
            if (!actual[..checkSize].SequenceEqual(expected[..checkSize]))
                throw new IOException("Die Block-Prüfsumme des XZ-Abbilds stimmt nicht.");
        }
    }

    static long ReadVli(ReadOnlySpan<byte> bytes, ref int position)
    {
        long value = 0;
        for (int shift = 0; shift < 63 && position < bytes.Length; shift += 7)
        {
            byte part = bytes[position++];
            value |= (long)(part & 0x7F) << shift;
            if ((part & 0x80) == 0)
            {
                if (part == 0 && shift != 0) throw Invalid();
                return value;
            }
        }
        throw Invalid();
    }

    static bool ValidCrc32(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected) =>
        (UpdateCrc(uint.MaxValue, data, Crc32) ^ uint.MaxValue) == BinaryPrimitives.ReadUInt32LittleEndian(expected);

    static ulong UpdateCrc(ulong crc, ReadOnlySpan<byte> data, ulong[] table)
    {
        foreach (byte value in data) crc = table[(byte)(crc ^ value)] ^ (crc >> 8);
        return crc;
    }

    static ulong[] MakeTable(ulong polynomial)
    {
        var table = new ulong[256];
        for (int i = 0; i < table.Length; i++)
        {
            ulong crc = (ulong)i;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : polynomial);
            table[i] = crc;
        }
        return table;
    }

    static IOException Invalid() => new("Das XZ-Abbild ist beschädigt oder enthält mehrere Datenströme.");
}

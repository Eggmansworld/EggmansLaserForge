using System.Buffers.Binary;

namespace Ldp.Engine;

/// <summary>
/// Reader/writer for Hypseus Singe VLDP ".dat" frame index files.
///
/// Layout (matching the C struct written by fwrite in vldp_internal.cpp,
/// including alignment padding):
///   byte  0     version        (3)
///   byte  1     finished       (1 = parse completed)
///   byte  2     uses_fields
///   bytes 3-7   struct padding (zeros)
///   bytes 8-15  uint64 LE      length of the .m2v file
/// followed by one int64 LE per coded picture: the byte offset of the
/// picture start code for I-frames, or -1 for anything else.
/// </summary>
public static class VldpDat
{
    public const byte DatVersion = 3;
    private const int HeaderSize = 16;

    public static string PathFor(string m2vPath) => Path.ChangeExtension(m2vPath, ".dat");

    public static void Write(FrameIndex index, string datPath)
    {
        using FileStream fs = new(datPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        header[0] = DatVersion;
        header[1] = 1; // finished
        header[2] = (byte)(index.UsesFields ? 1 : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)index.FileLength);
        fs.Write(header);

        Span<byte> entry = stackalloc byte[8];
        foreach (long offset in index.DatEntries)
        {
            BinaryPrimitives.WriteInt64LittleEndian(entry, offset);
            fs.Write(entry);
        }
    }

    /// <summary>
    /// Reads an existing .dat and returns its entry table, or null when the
    /// file is missing/stale (wrong version, unfinished parse, or length
    /// mismatch) — the same conditions under which VLDP regenerates it.
    /// </summary>
    public static (bool UsesFields, long[] Entries)? TryRead(string datPath, long m2vLength)
    {
        if (!File.Exists(datPath)) return null;

        using FileStream fs = new(datPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[HeaderSize];
        if (fs.Read(header) != HeaderSize) return null;

        byte version = header[0];
        byte finished = header[1];
        bool usesFields = header[2] != 0;
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
        if (version != DatVersion || finished != 1 || length != (ulong)m2vLength) return null;

        long entryCount = (fs.Length - HeaderSize) / 8;
        long[] entries = new long[entryCount];
        using BinaryReader reader = new(fs);
        for (long i = 0; i < entryCount; i++) entries[i] = reader.ReadInt64();
        return (usesFields, entries);
    }
}

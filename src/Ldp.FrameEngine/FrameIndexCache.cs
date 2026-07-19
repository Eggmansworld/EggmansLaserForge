using System.Buffers.Binary;
using System.Text;

namespace Ldp.Engine;

/// <summary>
/// Binary sidecar cache for a <see cref="FrameIndex"/> (".ldpidx" next to the
/// .m2v) so reopening a large video skips the full-stream rescan. Invalidated
/// by video file length mismatch or format version bump.
/// </summary>
public static class FrameIndexCache
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LDPX");
    private const int Version = 1;

    public static string PathFor(string m2vPath) => m2vPath + ".ldpidx";

    public static void Write(FrameIndex index)
    {
        string cachePath = PathFor(index.M2vPath);
        using FileStream fs = new(cachePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using BinaryWriter w = new(fs);

        w.Write(Magic);
        w.Write(Version);
        w.Write(index.FileLength);
        w.Write(index.UsesFields);
        w.Write(index.FrameRateCode);
        w.Write(index.Fps);
        w.Write(index.Width);
        w.Write(index.Height);
        w.Write(index.GopCount);
        w.Write(index.SequenceHeaderCache.Length);
        w.Write(index.SequenceHeaderCache);

        w.Write(index.CodedPictureCount);
        foreach (long v in index.DatEntries) w.Write(v);
        foreach (long v in index.PacketStarts) w.Write(v);
        foreach (ushort v in index.TemporalReferences) w.Write(v);
    }

    public static FrameIndex? TryRead(string m2vPath)
    {
        string cachePath = PathFor(m2vPath);
        if (!File.Exists(cachePath) || !File.Exists(m2vPath)) return null;

        try
        {
            using FileStream fs = new(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using BinaryReader r = new(fs);

            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) return null;
            if (r.ReadInt32() != Version) return null;

            long fileLength = r.ReadInt64();
            if (fileLength != new FileInfo(m2vPath).Length) return null; // stale

            bool usesFields = r.ReadBoolean();
            byte frameRateCode = r.ReadByte();
            double fps = r.ReadDouble();
            int width = r.ReadInt32();
            int height = r.ReadInt32();
            int gopCount = r.ReadInt32();
            byte[] seqCache = r.ReadBytes(r.ReadInt32());

            int count = r.ReadInt32();
            long[] datEntries = new long[count];
            for (int i = 0; i < count; i++) datEntries[i] = r.ReadInt64();
            long[] packetStarts = new long[count];
            for (int i = 0; i < count; i++) packetStarts[i] = r.ReadInt64();
            ushort[] temporalRefs = new ushort[count];
            for (int i = 0; i < count; i++) temporalRefs[i] = r.ReadUInt16();

            return new FrameIndex
            {
                M2vPath = m2vPath,
                FileLength = fileLength,
                UsesFields = usesFields,
                DatEntries = datEntries,
                PacketStarts = packetStarts,
                TemporalReferences = temporalRefs,
                SequenceHeaderCache = seqCache,
                Fps = fps,
                FrameRateCode = frameRateCode,
                Width = width,
                Height = height,
                GopCount = gopCount,
            };
        }
        catch (IOException) { return null; } // includes EndOfStreamException (truncated cache)
    }
}

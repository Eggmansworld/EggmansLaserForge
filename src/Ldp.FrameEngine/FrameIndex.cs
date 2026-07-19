namespace Ldp.Engine;

/// <summary>
/// In-memory index of an MPEG-2 elementary video stream (M2V).
///
/// The core of this index is the same coded-order picture table that
/// Daphne/Hypseus VLDP stores in its ".dat" files: one entry per coded
/// picture, holding the byte offset of the picture start code for
/// I-frames and -1 for P/B frames. Frame numbers everywhere in this
/// engine are indexes into that table (0-based), which is exactly the
/// frame numbering the emulator uses for discSearch().
/// </summary>
public sealed class FrameIndex
{
    public required string M2vPath { get; init; }
    public required long FileLength { get; init; }

    /// <summary>True when the stream is field-coded (two field pictures per frame).</summary>
    public required bool UsesFields { get; init; }

    /// <summary>Per coded picture: byte offset of the I-frame picture start code, or -1.</summary>
    public required long[] DatEntries { get; init; }

    /// <summary>
    /// Per coded picture: byte offset where this picture's packet begins,
    /// including any sequence/GOP header chain immediately preceding the
    /// picture start code. Used to packetize the stream for the decoder.
    /// </summary>
    public required long[] PacketStarts { get; init; }

    /// <summary>Per coded picture: 10-bit temporal_reference from the picture header.</summary>
    public required ushort[] TemporalReferences { get; init; }

    /// <summary>
    /// Bytes from the start of the file up to (not including) the first GOP
    /// start code. VLDP feeds these to the decoder before any seek target,
    /// and we do the same.
    /// </summary>
    public required byte[] SequenceHeaderCache { get; init; }

    public required double Fps { get; init; }
    public required byte FrameRateCode { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int GopCount { get; init; }

    public int CodedPictureCount => DatEntries.Length;

    /// <summary>Number of displayable frames (field pairs count as one frame).</summary>
    public int FrameCount => UsesFields ? CodedPictureCount / 2 : CodedPictureCount;
}

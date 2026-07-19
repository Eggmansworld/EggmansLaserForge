namespace Ldp.Engine;

/// <summary>
/// Single-pass scanner for MPEG-2 elementary streams.
///
/// This is a faithful port of the state machine in hypseus-singe's
/// src/vldp/mpegscan.cpp so that the picture table we build (and the
/// .dat files we write) are semantically identical to what the emulator
/// generates on first run. Any deviation from that state machine risks
/// frame numbers that disagree with Hypseus, so cleverness is deliberately
/// avoided here.
/// </summary>
public static class M2vScanner
{
    private const int InNothing = 0;
    private const int InPic = 1;
    private const int InPicExt = 2;

    public static FrameIndex Scan(string m2vPath, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using FileStream fs = new(m2vPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                  1 << 20, FileOptions.SequentialScan);
        long fileLength = fs.Length;

        List<long> datEntries = new(1 << 17);
        List<long> packetStarts = new(1 << 17);
        List<ushort> temporalRefs = new(1 << 17);

        int state = InNothing;
        int relPos = 0;
        uint picHeaderBits = 0;
        byte extType = 0;
        bool fieldsDetected = false;
        bool framesDetected = false;
        long pendingChainStart = -1; // start of seq/GOP header chain preceding the next picture
        long curPicStart = -1;
        long firstSeqStart = -1;
        long firstGopStart = -1;
        int gopCount = 0;

        // The three bytes preceding the current one; a start code is found
        // when they equal 00 00 01. mpegscan tracks the same window (its
        // "last three"), and the header position it records is the offset
        // of the oldest of those bytes, i.e. current position - 3.
        byte p1 = 0, p2 = 0, p3 = 0;

        byte[] buf = new byte[1 << 20];
        long pos = 0;
        long lastReport = 0;

        while (true)
        {
            int read = fs.Read(buf, 0, buf.Length);
            if (read == 0) break;

            for (int i = 0; i < read; i++, pos++)
            {
                byte ch = buf[i];

                if (state == InPic)
                {
                    // The two bytes after a picture start code hold
                    // temporal_reference (10 bits) and picture_coding_type (3 bits).
                    if (relPos == 0)
                    {
                        picHeaderBits = (uint)(ch << 8);
                    }
                    else if (relPos == 1)
                    {
                        picHeaderBits |= ch;
                        temporalRefs.Add((ushort)(picHeaderBits >> 6));
                        // mpegscan masks with 3 (not 7); match it exactly. 1 = I frame.
                        uint codingType = (picHeaderBits >> 3) & 3;
                        datEntries.Add(codingType == 1 ? curPicStart : -1L);
                        state = InNothing;
                    }
                    relPos++;
                }
                else if (state == InPicExt)
                {
                    if (relPos == 0)
                    {
                        extType = (byte)(ch >> 4);
                    }
                    else if (relPos >= 2)
                    {
                        // picture_coding_extension: picture_structure is the low
                        // 2 bits of this byte. 1/2 = field picture, 3 = frame picture.
                        if (relPos == 2 && extType == 8)
                        {
                            int structure = ch & 3;
                            if (structure is 1 or 2) fieldsDetected = true;
                            else if (structure == 3) framesDetected = true;
                        }
                        state = InNothing;
                    }
                    relPos++;
                }
                else if (p3 == 0 && p2 == 0 && p1 == 1)
                {
                    long headerPos = pos - 3;
                    switch (ch)
                    {
                        case 0x00: // picture start code
                            curPicStart = headerPos;
                            packetStarts.Add(pendingChainStart >= 0 ? pendingChainStart : headerPos);
                            pendingChainStart = -1;
                            relPos = 0;
                            state = InPic;
                            break;
                        case 0xB3: // sequence header
                            if (firstSeqStart < 0) firstSeqStart = headerPos;
                            if (pendingChainStart < 0) pendingChainStart = headerPos;
                            break;
                        case 0xB5: // extension header
                            relPos = 0;
                            state = InPicExt;
                            break;
                        case 0xB8: // group of pictures
                            if (firstGopStart < 0) firstGopStart = headerPos;
                            if (pendingChainStart < 0) pendingChainStart = headerPos;
                            gopCount++;
                            break;
                    }
                }

                p3 = p2;
                p2 = p1;
                p1 = ch;
            }

            if (progress != null && pos - lastReport >= (16 << 20))
            {
                lastReport = pos;
                progress.Report((double)pos / fileLength);
            }
            ct.ThrowIfCancellationRequested();
        }

        if (firstSeqStart < 0 || datEntries.Count == 0)
            throw new InvalidDataException(
                $"'{Path.GetFileName(m2vPath)}' does not look like an MPEG video elementary stream " +
                "(no sequence header / pictures found). Is it demultiplexed?");
        if (fieldsDetected && framesDetected)
            throw new InvalidDataException(
                $"'{Path.GetFileName(m2vPath)}' mixes field and frame pictures; VLDP treats this as an error.");
        if (firstGopStart < 0)
            throw new InvalidDataException(
                $"'{Path.GetFileName(m2vPath)}' contains no GOP headers; VLDP cannot seek in such a stream.");

        bool usesFields = fieldsDetected && !framesDetected;

        // Sequence header parameters (width/height/frame rate) live in the
        // 8 bytes after the first sequence start code.
        Span<byte> seq = stackalloc byte[8];
        fs.Position = firstSeqStart + 4;
        fs.ReadExactly(seq);
        int width = (seq[0] << 4) | (seq[1] >> 4);
        int height = ((seq[1] & 0xF) << 8) | seq[2];
        byte frameRateCode = (byte)(seq[3] & 0xF);

        // VLDP caches everything before the first GOP header and replays it
        // to the decoder ahead of every seek; keep the identical span.
        byte[] seqCache = new byte[firstGopStart];
        fs.Position = 0;
        fs.ReadExactly(seqCache);

        progress?.Report(1.0);

        return new FrameIndex
        {
            M2vPath = m2vPath,
            FileLength = fileLength,
            UsesFields = usesFields,
            DatEntries = datEntries.ToArray(),
            PacketStarts = packetStarts.ToArray(),
            TemporalReferences = temporalRefs.ToArray(),
            SequenceHeaderCache = seqCache,
            Fps = FpsFromCode(frameRateCode),
            FrameRateCode = frameRateCode,
            Width = width,
            Height = height,
            GopCount = gopCount,
        };
    }

    public static double FpsFromCode(byte code) => code switch
    {
        1 => 24000.0 / 1001.0,
        2 => 24.0,
        3 => 25.0,
        4 => 30000.0 / 1001.0,
        5 => 30.0,
        6 => 50.0,
        7 => 60000.0 / 1001.0,
        8 => 60.0,
        _ => 30000.0 / 1001.0, // out-of-spec; NTSC is the least-wrong guess
    };
}

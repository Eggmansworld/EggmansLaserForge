using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Swscales;
using Sdcb.FFmpeg.Utils;

namespace Ldp.Engine;

/// <summary>A decoded frame as tightly-packed 32-bit BGRA pixels.</summary>
public sealed record FrameImage(int FrameNumber, int Width, int Height, int Stride, byte[] Bgra);

/// <summary>
/// Frame-exact MPEG-2 decoder driven by a <see cref="FrameIndex"/>.
///
/// Seeks replicate VLDP: resolve the target through <see cref="VldpSeekPolicy"/>,
/// feed the decoder the cached sequence header followed by the stream from the
/// anchor I-frame's picture start code, then count decoded outputs — after
/// discarding FramesToSkip frames, the next output IS the target frame,
/// exactly as the emulator would display it.
///
/// Packets are cut on picture boundaries using the offsets recorded during
/// the scan, so no bitstream parser sits between us and the decoder.
/// </summary>
public sealed class FrameDecoder : IDisposable
{
    /// <summary>Max frames to decode past the current position before a re-seek is cheaper.</summary>
    private const int SequentialWindow = 90;

    private readonly FrameIndex _idx;
    private readonly FileStream _fs;
    private readonly CodecContext _ctx;
    private readonly Frame _decoded = new();
    private readonly Packet _packet = new();
    private readonly VideoFrameConverter _converter = new();
    private Frame? _bgraFrame;

    private int _feedNext = -1;   // next coded picture index to send to the decoder
    private int _nextOutput = -1; // frame number of the decoder's next output; -1 = must seek
    private bool _draining;

    public FrameDecoder(FrameIndex index)
    {
        if (index.UsesFields)
            throw new NotSupportedException(
                "Field-coded MPEG-2 streams are not supported yet. " +
                "(All known Singe game videos are frame-coded.)");

        _idx = index;
        _fs = new FileStream(index.M2vPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             1 << 20, FileOptions.RandomAccess);

        Codec codec = Codec.FindDecoderById(AVCodecID.Mpeg2video);
        _ctx = new CodecContext(codec);
        // libmpeg2 (what Hypseus uses) emits every decoded picture, including
        // not-fully-valid leading B-frames right after a seek. Our output
        // counting must line up with that, so ask FFmpeg to do the same.
        _ctx.Flags |= AV_CODEC_FLAG.OutputCorrupt;
        _ctx.Flags2 |= (int)AV_CODEC_FLAG2.ShowAll;
        _ctx.Open(codec);
    }

    /// <summary>
    /// Decodes and returns the given frame (0-based coded index). Every frame
    /// that gets converted on the way (the skip window after a seek) is also
    /// handed to <paramref name="onFrame"/> so callers can cache it.
    /// </summary>
    public FrameImage DecodeFrame(int target, Action<FrameImage>? onFrame = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(target);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(target, _idx.CodedPictureCount);

        bool canContinue = _nextOutput >= 0
                           && target >= _nextOutput
                           && target - _nextOutput <= SequentialWindow;
        if (!canContinue) Seek(target);

        while (true)
        {
            CodecResult result = _ctx.ReceiveFrame(_decoded);
            switch (result)
            {
                case CodecResult.Success:
                    int frameNumber = _nextOutput++;
                    if (frameNumber < target - SequentialWindow) continue; // don't convert deep-skip frames
                    FrameImage image = ConvertCurrent(frameNumber);
                    onFrame?.Invoke(image);
                    if (frameNumber == target) return image;
                    continue;

                case CodecResult.Again:
                    FeedNextPacket();
                    continue;

                case CodecResult.EOF:
                default:
                    // The stream delivered fewer frames than the index promised.
                    int nextExpected = _nextOutput;
                    _nextOutput = -1;
                    throw new EndOfStreamException(
                        $"Decoder reached end of stream before frame {target} " +
                        $"(next expected output was {nextExpected}).");
            }
        }
    }

    private void Seek(int target)
    {
        VldpSeekPolicy.SeekPlan plan = VldpSeekPolicy.Resolve(_idx.DatEntries, target);

        unsafe
        {
            ffmpeg.avcodec_flush_buffers(this._ctx);
        }
        _draining = false;

        // First packet after a seek: sequence header cache + the anchor
        // I-frame picture, exactly the byte sequence VLDP feeds libmpeg2.
        long anchorEnd = PacketEnd(plan.AnchorPicture);
        int pictureLength = checked((int)(anchorEnd - plan.AnchorOffset));
        byte[] first = new byte[_idx.SequenceHeaderCache.Length + pictureLength];
        _idx.SequenceHeaderCache.CopyTo(first, 0);
        ReadRange(plan.AnchorOffset, first.AsSpan(_idx.SequenceHeaderCache.Length, pictureLength));
        SendPacket(first);

        _feedNext = plan.AnchorPicture + 1;
        _nextOutput = target - plan.FramesToSkip;
    }

    private void FeedNextPacket()
    {
        if (_feedNext >= _idx.CodedPictureCount)
        {
            if (!_draining)
            {
                _draining = true;
                _packet.Unref();
                _ctx.SendPacket(_packet); // empty packet: begin drain
            }
            return;
        }

        long start = _idx.PacketStarts[_feedNext];
        long end = PacketEnd(_feedNext);
        _feedNext++;

        byte[] data = new byte[checked((int)(end - start))];
        ReadRange(start, data);
        SendPacket(data);
    }

    private long PacketEnd(int picture) =>
        picture + 1 < _idx.CodedPictureCount ? _idx.PacketStarts[picture + 1] : _idx.FileLength;

    private void SendPacket(byte[] data)
    {
        // av_new_packet gives an FFmpeg-owned, padded, refcounted buffer;
        // never hand the decoder managed memory it might try to free.
        _packet.Unref();
        unsafe
        {
            int ret = ffmpeg.av_new_packet(_packet, data.Length);
            if (ret < 0) throw new OutOfMemoryException($"av_new_packet failed ({ret})");
        }
        data.CopyTo(_packet.Data.AsSpan());
        try
        {
            _ctx.SendPacket(_packet);
        }
        finally
        {
            _packet.Unref();
        }
    }

    private void ReadRange(long offset, Span<byte> destination)
    {
        _fs.Position = offset;
        _fs.ReadExactly(destination);
    }

    private unsafe FrameImage ConvertCurrent(int frameNumber)
    {
        int width = _decoded.Width;
        int height = _decoded.Height;

        if (_bgraFrame == null || _bgraFrame.Width != width || _bgraFrame.Height != height)
        {
            _bgraFrame?.Dispose();
            _bgraFrame = Frame.CreateVideo(width, height, AVPixelFormat.Bgra);
        }

        _converter.ConvertFrame(_decoded, _bgraFrame, SWS.Point);

        int srcStride = _bgraFrame.Linesize[0];
        int dstStride = width * 4;
        byte[] pixels = new byte[dstStride * height];
        byte* src = (byte*)_bgraFrame.Data[0];
        fixed (byte* dst = pixels)
        {
            for (int y = 0; y < height; y++)
                Buffer.MemoryCopy(src + (long)y * srcStride, dst + (long)y * dstStride, dstStride, dstStride);
        }

        return new FrameImage(frameNumber, width, height, dstStride, pixels);
    }

    public void Dispose()
    {
        _bgraFrame?.Dispose();
        _converter.Dispose();
        _decoded.Dispose();
        _packet.Dispose();
        _ctx.Dispose();
        _fs.Dispose();
    }
}

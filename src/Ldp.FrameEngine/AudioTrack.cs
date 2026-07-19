using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Common;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Utils;

namespace Ldp.Engine;

/// <summary>
/// Streaming PCM reader for a video's companion audio file (OGG Vorbis in
/// practice). Decodes on demand into 16-bit interleaved PCM, seekable by
/// time, thread-safe between an audio output thread calling Read and a UI
/// thread calling Seek. Past end of stream it delivers silence so a playback
/// clock driven by delivered bytes keeps advancing.
/// </summary>
public sealed class AudioTrack : IDisposable
{
    private readonly object _lock = new();
    private readonly FormatContext _format;
    private readonly CodecContext _codec;
    private readonly MediaStream _stream;
    private readonly Frame _decoded = new();
    private readonly Packet _packet = new();

    private byte[] _fifo = new byte[1 << 19];
    private int _fifoStart;
    private int _fifoCount;
    private bool _endOfStream;

    public int SampleRate { get; }
    public int Channels { get; }
    public int BytesPerSecond => SampleRate * Channels * 2;

    private AudioTrack(FormatContext format, MediaStream stream, CodecContext codec)
    {
        _format = format;
        _stream = stream;
        _codec = codec;
        SampleRate = codec.SampleRate;
        Channels = Math.Clamp(codec.ChLayout.nb_channels, 1, 2);
    }

    /// <summary>Opens the companion audio for a video, or null when absent/unreadable.</summary>
    public static AudioTrack? TryOpenFor(string m2vPath)
    {
        string oggPath = Path.ChangeExtension(m2vPath, ".ogg");
        if (!File.Exists(oggPath)) return null;

        FormatContext? format = null;
        CodecContext? codec = null;
        try
        {
            format = FormatContext.OpenInputUrl(oggPath);
            format.LoadStreamInfo();
            MediaStream stream = format.FindBestStream(AVMediaType.Audio);
            Codec decoder = Codec.FindDecoderById(stream.Codecpar!.CodecId);
            codec = new CodecContext(decoder);
            codec.FillParameters(stream.Codecpar);
            codec.Open(decoder);
            return new AudioTrack(format, stream, codec);
        }
        catch (Exception)
        {
            codec?.Dispose();
            format?.Dispose();
            return null;
        }
    }

    /// <summary>Repositions the decoder; the next Read delivers audio from this time.</summary>
    public void Seek(double seconds)
    {
        lock (_lock)
        {
            AVRational tb = _stream.TimeBase;
            long ts = (long)(seconds * tb.Den / tb.Num);
            try
            {
                _format.SeekFrame(ts, _stream.Index, AVSEEK_FLAG.Backward);
            }
            catch (FFmpegException)
            {
                _format.SeekFrame(0, _stream.Index, AVSEEK_FLAG.Backward);
            }
            unsafe { ffmpeg.avcodec_flush_buffers(_codec); }
            _fifoStart = 0;
            _fifoCount = 0;
            _endOfStream = false;

            // The seek lands on the previous page boundary; decode forward and
            // drop samples until the requested time.
            long skipBytes = 0;
            while (_fifoCount == 0 && !_endOfStream) DecodeMore();
            if (!_endOfStream)
            {
                // First decoded frame's pts tells us where we actually landed.
                long pts = _decoded.Pts != long.MinValue ? _decoded.Pts : ts;
                double landed = (double)pts * tb.Num / tb.Den;
                double drop = seconds - landed;
                if (drop > 0) skipBytes = (long)(drop * BytesPerSecond) & ~1L;
            }
            while (skipBytes > 0)
            {
                if (_fifoCount == 0 && !DecodeMore()) break;
                int take = (int)Math.Min(skipBytes, _fifoCount);
                _fifoStart = (_fifoStart + take) % _fifo.Length;
                _fifoCount -= take;
                skipBytes -= take;
            }
        }
    }

    /// <summary>Fills the buffer with PCM (silence past end of stream). Never returns less than requested.</summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            int written = 0;
            while (written < count)
            {
                if (_fifoCount == 0 && !DecodeMore())
                {
                    Array.Clear(buffer, offset + written, count - written);
                    written = count;
                    break;
                }
                int take = Math.Min(count - written, Math.Min(_fifoCount, _fifo.Length - _fifoStart));
                Array.Copy(_fifo, _fifoStart, buffer, offset + written, take);
                _fifoStart = (_fifoStart + take) % _fifo.Length;
                _fifoCount -= take;
                written += take;
            }
            return written;
        }
    }

    /// <summary>Decodes one more audio frame into the FIFO. False at end of stream.</summary>
    private bool DecodeMore()
    {
        while (true)
        {
            CodecResult result = _codec.ReceiveFrame(_decoded);
            if (result == CodecResult.Success)
            {
                AppendConverted(_decoded);
                return true;
            }
            if (result == CodecResult.EOF)
            {
                _endOfStream = true;
                return false;
            }

            // Needs more input.
            _packet.Unref();
            try
            {
                _format.ReadFrame(_packet);
            }
            catch (FFmpegException)
            {
                _endOfStream = true;
                return false;
            }
            if (_packet.StreamIndex != _stream.Index) continue;
            try { _codec.SendPacket(_packet); }
            catch (FFmpegException) { /* damaged packet: skip */ }
            finally { _packet.Unref(); }
        }
    }

    private unsafe void AppendConverted(Frame frame)
    {
        int samples = frame.NbSamples;
        int srcChannels = Math.Max(1, frame.ChLayout.nb_channels);
        var format = (AVSampleFormat)frame.Format;
        int needed = samples * Channels * 2;
        EnsureFifoSpace(needed);

        for (int i = 0; i < samples; i++)
        {
            for (int ch = 0; ch < Channels; ch++)
            {
                int srcCh = Math.Min(ch, srcChannels - 1);
                short value = format switch
                {
                    AVSampleFormat.Fltp => FloatToS16(((float*)frame.Data[srcCh])[i]),
                    AVSampleFormat.Flt => FloatToS16(((float*)frame.Data[0])[i * srcChannels + srcCh]),
                    AVSampleFormat.S16p => ((short*)frame.Data[srcCh])[i],
                    AVSampleFormat.S16 => ((short*)frame.Data[0])[i * srcChannels + srcCh],
                    AVSampleFormat.S32p => (short)(((int*)frame.Data[srcCh])[i] >> 16),
                    AVSampleFormat.S32 => (short)(((int*)frame.Data[0])[i * srcChannels + srcCh] >> 16),
                    AVSampleFormat.Dblp => FloatToS16((float)((double*)frame.Data[srcCh])[i]),
                    _ => 0,
                };
                int pos = (_fifoStart + _fifoCount) % _fifo.Length;
                _fifo[pos] = (byte)value;
                _fifo[(pos + 1) % _fifo.Length] = (byte)(value >> 8);
                _fifoCount += 2;
            }
        }
    }

    private static short FloatToS16(float v) => (short)Math.Clamp((int)(v * 32767f), short.MinValue, short.MaxValue);

    private void EnsureFifoSpace(int needed)
    {
        if (_fifoCount + needed <= _fifo.Length) return;
        int newSize = _fifo.Length;
        while (_fifoCount + needed > newSize) newSize *= 2;
        byte[] grown = new byte[newSize];
        for (int i = 0; i < _fifoCount; i++)
            grown[i] = _fifo[(_fifoStart + i) % _fifo.Length];
        _fifo = grown;
        _fifoStart = 0;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _packet.Dispose();
            _decoded.Dispose();
            _codec.Dispose();
            _format.Dispose();
        }
    }
}

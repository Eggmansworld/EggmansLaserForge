using Ldp.Engine;
using NAudio.Wave;
using System;

namespace Ldp.App;

/// <summary>
/// Sound output for one video's companion OGG. During playback the sound
/// device is the master clock: <see cref="PositionSeconds"/> reports what is
/// audible right now, and the video slaves its frame position to it, so
/// picture and audio can never drift apart.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private sealed class TrackWaveProvider(AudioTrack track) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(track.SampleRate, 16, track.Channels);
        public int Read(byte[] buffer, int offset, int count) => track.Read(buffer, offset, count);
    }

    private readonly AudioTrack _track;
    private readonly WaveOutEvent _waveOut;
    private double _baseSeconds;

    public AudioPlayer(AudioTrack track)
    {
        _track = track;
        _waveOut = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 4 };
        _waveOut.Init(new TrackWaveProvider(track));
    }

    public void PlayFrom(double seconds)
    {
        _waveOut.Stop(); // also resets the device position counter
        _track.Seek(seconds);
        _baseSeconds = seconds;
        _waveOut.Play();
    }

    public void Stop() => _waveOut.Stop();

    public double PositionSeconds =>
        _baseSeconds + (double)_waveOut.GetPosition() / _waveOut.OutputWaveFormat.AverageBytesPerSecond;

    public void Dispose()
    {
        _waveOut.Dispose();
        _track.Dispose();
    }
}

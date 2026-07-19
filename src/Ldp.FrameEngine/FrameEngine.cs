namespace Ldp.Engine;

/// <summary>
/// Public facade: open an M2V, then ask for any frame by number and get the
/// exact picture the Hypseus Singe emulator would display for that number.
/// Recently decoded frames are cached so jogging backward is instant.
/// Not thread-safe; call from one thread (or serialize externally).
/// </summary>
public sealed class FrameEngine : IDisposable
{
    private const int CacheCapacity = 64;

    private readonly FrameDecoder _decoder;
    private readonly Dictionary<int, LinkedListNode<FrameImage>> _cacheMap = new(CacheCapacity);
    private readonly LinkedList<FrameImage> _cacheOrder = new();

    public FrameIndex Index { get; }
    public int FrameCount => Index.FrameCount;
    public double Fps => Index.Fps;

    private FrameEngine(FrameIndex index)
    {
        Index = index;
        _decoder = new FrameDecoder(index);
    }

    public static FrameEngine Open(string m2vPath, IProgress<double>? progress = null,
                                   CancellationToken ct = default, bool useCache = true)
    {
        if (useCache && FrameIndexCache.TryRead(m2vPath) is { } cached)
            return new FrameEngine(cached);

        FrameIndex index = M2vScanner.Scan(m2vPath, progress, ct);
        if (useCache)
        {
            try { FrameIndexCache.Write(index); }
            catch (IOException) { /* cache is an optimization, never fatal */ }
        }
        return new FrameEngine(index);
    }

    public FrameImage GetFrame(int frameNumber)
    {
        frameNumber = Math.Clamp(frameNumber, 0, FrameCount - 1);

        if (_cacheMap.TryGetValue(frameNumber, out LinkedListNode<FrameImage>? node))
        {
            _cacheOrder.Remove(node);
            _cacheOrder.AddFirst(node);
            return node.Value;
        }

        return _decoder.DecodeFrame(frameNumber, CacheAdd);
    }

    /// <summary>Writes the VLDP .dat index next to the .m2v and returns its path.</summary>
    public string WriteVldpDat()
    {
        string datPath = VldpDat.PathFor(Index.M2vPath);
        VldpDat.Write(Index, datPath);
        return datPath;
    }

    private void CacheAdd(FrameImage image)
    {
        if (_cacheMap.TryGetValue(image.FrameNumber, out LinkedListNode<FrameImage>? existing))
        {
            _cacheOrder.Remove(existing);
            _cacheMap.Remove(image.FrameNumber);
        }
        _cacheMap[image.FrameNumber] = _cacheOrder.AddFirst(image);

        while (_cacheMap.Count > CacheCapacity)
        {
            FrameImage oldest = _cacheOrder.Last!.Value;
            _cacheOrder.RemoveLast();
            _cacheMap.Remove(oldest.FrameNumber);
        }
    }

    public void Dispose() => _decoder.Dispose();
}

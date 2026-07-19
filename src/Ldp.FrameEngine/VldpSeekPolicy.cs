namespace Ldp.Engine;

/// <summary>
/// Port of the seek resolution loop in hypseus-singe's ivldp_respond_req_play_or_skip
/// (vldp_internal.cpp). Given a target coded picture, decide which I-frame to
/// start decoding from and how many decoded frames to discard before the
/// target is on screen. This includes VLDP's quirk of backing up one extra
/// GOP when the target is within two frames of its own I-frame (decoding
/// that close to an anchor produced corrupted output in libmpeg2).
/// </summary>
public static class VldpSeekPolicy
{
    public readonly record struct SeekPlan(int AnchorPicture, long AnchorOffset, int FramesToSkip);

    public static SeekPlan Resolve(long[] datEntries, int targetPicture)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetPicture);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(targetPicture, datEntries.Length);

        int framesToSkip = 0;
        int actual = targetPicture;
        int skippedI = 0;
        long proposed = datEntries[actual];

        while (true)
        {
            while (proposed == -1 && actual > 0)
            {
                framesToSkip++;
                actual--;
                proposed = datEntries[actual];
            }
            skippedI++;

            if (skippedI < 2 && framesToSkip < 3 && actual > 0)
                proposed = -1; // force the walk to continue to the previous I-frame
            else
                break;
        }

        // Degenerate stream that does not start with an I-frame: decode from
        // the very beginning (offset 0 lands on the sequence header).
        if (proposed < 0) proposed = 0;

        return new SeekPlan(actual, proposed, framesToSkip);
    }
}

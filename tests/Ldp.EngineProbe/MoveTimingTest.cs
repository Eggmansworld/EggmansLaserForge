using Ldp.Project;

namespace Ldp.EngineProbe;

/// <summary>
/// Checks for two timing rules.
///
/// The release exemption: a hold and its LetGo are one gesture, not two
/// reactions. The player is already holding the input, so no cushion is owed
/// between them — letting go on the very next frame is ordinary authoring, and
/// the old rule flagged every hold in the project as broken.
///
/// Window normalising: some published games shorten each move's window as a
/// difficulty knob, which defeats the one the framework has. Normal, Hard and
/// Extreme each shrink the window further, so a window hand-cut to a few frames
/// leaves nothing to shrink and those modes stop working.
/// </summary>
public static class MoveTimingTest
{
    private const int Base = Difficulty.DefaultBaseWindow; // 20

    public static void Run(Action<string, bool> Check)
    {
        ReleaseChecks(Check);
        NormalizeChecks(Check);
    }

    private static Clip SceneWith(params InteractionMarker[] moves)
    {
        var clip = new Clip { Name = "S", StartFrame = 5000, EndFrame = 9000 };
        clip.Interactions.AddRange(moves);
        return clip;
    }

    private static InteractionMarker M(int frame, InputKind kind, int? end = null) =>
        new() { Frame = frame, Input = kind, EndFrameOverride = end };

    private static void ReleaseChecks(Action<string, bool> Check)
    {
        // The author's own case: a hold running 5876-5916, released at 5917.
        InteractionMarker hold = M(5876, InputKind.HoldUp, 5916);
        InteractionMarker release = M(5917, InputKind.LetGo);
        Clip scene = SceneWith(hold, release);
        HashSet<Guid> bad = InteractionRules.FindViolators(scene, Base);
        Check("release: letting go one frame after a hold is fine", !bad.Contains(release.Id));
        Check("release: the hold itself is fine", !bad.Contains(hold.Id));

        // Every hold kind gets the exemption, not just HoldUp.
        foreach (InputKind kind in (InputKind[])[InputKind.HoldUp, InputKind.HoldDown,
                 InputKind.HoldLeft, InputKind.HoldRight, InputKind.HoldButton])
        {
            InteractionMarker h = M(5876, kind, 5916);
            InteractionMarker r = M(5917, InputKind.LetGo);
            Check($"release: {kind} is exempt too",
                  !InteractionRules.FindViolators(SceneWith(h, r), Base).Contains(r.Id));
        }

        // A release must still come after the hold starts.
        InteractionMarker early = M(5876, InputKind.LetGo);
        Check("release: a release at the hold's own frame is flagged",
              InteractionRules.FindViolators(SceneWith(M(5876, InputKind.HoldUp, 5916), early), Base)
                              .Contains(early.Id));

        // The exemption is for a release AFTER A HOLD only.
        InteractionMarker strayRelease = M(5917, InputKind.LetGo);
        Check("release: a release crowding an ordinary move is still flagged",
              InteractionRules.FindViolators(SceneWith(M(5900, InputKind.Up), strayRelease), Base)
                              .Contains(strayRelease.Id));

        // ...and a non-release crowding a hold is still flagged.
        InteractionMarker crowding = M(5917, InputKind.Down);
        Check("release: an ordinary move crowding a hold is still flagged",
              InteractionRules.FindViolators(SceneWith(M(5876, InputKind.HoldUp, 5916), crowding), Base)
                              .Contains(crowding.Id));

        // The move after a release is measured from the release, as the author
        // asked: this is the one thing that should still warn here.
        InteractionMarker tooSoon = M(5940, InputKind.Up);
        Check("release: a move too soon after the release is flagged",
              InteractionRules.FindViolators(
                  SceneWith(M(5876, InputKind.HoldUp, 5916), M(5917, InputKind.LetGo), tooSoon), Base)
                  .Contains(tooSoon.Id));

        // The release's own window ends at 5917+20-1 = 5936, so the next move
        // may open from 5937+20 = 5957.
        InteractionMarker farEnough = M(5957, InputKind.Up);
        Check("release: a properly spaced move after the release is fine",
              !InteractionRules.FindViolators(
                  SceneWith(M(5876, InputKind.HoldUp, 5916), M(5917, InputKind.LetGo), farEnough), Base)
                  .Contains(farEnough.Id));

        // Back-to-back gestures still work.
        Check("release: two hold/release pairs in a row are clean",
              InteractionRules.FindViolators(SceneWith(
                  M(5876, InputKind.HoldUp, 5916), M(5917, InputKind.LetGo),
                  M(5957, InputKind.HoldLeft, 5997), M(5998, InputKind.LetGo)), Base).Count == 0);
    }

    private static void NormalizeChecks(Action<string, bool> Check)
    {
        InteractionMarker shortened = M(1000, InputKind.Up, 1005);   // brutal 6-frame window
        InteractionMarker stretched = M(2000, InputKind.Down, 2100);
        InteractionMarker standard = M(3000, InputKind.Left);
        InteractionMarker skip = M(4000, InputKind.Skip, 4800);
        Clip scene = SceneWith(shortened, stretched, standard, skip);

        Check("normalize: a shortened window is a custom one",
              InteractionRules.HasCustomWindow(shortened));
        Check("normalize: a stretched window is a custom one",
              InteractionRules.HasCustomWindow(stretched));
        Check("normalize: an untouched move is not", !InteractionRules.HasCustomWindow(standard));
        Check("normalize: a skip is never counted", !InteractionRules.HasCustomWindow(skip));

        int changed = InteractionRules.NormalizeWindows([scene]);
        Check("normalize: both custom windows were reset", changed == 2);
        Check("normalize: the shortened move is back to standard",
              shortened.EndFrameOverride == null && shortened.WindowEnd(Base) == 1000 + Base - 1);
        Check("normalize: the stretched move is back to standard", stretched.EndFrameOverride == null);
        Check("normalize: the untouched move is unchanged", standard.EndFrameOverride == null);
        Check("normalize: the skip kept its passage", skip.EndFrameOverride == 4800);

        Check("normalize: running it again changes nothing",
              InteractionRules.NormalizeWindows([scene]) == 0);
        Check("normalize: an empty project is a no-op",
              InteractionRules.NormalizeWindows([]) == 0);

        // The point of it: after resetting, the harder difficulties have room.
        Check("normalize: a reset window still leaves room at Extreme",
              Difficulty.Window(Base, -8) > 0);
    }
}

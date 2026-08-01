namespace Ldp.Project;

/// <summary>
/// Sanity-checks a level's intro clip on import.
///
/// A Level line carries two frames before its scene count:
///
///     Level[2] = {"Yakuza", 19030, 21775, 5, 0, 0, -1}
///                            ^start  ^end of intro clip
///
/// The framework plays that span once, before the level's first scene, and
/// decides whether there is an intro at all by the gap:
///
///     main.singe:1909
///     if Level[i][INTROCLIPEND] - Level[i][INTROCLIP] &lt; 2 then bSkipIntroClip = true
///
/// so a gap under 2 — `{41084, 41085}` — is the framework's own way of saying
/// "no intro". This app writes Start+1 for the same reason.
///
/// Community scripts frequently put something else in the second slot: the end
/// of the level, or where the first scene begins. The result is an "intro" of
/// thousands of frames that plays a chunk of the game before the level starts.
/// Those are corrected to Start+1 on import and reported, because the alternative
/// is an author opening the project and seeing an intro passage they never wrote.
///
/// The test for it is structural rather than a length guess: an intro runs
/// BEFORE the gameplay, so it may not extend past the level's first scene. A
/// genuine title card passes at any length; a value that swallows the opening
/// scene fails however short it is.
/// </summary>
public static class LevelIntro
{
    /// <summary>
    /// Ceiling used only when a level has no scenes to measure against, so the
    /// structural test cannot run. Ten seconds at any of the frame rates Singe
    /// supports — long for a title card, far short of the multi-minute spans
    /// that turn up in mis-filled scripts.
    /// </summary>
    public const int MaxIntroFramesWithoutScenes = 600;

    /// <summary>The framework treats a gap below this as "no intro clip".</summary>
    public const int MinIntroGap = 2;

    /// <summary>
    /// Whether the level declares an intro clip at all, by the framework's rule.
    /// </summary>
    public static bool DeclaresIntro(int startFrame, int introEndFrame) =>
        introEndFrame - startFrame >= MinIntroGap;

    /// <summary>
    /// Whether a declared intro is believable.
    /// <paramref name="firstSceneFrame"/> is the level's first gameplay frame,
    /// or null when the level has no scenes.
    /// </summary>
    public static bool IsPlausible(int startFrame, int introEndFrame, int? firstSceneFrame)
    {
        if (!DeclaresIntro(startFrame, introEndFrame)) return true; // nothing claimed
        if (firstSceneFrame is not { } firstScene)
            return introEndFrame - startFrame <= MaxIntroFramesWithoutScenes;

        // The intro plays before the level does. Running level with, or past,
        // its opening scene means the second number is not an intro end.
        return introEndFrame <= firstScene;
    }

    /// <summary>
    /// Returns the intro end to keep. Implausible values collapse to
    /// <paramref name="startFrame"/> + 1, the "no intro" encoding.
    /// </summary>
    public static int Correct(int startFrame, int introEndFrame, int? firstSceneFrame) =>
        IsPlausible(startFrame, introEndFrame, firstSceneFrame) ? introEndFrame : startFrame + 1;

    /// <summary>
    /// Explains a correction for the import log, or null when none is needed.
    /// </summary>
    public static string? Explain(int number, string title, int startFrame, int introEndFrame, int? firstSceneFrame)
    {
        if (IsPlausible(startFrame, introEndFrame, firstSceneFrame)) return null;
        string why = firstSceneFrame is { } first
            ? $"runs to {introEndFrame}, past its first scene at {first}"
            : $"is {introEndFrame - startFrame} frames long with no scenes to check it against";
        return $"Level[{number}] '{title}': intro clip {startFrame}-{introEndFrame} {why}. " +
               $"Corrected to no intro ({startFrame}-{startFrame + 1}); set one in Game Setup if the level really has one.";
    }
}

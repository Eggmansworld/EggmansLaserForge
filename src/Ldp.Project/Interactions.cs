namespace Ldp.Project;

/// <summary>The player inputs a Singe game can ask for (4-way joystick + 2 buttons).</summary>
public enum InputKind
{
    Up,
    Down,
    Left,
    Right,
    Button1,
    Button2,

    /// <summary>
    /// Any input skips ahead - used over long non-interactive passages
    /// (dialogue etc.). Its window is custom-length, not difficulty-derived:
    /// move[n] = {start, customEnd, SKIP, 0}.
    ///
    /// Implemented by BOTH global frameworks (SKIP = 100 in each globals.singe;
    /// Framework handles it at main.singe:3430 via checkSkip and draws the
    /// skip.png sprite), so it is also the way to give an otherwise
    /// non-interactive scene the one move a level scene must have.
    /// </summary>
    Skip,

    /// <summary>Any joystick direction (the WAY token in Standard Framework scripts).</summary>
    AnyDirection,

    // ---- Double moves: two inputs held at once ----
    // The framework groups these itself (main.singe:3012, ">= UPLEFT and
    // <= ACTRIGHT -- Double moves") and tests them as two concurrent held
    // states: UPLEFT is `p1UP and p1LEFT`, ACTUP is `p1BUTTON1 and p1UP`.
    // Hypseus sets those states from any bound control, keyboard included, so
    // none of these needs a thumbstick — only two keys at the same time.
    UpLeft, UpRight, DownLeft, DownRight,
    ActUp, ActDown, ActLeft, ActRight,

    // ---- Hold, then release ----
    // These come in pairs. The framework reads the move AFTER a hold expecting
    // LETGO (main.singe:2296, 2360, 2499) and rewinds by stepping currentMove-2,
    // so a hold without a LetGo following it is a broken script.
    HoldUp, HoldDown, HoldLeft, HoldRight, HoldButton,

    /// <summary>Release of the hold immediately preceding it. Never stands alone.</summary>
    LetGo,

    /// <summary>
    /// A framework move this app does not model — mash and run rates, and the
    /// multi-move branch constructs (CHOOSE, PATH, YESNO, TIMED). The script's
    /// own token is kept in <see cref="InteractionMarker.RawInput"/> and written
    /// back untouched, so importing a game that uses them never loses a move.
    /// </summary>
    Advanced,
}

/// <summary>
/// One expected player action inside a clip. Only the start frame is
/// authored; the reaction window is derived from the difficulty level at
/// runtime, so the author never hand-computes end frames.
/// </summary>
public sealed class InteractionMarker
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Global frame where the reaction window opens.</summary>
    public int Frame { get; set; }

    public InputKind Input { get; set; }

    /// <summary>
    /// Custom window end (global, inclusive) for inputs whose window is not
    /// difficulty-derived - currently Skip. Null for normal moves.
    /// </summary>
    public int? EndFrameOverride { get; set; }

    /// <summary>
    /// Death scene played when this move is missed; null = the scene's first
    /// Death wire. Exporters build the global Death[] table from these.
    /// </summary>
    public Guid? DeathClipId { get; set; }

    /// <summary>
    /// True when the author deliberately wrote Death# 0 on a normal move
    /// (e.g. the surrounding video already shows the failure). Distinct from
    /// "unset": null DeathClipId with this false falls back to the scene's
    /// Death wire; with this true the exporter writes 0 and doesn't warn.
    /// </summary>
    public bool ExplicitNoDeath { get; set; }

    /// <summary>
    /// The script's own token when <see cref="Input"/> is
    /// <see cref="InputKind.Advanced"/>, e.g. "MASHMAX" or "CHOOSE". Written
    /// back verbatim on export.
    ///
    /// A move whose token this app cannot name used to be dropped on import
    /// with a log line, which is silent data loss dressed up as a warning:
    /// the game still exported, just missing moves. Keeping the token costs
    /// nothing and makes an import/export round trip lossless even for moves
    /// the editor has no way to author yet.
    /// </summary>
    public string? RawInput { get; set; }

    /// <summary>
    /// Optional alternate accepted input (the 5th move element in Standard
    /// Framework scripts, e.g. move = {s, e, UP, 3, BUTTON1}).
    /// </summary>
    public InputKind? AltInput { get; set; }

    /// <summary>The alternate input's own token, when it is an unmodelled one.</summary>
    public string? RawAltInput { get; set; }

    public string? Note { get; set; }

    /// <summary>Last frame of this move's window at the given base (Easy) window.</summary>
    public int WindowEnd(int baseWindow) => EndFrameOverride ?? Frame + baseWindow - 1;
}

/// <summary>
/// Singe difficulty levels and their reaction windows. The base window is
/// Easy's; harder levels shrink it (Extreme's 12 frames is under half a
/// second of NTSC video).
/// </summary>
public static class Difficulty
{
    public const int DefaultBaseWindow = 20;

    public static readonly (string Name, int Offset)[] Levels =
    [
        ("Easy", 0),
        ("Normal", -2),
        ("Hard", -4),
        ("Extreme", -8),
    ];

    public static int Window(int baseWindow, int offset) => baseWindow + offset;
}

/// <summary>
/// Authoring-time validation of interaction placement. The binding constraint
/// is the widest (Easy) window: between two consecutive interaction starts
/// there must be room for a full window plus an equal cushion, so inputs are
/// never swallowed when moves are stacked back-to-back. Satisfying Easy
/// automatically satisfies every harder difficulty (their windows and
/// cushions are smaller).
/// </summary>
public static class InteractionRules
{
    /// <summary>window + cushion.</summary>
    public static int MinSpacing(int baseWindow) => baseWindow * 2;

    /// <summary>
    /// Returns the ids of markers that violate placement rules within their
    /// clip: too close to the previous marker (spacing &lt; window + cushion),
    /// window extending past the clip's end, or start outside the clip.
    /// </summary>
    public static HashSet<Guid> FindViolators(Clip clip, int baseWindow)
    {
        HashSet<Guid> violators = [];
        List<InteractionMarker> sorted = clip.Interactions.OrderBy(m => m.Frame).ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            InteractionMarker marker = sorted[i];
            if (marker.Frame < clip.StartFrame || marker.Frame > clip.EndFrame)
                violators.Add(marker.Id);
            if (marker.WindowEnd(baseWindow) > clip.EndFrame)
                violators.Add(marker.Id); // window runs past the scene's last frame
            if (marker.EndFrameOverride is { } end && end < marker.Frame)
                violators.Add(marker.Id); // inverted custom window

            // Cushion: after the previous move's window closes, a full window's
            // worth of frames must pass before the next window opens.
            if (i > 0 && marker.Frame < sorted[i - 1].WindowEnd(baseWindow) + 1 + baseWindow)
                violators.Add(marker.Id);
        }
        return violators;
    }
}

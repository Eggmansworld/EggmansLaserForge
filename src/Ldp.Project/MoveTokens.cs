namespace Ldp.Project;

/// <summary>
/// The framework's move vocabulary, and what each token costs an author.
///
/// Community scripts use a good deal more than the six inputs this editor
/// authors. Before this existed, anything else failed to parse and the move was
/// dropped — the game still exported, quietly missing its hardest beats.
///
/// The tiers are the framework's own groupings, not a guess:
///
///   Double   main.singe:3012 handles UPLEFT..ACTRIGHT as one block, "Double
///            moves", each tested as two concurrent held states — UPLEFT is
///            `p1UP and p1LEFT`, ACTUP is `p1BUTTON1 and p1UP`. Hypseus sets
///            those from any bound control, so these need two keys pressed
///            together, not a thumbstick.
///
///   Hold     main.singe:2738 treats HOLDUP..HOLDBUT as one block, and 2296 /
///            2360 / 2499 read the move AFTER a hold expecting LETGO, with
///            rewind stepping currentMove-2. A hold and its release are one
///            authoring unit; a hold without a following LetGo cannot work.
///
///   Rate     MASH/MASHMIN/MASHMAX and RUN/RUNMIN/RUNMAX are not three moves.
///            main.singe:2927 nudges the required rate by ±0.01 for the MIN and
///            MAX forms, so they are one move with a difficulty setting.
///
///   Branch   CHOOSE, PATH, YESNO, TIMED and friends are multi-move constructs.
///            CHOOSE overwrites the move's own death slot at runtime to hold the
///            player's pick (main.singe:6364) and steps currentMove-2. They
///            cannot be expressed as a single move row, so this editor carries
///            them through untouched rather than pretending to author them.
/// </summary>
public static class MoveTokens
{
    public enum Tier
    {
        /// <summary>One of the six inputs the editor authors directly.</summary>
        Basic,
        /// <summary>Two inputs held together. Authorable as a single move.</summary>
        Double,
        /// <summary>Hold or release. Only valid as a hold/LetGo pair.</summary>
        Hold,
        /// <summary>A mash or run rate — one move with a difficulty setting.</summary>
        Rate,
        /// <summary>A multi-move branch construct. Carried through, not authored.</summary>
        Branch,
    }

    public sealed record Info(string Token, Tier Tier, string Display, string Note);

    /// <summary>Every token the frameworks define, with the tier it belongs to.</summary>
    public static readonly IReadOnlyList<Info> All =
    [
        new("UPLEFT",    Tier.Double, "Up + Left",    "Hold Up and Left together"),
        new("UPRIGHT",   Tier.Double, "Up + Right",   "Hold Up and Right together"),
        new("DOWNLEFT",  Tier.Double, "Down + Left",  "Hold Down and Left together"),
        new("DOWNRIGHT", Tier.Double, "Down + Right", "Hold Down and Right together"),
        new("ACTUP",     Tier.Double, "Button 1 + Up",    "Hold button 1 and Up together"),
        new("ACTDOWN",   Tier.Double, "Button 1 + Down",  "Hold button 1 and Down together"),
        new("ACTLEFT",   Tier.Double, "Button 1 + Left",  "Hold button 1 and Left together"),
        new("ACTRIGHT",  Tier.Double, "Button 1 + Right", "Hold button 1 and Right together"),

        new("HOLDUP",    Tier.Hold, "Hold Up",       "Needs a Let go move straight after it"),
        new("HOLDDOWN",  Tier.Hold, "Hold Down",     "Needs a Let go move straight after it"),
        new("HOLDLEFT",  Tier.Hold, "Hold Left",     "Needs a Let go move straight after it"),
        new("HOLDRIGHT", Tier.Hold, "Hold Right",    "Needs a Let go move straight after it"),
        new("HOLDBUT",   Tier.Hold, "Hold Button 1", "Needs a Let go move straight after it"),
        new("LETGO",     Tier.Hold, "Let go",        "Releases the hold immediately before it"),

        new("MASH",     Tier.Rate, "Mash button 1",       "Repeated presses; MIN and MAX shift the rate needed"),
        new("MASHMIN",  Tier.Rate, "Mash (easier)",       "Mash with a lower rate required"),
        new("MASHMAX",  Tier.Rate, "Mash (harder)",       "Mash with a higher rate required"),
        new("MASH2",    Tier.Rate, "Mash both buttons",   "Repeated presses on either button"),
        new("MASH2MIN", Tier.Rate, "Mash both (easier)",  "Lower rate required"),
        new("MASH2MAX", Tier.Rate, "Mash both (harder)",  "Higher rate required"),
        new("RUN",      Tier.Rate, "Run",                 "Sustained input; MIN and MAX shift the rate"),
        new("RUNMIN",   Tier.Rate, "Run (easier)",        "Lower rate required"),
        new("RUNMAX",   Tier.Rate, "Run (harder)",        "Higher rate required"),
        new("MULTI",    Tier.Rate, "Multi",               "Repeated presses counted across both buttons"),
        new("DOUBLE",   Tier.Rate, "Double tap",          "Two presses in quick succession"),

        new("CHOOSE",    Tier.Branch, "Choose a way",   "Player picks a branch; spans several move rows"),
        new("PATH",      Tier.Branch, "Path",           "Branch construct spanning several move rows"),
        new("YESNO",     Tier.Branch, "Yes / No",       "Branch construct spanning several move rows"),
        new("TIMED",     Tier.Branch, "Timed",          "Timed prompt spanning several move rows"),
        new("LOOPLEFT",  Tier.Branch, "Loop left",      "Looping construct"),
        new("LOOPRIGHT", Tier.Branch, "Loop right",     "Looping construct"),
        new("WAYOUT",    Tier.Branch, "Way out",        "Exit branch"),
    ];

    private static readonly Dictionary<string, Info> ByToken =
        All.ToDictionary(i => i.Token, StringComparer.OrdinalIgnoreCase);

    public static Info? Find(string token) =>
        ByToken.TryGetValue(token.Trim(), out Info? info) ? info : null;

    /// <summary>Whether the token is one the frameworks define at all.</summary>
    public static bool IsKnown(string token) => Find(token) != null;

    /// <summary>The tier a token belongs to, or null if the frameworks don't define it.</summary>
    public static Tier? TierOf(string token) => Find(token)?.Tier;

    /// <summary>
    /// The two tiers the editor can author as ordinary move rows. Everything
    /// else round-trips but is not offered for authoring.
    /// </summary>
    public static IEnumerable<Info> Authorable =>
        All.Where(i => i.Tier is Tier.Double or Tier.Hold);

    /// <summary>The <see cref="InputKind"/> for an authorable token, else null.</summary>
    public static InputKind? KindOf(string token) => token.Trim().ToUpperInvariant() switch
    {
        "UPLEFT" => InputKind.UpLeft,
        "UPRIGHT" => InputKind.UpRight,
        "DOWNLEFT" => InputKind.DownLeft,
        "DOWNRIGHT" => InputKind.DownRight,
        "ACTUP" => InputKind.ActUp,
        "ACTDOWN" => InputKind.ActDown,
        "ACTLEFT" => InputKind.ActLeft,
        "ACTRIGHT" => InputKind.ActRight,
        "HOLDUP" => InputKind.HoldUp,
        "HOLDDOWN" => InputKind.HoldDown,
        "HOLDLEFT" => InputKind.HoldLeft,
        "HOLDRIGHT" => InputKind.HoldRight,
        "HOLDBUT" => InputKind.HoldButton,
        "LETGO" => InputKind.LetGo,
        _ => null,
    };

    /// <summary>The script token for a kind the editor models, else null.</summary>
    public static string? TokenOf(InputKind kind) => kind switch
    {
        InputKind.UpLeft => "UPLEFT",
        InputKind.UpRight => "UPRIGHT",
        InputKind.DownLeft => "DOWNLEFT",
        InputKind.DownRight => "DOWNRIGHT",
        InputKind.ActUp => "ACTUP",
        InputKind.ActDown => "ACTDOWN",
        InputKind.ActLeft => "ACTLEFT",
        InputKind.ActRight => "ACTRIGHT",
        InputKind.HoldUp => "HOLDUP",
        InputKind.HoldDown => "HOLDDOWN",
        InputKind.HoldLeft => "HOLDLEFT",
        InputKind.HoldRight => "HOLDRIGHT",
        InputKind.HoldButton => "HOLDBUT",
        InputKind.LetGo => "LETGO",
        _ => null,
    };

    /// <summary>Whether this kind is a hold that must be followed by a LetGo.</summary>
    public static bool IsHold(InputKind kind) =>
        kind is InputKind.HoldUp or InputKind.HoldDown or InputKind.HoldLeft
             or InputKind.HoldRight or InputKind.HoldButton;

    /// <summary>
    /// Pairing faults in one scene's moves, in play order.
    ///
    /// The framework reads the move after a hold expecting LETGO
    /// (main.singe:2296, 2360, 2499) and rewinds by stepping currentMove-2, so
    /// the two are one unit. A hold whose next move is something else, or a
    /// release with no hold in front of it, produces a scene that misbehaves at
    /// exactly the moment the player is trying to do something hard — worth
    /// catching at export rather than in play-testing.
    /// </summary>
    public static IEnumerable<string> PairingProblems(IReadOnlyList<InteractionMarker> moves, string sceneName)
    {
        for (int i = 0; i < moves.Count; i++)
        {
            InputKind kind = moves[i].Input;

            if (IsHold(kind))
            {
                if (i + 1 >= moves.Count)
                    yield return $"'{sceneName}': the hold at frame {moves[i].Frame} is the scene's last move — " +
                                 "it needs a Let go straight after it.";
                else if (moves[i + 1].Input != InputKind.LetGo)
                    yield return $"'{sceneName}': the hold at frame {moves[i].Frame} is followed by " +
                                 $"{Find(TokenOf(moves[i + 1].Input) ?? "")?.Display ?? moves[i + 1].Input.ToString()} " +
                                 $"at {moves[i + 1].Frame}, not a Let go. The framework reads the next move as the release.";
            }
            else if (kind == InputKind.LetGo && (i == 0 || !IsHold(moves[i - 1].Input)))
            {
                yield return $"'{sceneName}': the Let go at frame {moves[i].Frame} has no hold before it.";
            }
        }
    }
}

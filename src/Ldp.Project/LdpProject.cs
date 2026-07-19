namespace Ldp.Project;

/// <summary>
/// A laserdisc game authoring project: an ordered set of M2V videos that
/// together form one global frame space (mirroring a Hypseus frame file),
/// plus the clips the author has marked. Serialized as JSON (.ldproj).
/// </summary>
public sealed class LdpProject
{
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// The game's internal display name (singeSetGameName + README title),
    /// independent of the folder/file name — e.g. "Sonic the Hedgehog, The
    /// Movie (Animated, 1996)".
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// The game folder / script base name (no spaces): the folder under
    /// singe/ and the .singe / .txt / .ldproj base name. Drives MYDIR.
    /// </summary>
    public string GameFolder { get; set; } = "";

    /// <summary>
    /// Absolute path to the Hypseus Singe install root (the folder holding
    /// hypseus.exe). Lets the app place the game under singe/ and launch it.
    /// </summary>
    public string HypseusRoot { get; set; } = "";

    /// <summary>Folder/base name actually used by export (falls back to a sanitized Name).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveGameFolder =>
        !string.IsNullOrWhiteSpace(GameFolder) ? SanitizeFolder(GameFolder) : SanitizeFolder(Name);

    /// <summary>Turns a display string into a valid, space-free folder/file base name.</summary>
    public static string SanitizeFolder(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        string result = sb.ToString().Trim('_');
        return result.Length > 0 ? result : "MyGame";
    }

    /// <summary>Game author credit; required for export (goes into the script README).</summary>
    public string Author { get; set; } = "";

    /// <summary>Game version string shown in the script README (author-managed).</summary>
    public string GameVersion { get; set; } = "1.0";

    /// <summary>Release/build date in ISO YYYY-MM-DD form; required for export.</summary>
    public string GameDate { get; set; } = "";

    /// <summary>Whether a string is a valid YYYY-MM-DD date.</summary>
    public static bool IsValidDate(string s) =>
        System.DateTime.TryParseExact(s, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);

    /// <summary>One-or-two sentence story synopsis for the script README.</summary>
    public string Synopsis { get; set; } = "";

    /// <summary>Free-form author notes for the script README (history, credits, install tips).</summary>
    public string AuthorNotes { get; set; } = "";

    /// <summary>Ordered like the frame file; order defines default global bases.</summary>
    public List<VideoSource> Videos { get; set; } = [];

    public List<Clip> Clips { get; set; } = [];

    /// <summary>The storyboard node graph (additive since format version 1).</summary>
    public StoryGraph Graph { get; set; } = new();

    /// <summary>
    /// Easy-difficulty reaction window in frames; harder levels derive from it
    /// (see <see cref="Difficulty"/>). Also sets the authoring cushion.
    /// </summary>
    public int BaseWindowFrames { get; set; } = Difficulty.DefaultBaseWindow;

    /// <summary>
    /// Which Singe scripting framework the exporter targets. Defaults to the
    /// global "Framework" (the most common choice); FrameworkKimmy is the
    /// other global option, and Structure is a custom standalone framework the
    /// author supplies inside the game folder.
    /// </summary>
    public GameFramework Framework { get; set; } = GameFramework.StandardFramework;

    /// <summary>
    /// Author overrides for script variables the app has no dedicated setting
    /// for yet (scoring constants, toggles, ...). Keyed by the Lua variable
    /// name; values are written verbatim. Takes precedence over template
    /// defaults during export.
    /// </summary>
    public Dictionary<string, string> ScriptValues { get; set; } = [];

    /// <summary>
    /// Selectable audio language tracks (LangOpt). Each maps a display name to
    /// the file suffix on the matching .ogg (English = "", "_russian", etc.).
    /// </summary>
    public List<GameLanguage> Languages { get; set; } = [];

    /// <summary>
    /// The game's death pool: scene ids in Death[] table order. Seeded by
    /// import (preserving the original numbering, including curated spares no
    /// move references yet); deaths referenced only by wires/moves are
    /// appended at export time.
    /// </summary>
    public List<Guid> DeathPool { get; set; } = [];

    /// <summary>Framework slot assignments (attract videos, menus, system videos).</summary>
    public GameSlots Slots { get; set; } = new();

    /// <summary>
    /// Level definitions, in play order. Each groups gameplay scenes and maps
    /// to one Level[n] = {Title, Start, IntroEnd, SceneCount, Mirror,
    /// DeathMirror, Replay} line in the script.
    /// </summary>
    public List<GameLevel> Levels { get; set; } = [];

    // ----- Global frame space (semantics of ldp-vldp.cpp mpeg_info()) -----

    /// <summary>Global frame number of a local picture in a video.</summary>
    public int ToGlobal(int videoIndex, int localFrame) => Videos[videoIndex].GlobalBase + localFrame;

    /// <summary>
    /// Maps a global frame to (videoIndex, localFrame) exactly the way Hypseus
    /// does: the video with the largest base &lt;= frame wins, local = global - base.
    /// Returns null when the frame lands outside every video (e.g. in a gap or
    /// past the last picture).
    /// </summary>
    public (int VideoIndex, int LocalFrame)? Resolve(int globalFrame)
    {
        int index = -1;
        for (int i = 0; i < Videos.Count; i++)
            if (globalFrame >= Videos[i].GlobalBase) index = i;
            else break;

        if (index < 0) return null;
        int local = globalFrame - Videos[index].GlobalBase;
        if (local >= Videos[index].PictureCount) return null;
        return (index, local);
    }

    /// <summary>
    /// Index of the video containing a global frame (largest base &lt;= frame),
    /// or -1 when the frame falls in a gap or past every video. This is the
    /// single source of truth for "which video is this frame in" — a scene's
    /// video is always derived from its global frame, never stored, so it can
    /// never drift out of sync.
    /// </summary>
    public int VideoIndexOf(int globalFrame) => Resolve(globalFrame)?.VideoIndex ?? -1;

    /// <summary>Contiguous default base for the next video to be appended.</summary>
    public int NextGlobalBase()
    {
        if (Videos.Count == 0) return 0;
        VideoSource last = Videos[^1];
        return last.GlobalBase + last.PictureCount;
    }
}

/// <summary>
/// Singe LUA framework flavors the exporter targets. The rule of thumb: a
/// framework OUTSIDE the game folder (in the parent singe/) is a GLOBAL one
/// shared by many games; a framework INSIDE the game folder is a CUSTOM
/// STANDALONE one the author supplies themselves.
/// - StandardFramework ("Framework"): the global framework at singe/Framework;
///   the default and most common choice.
/// - FrameworkKimmy: a global derivative at singe/FrameworkKimmy with complex
///   stacked movements; mostly one author's punishing games.
/// - Structure: a custom standalone framework the author places INSIDE the
///   game folder (singe/&lt;game&gt;/Structure). Its dofile uses MYDIR, and the
///   author must supply the folder for the game to run.
/// </summary>
public enum GameFramework
{
    StandardFramework,
    FrameworkKimmy,
    Structure,
}

public static class GameFrameworkInfo
{
    /// <summary>Friendly picker label for a framework.</summary>
    public static string Display(this GameFramework framework) => framework switch
    {
        GameFramework.StandardFramework => "Framework (global)",
        GameFramework.FrameworkKimmy => "FrameworkKimmy (global)",
        GameFramework.Structure => "Structure (custom standalone)",
        _ => framework.ToString(),
    };

    /// <summary>Picker order (default first).</summary>
    public static readonly GameFramework[] Ordered =
    [
        GameFramework.StandardFramework,
        GameFramework.FrameworkKimmy,
        GameFramework.Structure,
    ];

    /// <summary>Whether this framework lives inside the game folder (via MYDIR).</summary>
    public static bool IsStandalone(this GameFramework framework) => framework == GameFramework.Structure;
}

/// <summary>
/// One selectable audio language: a display name plus the suffix appended to
/// the base video name to find its .ogg (e.g. "_russian" → main_russian.ogg;
/// the primary track uses an empty suffix).
/// </summary>
public sealed class GameLanguage
{
    public string Name { get; set; } = "";
    public string Suffix { get; set; } = "";
}

/// <summary>Definitions of the scoring constants the app can edit (with helper text).</summary>
public static class ScoringCatalog
{
    public sealed record Entry(string LuaName, string Display, string Hint);

    public static readonly Entry[] Entries =
    [
        new("SCOREMOVE", "Points per move", "Base points for a correct move (a difficulty buff is added)."),
        new("BUFFMOVE", "Difficulty buff", "Extra per-move points added for each level above Easy."),
        new("SCORESCENE", "Points per scene", "Points for finishing a scene (reduced by each death)."),
        new("SCORELEVEL", "Points per level", "Points for finishing a level."),
        new("SCOREGAME", "Points per game", "Points for finishing the whole game."),
        new("PERFECTBONUS", "Perfect bonus", "Bonus for finishing a level with zero deaths."),
        new("EXTRALIFE", "Extra life at", "Points needed for an extra life (0 = none)."),
        new("DEATHPENALTY", "Death penalty", "Points subtracted from a scene for each death."),
        new("SCORESECRET", "Secret bonus", "Points for finishing the game on a single life."),
    ];
}

/// <summary>
/// One game level: a skippable intro passage followed by gameplay scenes.
/// Mirrors the script's Level[n] = {Title, Start, IntroEnd, SceneCount,
/// Mirror, DeathMirror, Replay} line (SceneCount derives from SceneIds).
/// </summary>
public sealed class GameLevel
{
    public string Title { get; set; } = "";

    /// <summary>Global frame where the level (its intro) starts.</summary>
    public int StartFrame { get; set; }

    /// <summary>End of the skippable level intro (StartFrame + 1 when there is none).</summary>
    public int IntroEndFrame { get; set; }

    /// <summary>Frame offset of an exact mirrored copy of the level video (0 = none).</summary>
    public int Mirror { get; set; }

    /// <summary>Frame offset of mirrored death videos (0 = none).</summary>
    public int DeathMirror { get; set; }

    /// <summary>
    /// Death behavior: -1 replay until passed, 0 skip on death, 1 replay once,
    /// N&gt;1 requeue at position N for one replay.
    /// </summary>
    public int Replay { get; set; } = -1;

    /// <summary>Gameplay scenes of this level, in play order.</summary>
    public List<Guid> SceneIds { get; set; } = [];
}

/// <summary>One M2V file in the project's global frame space.</summary>
public sealed class VideoSource
{
    /// <summary>Path relative to the project file's directory (or absolute if on another drive).</summary>
    public string Path { get; set; } = "";

    /// <summary>Video file length at scan time; used to detect stale metadata.</summary>
    public long FileLength { get; set; }

    /// <summary>Coded picture count (frame count for frame-coded streams).</summary>
    public int PictureCount { get; set; }

    /// <summary>Global frame number of this video's first picture.</summary>
    public int GlobalBase { get; set; }

    public double Fps { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>A named, frame-exact range within one video of the project.</summary>
public sealed class Clip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Global frame numbers, inclusive on both ends.</summary>
    public int StartFrame { get; set; }
    public int EndFrame { get; set; }

    /// <summary>Expected player actions inside this clip (a clip can have many).</summary>
    public List<InteractionMarker> Interactions { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public int FrameCount => EndFrame - StartFrame + 1;
}

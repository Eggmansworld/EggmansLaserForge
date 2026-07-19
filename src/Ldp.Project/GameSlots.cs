namespace Ldp.Project;

/// <summary>
/// The framework's non-game elements ("Section 2" of a Singe script): attract
/// and title videos, menu still frames, system videos, and difficulty-select
/// stills. A game is only complete when the exporter can fill every required
/// slot, so these are assignable in the project alongside gameplay scenes.
/// </summary>
public sealed class GameSlots
{
    /// <summary>Video-range slots, each filled by a scene from the bin.</summary>
    public Dictionary<RangeSlot, Guid> Ranges { get; set; } = [];

    /// <summary>Single-frame (still) slots, each a global frame number.</summary>
    public Dictionary<StillSlot, int> Stills { get; set; } = [];
}

/// <summary>Framework slots that play a video passage.</summary>
public enum RangeSlot
{
    Title,
    Intro01,
    Intro02,
    Intro03,
    IntroGame,
    Continue,
    LevelClear,
    GetReady,
    SupDeath,
    GameOver,
    GameOverAlt,
    NewHighScore,
    EnterHighScore,
    Rankings,
    Map,

    /// <summary>Optional quit video (Standard Framework).</summary>
    Quit,
}

/// <summary>Framework slots that show a single still frame.</summary>
public enum StillSlot
{
    Controls,
    SaveMenu,
    OptionsMenu,
    RankingsMenu,
    Victory,
    SpecialMoves,
    Secret,
    Trophy,
    Hints,
    DifficultyEasy,
    DifficultyNormal,
    DifficultyHard,
    DifficultyExtreme,

    /// <summary>Quit screen frame (Standard Framework).</summary>
    QuitScreen,

    /// <summary>New Game screen frame (Standard Framework).</summary>
    NewGame,

    /// <summary>Menu video base frame - offsetMenus (Standard Framework).</summary>
    MenusBase,
}

/// <summary>
/// Display and export metadata for every framework slot, in the order the
/// script declares them. Lua names match the Framework/FrameworkKimmy
/// variable names so the exporter is a straight lookup.
/// </summary>
public static class SlotCatalog
{
    public sealed record RangeInfo(RangeSlot Slot, string LuaName, string Display, string Hint, bool Required);
    public sealed record StillInfo(StillSlot Slot, string LuaName, string Display, string Hint, bool Required);

    public static readonly RangeInfo[] Ranges =
    [
        new(RangeSlot.Title, "offsetTitle", "Title video", "Shown once at first boot (logo/intro)", true),
        new(RangeSlot.Intro01, "offsetIntro01", "Attract video 1", "Arcade-style attract loop entry", true),
        new(RangeSlot.Intro02, "offsetIntro02", "Attract video 2", "Second attract video", false),
        new(RangeSlot.Intro03, "offsetIntro03", "Attract video 3", "Third attract video", false),
        new(RangeSlot.IntroGame, "offsetIntroGame", "Game intro", "Played only when a game starts (optional)", false),
        new(RangeSlot.Continue, "offsetContinue", "Continue", "~15s countdown video for the continue decision", true),
        new(RangeSlot.LevelClear, "offsetClear", "Level cleared", "After each level (can be a single frame)", true),
        new(RangeSlot.GetReady, "offsetGetReady", "Get ready", "Resurrect video after a death (recommended)", false),
        new(RangeSlot.SupDeath, "offsetSupDeath", "Extra death", "Bonus death video, e.g. villain laughing (recommended)", false),
        new(RangeSlot.GameOver, "offsetGameOver", "Game over", "Regular ending", true),
        new(RangeSlot.GameOverAlt, "offsetGameOverAlt", "Game over (alt)", "Happy ending, e.g. after a high score", false),
        new(RangeSlot.NewHighScore, "offsetNewHScore", "High score announce", "Keep short, 4-5 seconds", true),
        new(RangeSlot.EnterHighScore, "offsetEnterHScore", "Enter initials", "20-30 seconds for initials entry", true),
        new(RangeSlot.Rankings, "offsetRankings", "Rankings video", "Shown after high score entry", false),
        new(RangeSlot.Map, "offsetMap", "Map", "Optional map video between levels", false),
        new(RangeSlot.Quit, "offsetQuit", "Quit video", "Optional quit video (Standard Framework)", false),
    ];

    public static readonly StillInfo[] Stills =
    [
        new(StillSlot.Controls, "frameControls", "Instructions", "Key/button instructions between attract videos", true),
        new(StillSlot.SaveMenu, "frameSave", "Load/Save menu", "Background for the load/save menu", true),
        new(StillSlot.OptionsMenu, "frameOptions", "Options menu", "Background for the service/options menu", true),
        new(StillSlot.RankingsMenu, "frameRankings", "Top scores", "Background for the top-10 scores", true),
        new(StillSlot.Victory, "frameVictory", "Victory", "Shown when the game is completed", true),
        new(StillSlot.SpecialMoves, "frameSpecial", "Special moves", "Can reuse the instructions frame", false),
        new(StillSlot.Secret, "frameSecret", "Secret level", "Shown when finishing on one life (optional)", false),
        new(StillSlot.Trophy, "frameTrophy", "Trophies", "Optional trophies frame", false),
        new(StillSlot.Hints, "frameHints", "Hints", "Optional hints frame", false),
        new(StillSlot.DifficultyEasy, "frameEasy", "Difficulty: Easy", "Shown during in-game difficulty choice", false),
        new(StillSlot.DifficultyNormal, "frameNormal", "Difficulty: Normal", "", false),
        new(StillSlot.DifficultyHard, "frameHard", "Difficulty: Hard", "", false),
        new(StillSlot.DifficultyExtreme, "frameExtreme", "Difficulty: Extreme", "", false),
        new(StillSlot.QuitScreen, "frameQuit", "Quit screen", "Optional (Standard Framework)", false),
        new(StillSlot.NewGame, "frameNewGame", "New Game screen", "Optional (Standard Framework)", false),
        new(StillSlot.MenusBase, "offsetMenus", "Menus base frame", "Menu video start frame (Standard Framework)", false),
    ];
}

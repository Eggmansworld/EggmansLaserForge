using System.Text;

namespace Ldp.Project;

/// <summary>
/// Generates the declarative game script (.singe) a Singe framework consumes:
/// Section 2 slot offsets, the numbered global Death[] table, Level[] lines,
/// and setupMoves - everything derived from the project so no frame number is
/// ever hand-maintained. Also emits the frame file that maps global frames to
/// the project's videos (gapless by construction).
///
/// Two paths share the same generators: <see cref="Export"/> builds a whole
/// script from scratch, and <see cref="SingeTemplate.Apply"/> fills a
/// community-authored template so the result keeps every framework element
/// and helper comment of a known-good script.
/// </summary>
public static class SingeExporter
{
    public sealed record Result(string Script, string FrameFile, List<string> Warnings);

    public static Result Export(LdpProject project)
    {
        List<string> warnings = [];
        var gen = SingeGen.Build(project, warnings);
        var sb = new StringBuilder();

        // ---- Header ----
        sb.AppendLine(gen.BuildReadme());
        sb.AppendLine();
        sb.AppendLine("-- Do Not Remove/Alter these --");
        sb.AppendLine("OVERLAY_FULL     = 1");
        sb.AppendLine("OVERLAY_HALF     = 2");
        sb.AppendLine("OVERLAY_OVERSIZE = 3");
        sb.AppendLine("-- Do Not Remove/Alter these --");
        sb.AppendLine();
        sb.AppendLine($"singeSetGameName(\"{project.Name}\")");
        sb.AppendLine();
        sb.AppendLine("BASEDIR = \"singe\"");
        sb.AppendLine("BASEOVERLAY = OVERLAY_FULL");
        sb.AppendLine($"MYDIR = BASEDIR .. \"/\" .. \"{project.EffectiveGameFolder}\"");
        sb.AppendLine($"MovieFPS = {gen.MovieFps}");
        sb.AppendLine();
        sb.AppendLine(SingeGen.FrameworkDofile(project.Framework));
        sb.AppendLine();

        // ---- Section 2: slots ----
        sb.AppendLine("------------------------------------------------------------------------");
        sb.AppendLine("-- 2. Starting and ending frames for the various elements of the game --");
        sb.AppendLine("------------------------------------------------------------------------");
        sb.AppendLine();
        foreach (SlotCatalog.RangeInfo info in SlotCatalog.Ranges)
        {
            sb.AppendLine($"{info.LuaName} = {gen.Value(info.LuaName)}\t\t\t\t-- {info.Display}: {info.Hint}");
            string endName = SingeGen.RangeEndName(info);
            sb.AppendLine($"{endName} = {gen.Value(endName)}");
        }
        sb.AppendLine();
        foreach (SlotCatalog.StillInfo info in SlotCatalog.Stills)
            sb.AppendLine($"{info.LuaName} = {gen.Value(info.LuaName)}\t\t\t\t-- {info.Display}");
        sb.AppendLine();
        sb.AppendLine($"offsetMovieEnd = {gen.Value("offsetMovieEnd")}\t\t\t\t-- Last frame of the last level (Movie mode)");
        sb.AppendLine();

        // ---- Death pool ----
        sb.AppendLine("-----------------");
        sb.AppendLine("-- Death pool  --");
        sb.AppendLine("-----------------");
        sb.AppendLine();
        sb.AppendLine(gen.BuildDeathLines());
        sb.AppendLine();

        // ---- Levels ----
        sb.AppendLine($"finalstage = {project.Levels.Count}\t\t\t\t-- Last stage of the story");
        sb.AppendLine("AllowSecret = false");
        sb.AppendLine();
        sb.AppendLine(gen.BuildLevelLines());
        sb.AppendLine();

        // ---- setupMoves ----
        sb.AppendLine(gen.BuildMovesFunction());

        return new Result(sb.ToString(), BuildFrameFile(project), warnings);
    }

    /// <summary>Frame file mapping global frames to videos, gapless by construction.</summary>
    public static string BuildFrameFile(LdpProject project)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Video/");
        sb.AppendLine();
        foreach (VideoSource video in project.Videos)
            sb.AppendLine($"{video.GlobalBase}\t{System.IO.Path.GetFileName(video.Path)}");
        return sb.ToString();
    }

    public static string InputToken(InputKind input) => input switch
    {
        InputKind.Up => "UP",
        InputKind.Down => "DOWN",
        InputKind.Left => "LEFT",
        InputKind.Right => "RIGHT",
        InputKind.Button1 => "BUTTON1",
        InputKind.Button2 => "BUTTON2",
        InputKind.Skip => "SKIP",
        _ => "WAY",
    };
}

/// <summary>
/// Shared generation state and section builders used by both the from-scratch
/// exporter and the template filler. Death numbering, per-scene default
/// deaths, and the variable value map are computed once so every consumer
/// sees consistent indexes.
/// </summary>
public sealed class SingeGen
{
    private readonly LdpProject _project;
    private readonly List<string> _warnings;
    private readonly List<Guid> _deathOrder = [];
    private readonly Dictionary<Guid, int> _deathIndex = [];
    private readonly Dictionary<Guid, int> _sceneDefaultDeath = [];
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public string MovieFps { get; private set; } = "29.97";

    private SingeGen(LdpProject project, List<string> warnings)
    {
        _project = project;
        _warnings = warnings;
    }

    public static SingeGen Build(LdpProject project, List<string> warnings)
    {
        var gen = new SingeGen(project, warnings);
        gen.CollectDeaths();
        gen.BuildValueMap();
        return gen;
    }

    private Clip? SceneById(Guid id) => _project.Clips.FirstOrDefault(c => c.Id == id);

    private void CollectDeaths()
    {
        void Note(Guid id)
        {
            if (!_deathOrder.Contains(id)) _deathOrder.Add(id);
        }

        // The curated pool comes first, in its original Death[] order - this
        // keeps exported numbering identical to the imported script and
        // retains spare deaths no move references yet.
        foreach (Guid id in _project.DeathPool)
            if (SceneById(id) != null) Note(id);

        foreach (GameLevel level in _project.Levels)
            foreach (Guid sceneId in level.SceneIds)
                foreach (InteractionMarker move in SceneById(sceneId)?.Interactions ?? [])
                    if (move.DeathClipId is { } d) Note(d);
        foreach (StoryEdge edge in _project.Graph.Edges.Where(e => e.FromPort == PortKind.Death))
        {
            StoryNode? target = _project.Graph.NodeById(edge.ToNode);
            if (target?.ClipId is { } clipId) Note(clipId);
        }
        for (int i = 0; i < _deathOrder.Count; i++) _deathIndex[_deathOrder[i]] = i + 1;

        foreach (StoryNode node in _project.Graph.Nodes.Where(n => n.ClipId != null))
        {
            StoryEdge? deathEdge = _project.Graph.Edges
                .FirstOrDefault(e => e.FromNode == node.Id && e.FromPort == PortKind.Death);
            if (deathEdge != null &&
                _project.Graph.NodeById(deathEdge.ToNode)?.ClipId is { } deathClip &&
                _deathIndex.TryGetValue(deathClip, out int index))
                _sceneDefaultDeath[node.ClipId!.Value] = index;
        }
    }

    public static string RangeEndName(SlotCatalog.RangeInfo info) =>
        info.Slot is RangeSlot.GetReady or RangeSlot.SupDeath
            ? info.LuaName + "End" // the framework spells these two with a capital E
            : info.LuaName + "end";

    /// <summary>The framework folder name (also its README label).</summary>
    public static string FrameworkDir(GameFramework framework) => framework switch
    {
        GameFramework.FrameworkKimmy => "FrameworkKimmy",
        GameFramework.Structure => "Structure",
        _ => "Framework",
    };

    /// <summary>
    /// The dofile bootstrap line for a framework. Global frameworks live in the
    /// parent singe/ (BASEDIR); a standalone Structure lives inside the game
    /// folder (MYDIR), so its path is anchored differently.
    /// </summary>
    public static string FrameworkDofile(GameFramework framework)
    {
        string dir = FrameworkDir(framework);
        string anchor = framework.IsStandalone() ? "MYDIR" : "BASEDIR";
        return $"dofile({anchor} .. \"/{dir}/globals.singe\")";
    }

    private void BuildValueMap()
    {
        foreach (SlotCatalog.RangeInfo info in SlotCatalog.Ranges)
        {
            Clip? scene = _project.Slots.Ranges.TryGetValue(info.Slot, out Guid id) ? SceneById(id) : null;
            if (scene == null && info.Required)
                _warnings.Add($"Required slot not assigned: {info.Display} ({info.LuaName})");
            _values[info.LuaName] = (scene?.StartFrame ?? 0).ToString();
            _values[RangeEndName(info)] = (scene?.EndFrame ?? 0).ToString();
        }
        foreach (SlotCatalog.StillInfo info in SlotCatalog.Stills)
        {
            int frame = _project.Slots.Stills.TryGetValue(info.Slot, out int f) ? f : 0;
            if (frame == 0 && info.Required)
                _warnings.Add($"Required slot not assigned: {info.Display} ({info.LuaName})");
            _values[info.LuaName] = frame.ToString();
        }

        int movieEnd = _project.Levels
            .SelectMany(l => l.SceneIds)
            .Select(id => SceneById(id)?.EndFrame ?? 0)
            .DefaultIfEmpty(0)
            .Max();
        _values["offsetMovieEnd"] = movieEnd.ToString();
        _values["finalstage"] = _project.Levels.Count.ToString();
        _values["totalDeath"] = _deathOrder.Count.ToString();
        _values["PlayOrder"] = "{" + string.Join(",", Enumerable.Range(1, Math.Max(1, _project.Levels.Count))) + "}";

        double fps = _project.Videos.Count > 0 ? _project.Videos[0].Fps : 29.97;
        MovieFps = fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        _values["MovieFPS"] = MovieFps;

        // Author overrides for variables without dedicated settings win last.
        foreach ((string name, string value) in _project.ScriptValues)
            _values[name] = value;
    }

    /// <summary>The project's value for a script variable, or null when the app doesn't own it.</summary>
    public string? TryValue(string variableName) =>
        _values.TryGetValue(variableName, out string? value) ? value : null;

    public string Value(string variableName) => TryValue(variableName) ?? "0";

    public string GameName => _project.Name;

    // ---------- Section builders (no trailing newline) ----------

    public string BuildReadme()
    {
        var sb = new StringBuilder();
        int sceneCount = _project.Levels.Sum(l => l.SceneIds.Count);
        int moveCount = _project.Levels.SelectMany(l => l.SceneIds)
            .Sum(id => SceneById(id)?.Interactions.Count ?? 0);
        List<string> inputsUsed = _project.Levels.SelectMany(l => l.SceneIds)
            .SelectMany(id => SceneById(id)?.Interactions ?? [])
            .Select(m => m.Input)
            .Distinct()
            .OrderBy(i => i)
            .Select(SingeExporter.InputToken)
            .ToList();

        if (string.IsNullOrWhiteSpace(_project.Author))
            _warnings.Add("AUTHOR is required: set it in Game Setup (Game Info) before release");
        if (!LdpProject.IsValidDate(_project.GameDate))
            _warnings.Add("DATE is required in YYYY-MM-DD form: set it in Game Setup (Game Info)");

        string dateText = LdpProject.IsValidDate(_project.GameDate)
            ? _project.GameDate
            : DateTime.Now.ToString("yyyy-MM-dd");

        sb.AppendLine("--[[");
        sb.AppendLine($"PROGRAM NAME:\t\t{_project.Name}");
        sb.AppendLine($"VERSION:\t\t\t{_project.GameVersion}");
        sb.AppendLine($"DATE:\t\t\t\t{dateText}");
        sb.AppendLine($"ENGINE:\t\t\t\tHypseus Singe, {FrameworkDir(_project.Framework)} framework");
        sb.AppendLine("\t\t\t\t\tLUA script written by Eggman's Laserdisc Publisher");
        sb.AppendLine($"AUTHOR:\t\t\t\t{(string.IsNullOrWhiteSpace(_project.Author) ? "(unknown - set in Game Setup)" : _project.Author)}");
        if (!string.IsNullOrWhiteSpace(_project.Synopsis))
        {
            sb.AppendLine();
            sb.AppendLine($"SYNOPSIS: {_project.Synopsis.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(_project.AuthorNotes))
        {
            sb.AppendLine();
            sb.AppendLine("Author Notes:");
            sb.AppendLine();
            sb.AppendLine(_project.AuthorNotes.Trim());
        }
        sb.AppendLine();
        sb.AppendLine("Game contents:");
        sb.AppendLine($"\tLevels:\t\t{_project.Levels.Count}");
        sb.AppendLine($"\tScenes:\t\t{sceneCount}");
        sb.AppendLine($"\tMoves:\t\t{moveCount}");
        sb.AppendLine($"\tDeaths:\t\t{_deathOrder.Count}");
        sb.AppendLine($"\tControls:\t{(inputsUsed.Count > 0 ? string.Join(", ", inputsUsed) : "(none yet)")}");
        sb.AppendLine();
        sb.AppendLine("Video files (frame file order):");
        foreach (VideoSource video in _project.Videos)
            sb.AppendLine($"\t{video.GlobalBase,8}  {System.IO.Path.GetFileName(video.Path)}");
        sb.AppendLine();
        sb.Append("]]--");
        return sb.ToString();
    }

    public string BuildLangOpt()
    {
        // Always at least English (primary track, empty suffix).
        List<GameLanguage> langs = _project.Languages.Count > 0
            ? _project.Languages
            : [new GameLanguage { Name = "English", Suffix = "" }];

        var sb = new StringBuilder();
        sb.AppendLine("LangOpt = {");
        for (int i = 0; i < langs.Count; i++)
        {
            GameLanguage lang = langs[i];
            sb.Append($"\t{{ \"{lang.Name}\", \"{lang.Suffix}\" }}");
            sb.AppendLine(i < langs.Count - 1 ? "," : "");
        }
        sb.Append('}');
        return sb.ToString();
    }

    public string BuildDeathLines()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"totalDeath = {_deathOrder.Count}\t\t\t\t\t-- Total number of death scenes");
        sb.AppendLine();
        foreach (Guid id in _deathOrder)
        {
            Clip? scene = SceneById(id);
            if (scene == null) continue;
            sb.AppendLine($"Death[{_deathIndex[id]}] = {{{scene.StartFrame}, {scene.EndFrame}}}\t\t\t\t-- {scene.Name}");
        }
        return sb.ToString().TrimEnd('\r', '\n');
    }

    public string BuildLevelLines()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _project.Levels.Count; i++)
        {
            GameLevel level = _project.Levels[i];
            int introEnd = level.IntroEndFrame > level.StartFrame ? level.IntroEndFrame : level.StartFrame + 1;
            sb.AppendLine($"Level[{i + 1}] = {{\"{level.Title}\", {level.StartFrame}, {introEnd}, " +
                          $"{level.SceneIds.Count}, {level.Mirror}, {level.DeathMirror}, {level.Replay}}}");
        }
        sb.AppendLine();
        sb.Append("-- Replay:  -1 = default (loop), 0 = no replay, 1 = one replay now");
        return sb.ToString();
    }

    public string BuildMovesFunction()
    {
        int baseWindow = _project.BaseWindowFrames;
        var sb = new StringBuilder();
        sb.AppendLine("function setupMoves(thisLevel, thisScene)");
        for (int levelIdx = 0; levelIdx < _project.Levels.Count; levelIdx++)
        {
            GameLevel level = _project.Levels[levelIdx];
            sb.AppendLine($"\t{(levelIdx == 0 ? "if" : "elseif")} thisLevel == {levelIdx + 1} then\t\t-- {level.Title}");
            for (int sceneIdx = 0; sceneIdx < level.SceneIds.Count; sceneIdx++)
            {
                Clip? scene = SceneById(level.SceneIds[sceneIdx]);
                if (scene == null)
                {
                    _warnings.Add($"Level {levelIdx + 1} scene {sceneIdx + 1} is missing from the project");
                    continue;
                }
                sb.AppendLine($"\t\t{(sceneIdx == 0 ? "if" : "elseif")} thisScene == {sceneIdx + 1} then");
                sb.AppendLine($"\t\t\tsceneStart = {scene.StartFrame}");
                sb.AppendLine($"\t\t\tsceneEnd = {scene.EndFrame}");

                List<InteractionMarker> moves = scene.Interactions.OrderBy(m => m.Frame).ToList();
                sb.AppendLine($"\t\t\ttotalMoves = {moves.Count}");
                sb.AppendLine();
                for (int n = 0; n < moves.Count; n++)
                {
                    InteractionMarker move = moves[n];
                    int end = move.EndFrameOverride ?? move.Frame + baseWindow;
                    int death = move.Input == InputKind.Skip || move.ExplicitNoDeath ? 0
                        : move.DeathClipId is { } d && _deathIndex.TryGetValue(d, out int di) ? di
                        : _sceneDefaultDeath.GetValueOrDefault(scene.Id);
                    if (death == 0 && move.Input != InputKind.Skip && !move.ExplicitNoDeath)
                        _warnings.Add($"'{scene.Name}' move at {move.Frame} has no death scene " +
                                      "(assign one to the move or wire the scene's Death port)");
                    string alt = move.AltInput is { } a ? $", {SingeExporter.InputToken(a)}" : "";
                    sb.AppendLine($"\t\t\tmove[{n + 1}] = {{{move.Frame}, {end}, {SingeExporter.InputToken(move.Input)}, {death}{alt}}}");
                }
                sb.AppendLine();
            }
            sb.AppendLine("\t\tend");
        }
        if (_project.Levels.Count > 0) sb.AppendLine("\tend");
        else _warnings.Add("No levels defined - the game has no playable content");
        sb.Append("end");
        return sb.ToString();
    }
}

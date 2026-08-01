using System.Text.RegularExpressions;

namespace Ldp.Project;

/// <summary>
/// Parses an existing Singe game script (Standard Framework or FrameworkKimmy
/// dialect) into project content: scenes with moves, the death pool, level
/// definitions, framework slot assignments, and a playable storyboard graph.
/// Also the community-library auditor: everything the spacing validator flags
/// after an import is a latent timing bug in the original game.
/// </summary>
public static partial class SingeImporter
{
    public sealed record Result(int Levels, int Scenes, int Moves, int Deaths, int SlotsFilled, List<string> Warnings);

    [GeneratedRegex(@"^\s*Death\[(\d+)\]\s*=\s*\{\s*(\d+)\s*,\s*(\d+)\s*\}\s*(?:;?\s*--\s*(.*?)\s*)?$", RegexOptions.Multiline)]
    private static partial Regex DeathPattern();

    [GeneratedRegex(@"^\s*Level\[(\d+)\]\s*=\s*\{\s*""([^""]*)""\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)", RegexOptions.Multiline)]
    private static partial Regex LevelPattern();

    [GeneratedRegex(@"(?:else)?if\s+thisLevel\s*==\s*(\d+)")]
    private static partial Regex LevelBranchPattern();

    /// <summary>Scene and move frames are relative to each level's start when this is on.</summary>
    [GeneratedRegex(@"^[ \t]*RelativeFrames[ \t]*=[ \t]*true", RegexOptions.Multiline)]
    private static partial Regex RelativeFramesPattern();

    /// <summary>The naming every frame and offset in these scripts follows.</summary>
    [GeneratedRegex(@"^(offset\w+|frame[A-Z]\w*)$")]
    private static partial Regex FrameNamePattern();

    [GeneratedRegex(@"(?:else)?if\s+thisScene\s*==\s*(\d+)")]
    private static partial Regex SceneBranchPattern();

    // Scene bounds accept the same "base + count" form the menu block uses; the
    // capture is resolved through the script's symbol table, not parsed as an int.
    [GeneratedRegex(@"^\s*sceneStart\s*=\s*([A-Za-z_]\w*[ \t]*[+-][ \t]*\d+|[A-Za-z_]\w*|\d+)", RegexOptions.Multiline)]
    private static partial Regex SceneStartPattern();

    [GeneratedRegex(@"^\s*sceneEnd\s*=\s*([A-Za-z_]\w*[ \t]*[+-][ \t]*\d+|[A-Za-z_]\w*|\d+)", RegexOptions.Multiline)]
    private static partial Regex SceneEndPattern();

    [GeneratedRegex(@"move\[(?:n|\d+)\]\s*=\s*\{\s*(\d+)\s*,\s*(\d+)\s*,\s*(\w+)\s*,\s*(\d+)\s*(?:,\s*(\w+)\s*)?\}")]
    private static partial Regex MovePattern();

    [GeneratedRegex(@"\{\s*""([^""]*)""\s*,\s*""([^""]*)""\s*\}")]
    private static partial Regex LangEntryPattern();

    [GeneratedRegex(@"^\s*dofile\s*\(\s*(?:BASEDIR|MYDIR)\s*\.\.\s*""/(\w+)/globals\.singe""", RegexOptions.Multiline)]
    private static partial Regex FrameworkDofilePattern();

    /// <summary>Imports script content into the project (which should already contain its videos).</summary>
    public static Result Import(LdpProject project, string scriptText)
    {
        List<string> warnings = [];

        // ---- Framework choice (from the first non-commented dofile) ----
        foreach (Match m in FrameworkDofilePattern().Matches(scriptText))
        {
            // Skip commented-out alternatives (e.g. the Kimmy suggestion line).
            int lineStart = scriptText.LastIndexOf('\n', m.Index) + 1;
            string before = scriptText[lineStart..m.Index];
            if (before.Contains("--")) continue;
            project.Framework = m.Groups[1].Value switch
            {
                "FrameworkKimmy" => GameFramework.FrameworkKimmy,
                "Structure" => GameFramework.Structure,
                _ => GameFramework.StandardFramework,
            };
            break;
        }

        // ---- Language tracks (LangOpt table) ----
        int langAt = scriptText.IndexOf("LangOpt", StringComparison.Ordinal);
        if (langAt >= 0)
        {
            int brace = scriptText.IndexOf('{', langAt);
            int close = brace >= 0 ? scriptText.IndexOf('}', brace) : -1;
            // The table's closing brace is the last one before the next
            // top-level statement; find the matching outer brace.
            if (brace >= 0)
            {
                int depth = 0, end = brace;
                for (int i = brace; i < scriptText.Length; i++)
                {
                    if (scriptText[i] == '{') depth++;
                    else if (scriptText[i] == '}' && --depth == 0) { end = i; break; }
                }
                string langBody = scriptText[brace..end];
                foreach (Match m in LangEntryPattern().Matches(langBody))
                    project.Languages.Add(new GameLanguage { Name = m.Groups[1].Value, Suffix = m.Groups[2].Value });
            }
        }

        // ---- Section 2: slots ----
        // Built from every assignment in the script, not just the ones written
        // as a bare number: authors routinely define the menu block by counting
        // from a landmark ("frameVictory = offsetMenus +3"), which a digits-only
        // pattern skips silently, leaving the slot looking unset.
        LuaValues.Table symbols = LuaValues.Build(scriptText);
        IReadOnlyDictionary<string, int> offsets = symbols.Values;

        // The table covers every assignment in the script, most of which are
        // nothing to do with frames. Only report names that follow the frame
        // naming convention, or the log fills with settings this app never reads.
        List<string> unresolvedFrames = symbols.Unresolved.Where(n => FrameNamePattern().IsMatch(n)).ToList();
        if (unresolvedFrames.Count > 0)
            warnings.Add("Frame values built on names the script never defines, left unset: " +
                         string.Join(", ", unresolvedFrames.Take(8)) +
                         (unresolvedFrames.Count > 8 ? $" (+{unresolvedFrames.Count - 8} more)" : ""));

        int slotsFilled = 0;
        foreach (SlotCatalog.RangeInfo info in SlotCatalog.Ranges)
        {
            // Range slots come as <lua> + <lua>end (GetReady/SupDeath use "End").
            if (!offsets.TryGetValue(info.LuaName, out int start)) continue;
            int end = offsets.TryGetValue(info.LuaName + "end", out int e1) ? e1
                    : offsets.TryGetValue(info.LuaName + "End", out int e2) ? e2
                    : start;
            if (start == 0 && end <= 1) continue; // unset slot convention

            Clip slotScene = GetOrCreateScene(project, start, Math.Max(start, end), $"Slot: {info.Display}");
            project.Slots.Ranges[info.Slot] = slotScene.Id;
            slotsFilled++;
        }
        foreach (SlotCatalog.StillInfo info in SlotCatalog.Stills)
        {
            if (!offsets.TryGetValue(info.LuaName, out int frame) || frame == 0) continue;
            project.Slots.Stills[info.Slot] = frame;
            slotsFilled++;
        }

        // ---- Death pool ----
        Dictionary<int, Guid> deathScenes = [];
        int deaths = 0;
        foreach (Match m in DeathPattern().Matches(scriptText))
        {
            int index = int.Parse(m.Groups[1].Value);
            int start = int.Parse(m.Groups[2].Value);
            int end = int.Parse(m.Groups[3].Value);
            string comment = m.Groups[4].Success ? m.Groups[4].Value.Trim() : "";
            string name = comment.Length > 0 ? $"Death {index}: {comment}" : $"Death {index}";
            Clip deathScene = GetOrCreateScene(project, start, end, name);
            deathScenes[index] = deathScene.Id;
            deaths++;
        }

        // Preserve the author's curated pool and its numbering (including
        // spares no move references). Duplicate ranges share one scene.
        foreach ((int _, Guid id) in deathScenes.OrderBy(kv => kv.Key))
            if (!project.DeathPool.Contains(id)) project.DeathPool.Add(id);

        // ---- Levels ----
        var levelDefs = new Dictionary<int, GameLevel>();
        foreach (Match m in LevelPattern().Matches(scriptText))
        {
            int number = int.Parse(m.Groups[1].Value);
            levelDefs[number] = new GameLevel
            {
                Title = m.Groups[2].Value,
                StartFrame = int.Parse(m.Groups[3].Value),
                IntroEndFrame = int.Parse(m.Groups[4].Value),
                Mirror = int.Parse(m.Groups[6].Value),
                DeathMirror = int.Parse(m.Groups[7].Value),
                Replay = int.Parse(m.Groups[8].Value),
            };
        }

        // ---- setupMoves: walk the function line by line, tracking branches ----
        int setupStart = scriptText.IndexOf("function setupMoves", StringComparison.Ordinal);
        string body = setupStart >= 0 ? scriptText[setupStart..] : "";
        int currentLevel = 0;
        int currentScene = 0;
        Clip? scene = null;
        int sceneCount = 0, moveCount = 0;

        foreach (string rawLine in body.Split('\n'))
        {
            string line = rawLine.TrimEnd();
            Match levelBranch = LevelBranchPattern().Match(line);
            if (levelBranch.Success) { currentLevel = int.Parse(levelBranch.Groups[1].Value); scene = null; }
            Match sceneBranch = SceneBranchPattern().Match(line);
            if (sceneBranch.Success) { currentScene = int.Parse(sceneBranch.Groups[1].Value); scene = null; }

            Match startMatch = SceneStartPattern().Match(line);
            Match endMatch = SceneEndPattern().Match(line);
            if (startMatch.Success && currentLevel > 0)
            {
                if (LuaValues.Resolve(startMatch.Groups[1].Value, symbols) is not { } sceneStart)
                {
                    warnings.Add($"L{currentLevel} S{currentScene}: sceneStart '{startMatch.Groups[1].Value.Trim()}' " +
                                 "could not be resolved to a frame - scene skipped");
                    scene = null;
                    continue;
                }
                scene = new Clip
                {
                    Name = $"L{currentLevel} S{currentScene}",
                    Description = levelDefs.TryGetValue(currentLevel, out GameLevel? lvl) ? lvl.Title : "",
                    StartFrame = sceneStart,
                    EndFrame = sceneStart, // corrected by sceneEnd below
                };
                project.Clips.Add(scene);
                sceneCount++;
                if (levelDefs.TryGetValue(currentLevel, out GameLevel? level))
                    level.SceneIds.Add(scene.Id);
            }
            if (endMatch.Success && scene != null)
            {
                if (LuaValues.Resolve(endMatch.Groups[1].Value, symbols) is { } sceneEnd) scene.EndFrame = sceneEnd;
                else warnings.Add($"L{currentLevel} S{currentScene}: sceneEnd '{endMatch.Groups[1].Value.Trim()}' " +
                                  "could not be resolved to a frame - scene end left at its start");
            }

            Match move = MovePattern().Match(line);
            if (move.Success && scene != null)
            {
                int start = int.Parse(move.Groups[1].Value);
                int end = int.Parse(move.Groups[2].Value);
                string token = move.Groups[3].Value;
                int deathIndex = int.Parse(move.Groups[4].Value);
                string? altToken = move.Groups[5].Success ? move.Groups[5].Value : null;

                if (!TryParseInput(token, out InputKind input))
                {
                    warnings.Add($"L{currentLevel} S{currentScene}: unknown input '{token}' at frame {start} - skipped");
                    continue;
                }
                InputKind? alt = null;
                if (altToken != null)
                {
                    if (TryParseInput(altToken, out InputKind parsedAlt)) alt = parsedAlt;
                    else warnings.Add($"L{currentLevel} S{currentScene}: unknown alt input '{altToken}' at frame {start} - ignored");
                }

                var marker = new InteractionMarker
                {
                    Frame = start,
                    Input = input,
                    AltInput = alt,
                    DeathClipId = deathIndex > 0 && deathScenes.TryGetValue(deathIndex, out Guid deathId) ? deathId : null,
                    // Death# 0 on a normal move is a deliberate authoring choice
                    // (the scene itself shows the failure); preserve it.
                    ExplicitNoDeath = deathIndex == 0 && input != InputKind.Skip,
                };
                // Scripts write standard windows as {start, start+base}; only
                // keep an explicit end when the window is non-standard.
                if (end != start + project.BaseWindowFrames)
                    marker.EndFrameOverride = end;
                if (deathIndex > 0 && !deathScenes.ContainsKey(deathIndex))
                    warnings.Add($"L{currentLevel} S{currentScene}: move at {start} references missing Death[{deathIndex}]");
                scene.Interactions.Add(marker);
                moveCount++;
            }
            else if (!move.Success && scene != null &&
                     line.Contains("move[", StringComparison.Ordinal) &&
                     line.Contains('{') && !line.TrimStart().StartsWith("--", StringComparison.Ordinal))
            {
                // A move-looking line the pattern rejected - usually a script
                // defect like a missing Death# ({s, e, LEFT, }). These are the
                // latent bugs this importer exists to surface.
                warnings.Add($"L{currentLevel} S{currentScene}: malformed move line skipped: {line.Trim()}");
            }
        }

        // A script that sets RelativeFrames makes a level's start frame the base
        // every scene and move is measured from (main.singe:6403). This importer
        // reads those numbers as absolute, so the intro check — which compares
        // the two — would be comparing different units. Leave it alone and say so.
        bool relativeFrames = RelativeFramesPattern().IsMatch(scriptText);
        if (relativeFrames)
            warnings.Add("This script sets RelativeFrames = true, so its scene and move frames are " +
                         "offsets from each level's start, not absolute frames. They have been imported " +
                         "as-is and will need checking against the video.");

        // Adopt levels in numeric order and verify counts.
        foreach ((int number, GameLevel level) in levelDefs.OrderBy(kv => kv.Key))
        {
            project.Levels.Add(level);
            if (level.SceneIds.Count == 0)
                warnings.Add($"Level[{number}] '{level.Title}' has no scenes in setupMoves");

            if (relativeFrames) continue;
            int? firstScene = level.SceneIds
                .Select(id => project.Clips.FirstOrDefault(c => c.Id == id))
                .Where(c => c != null)
                .Select(c => (int?)c!.StartFrame)
                .DefaultIfEmpty(null)
                .Min();
            if (LevelIntro.Explain(number, level.Title, level.StartFrame, level.IntroEndFrame, firstScene) is { } note)
            {
                warnings.Add(note);
                level.IntroEndFrame = LevelIntro.Correct(level.StartFrame, level.IntroEndFrame, firstScene);
            }
        }

        BuildStoryboard(project, deathScenes.Values.ToHashSet());
        return new Result(levelDefs.Count, sceneCount, moveCount, deaths, slotsFilled, warnings);
    }

    private static bool TryParseInput(string token, out InputKind input)
    {
        switch (token.ToUpperInvariant())
        {
            case "UP": input = InputKind.Up; return true;
            case "DOWN": input = InputKind.Down; return true;
            case "LEFT": input = InputKind.Left; return true;
            case "RIGHT": input = InputKind.Right; return true;
            case "BUTTON1" or "ACTION": input = InputKind.Button1; return true;
            case "BUTTON2": input = InputKind.Button2; return true;
            case "SKIP": input = InputKind.Skip; return true;
            case "WAY": input = InputKind.AnyDirection; return true;
            default: input = default; return false;
        }
    }

    /// <summary>Reuses an existing scene with the same range, otherwise creates one.</summary>
    private static Clip GetOrCreateScene(LdpProject project, int start, int end, string name)
    {
        Clip? existing = project.Clips.FirstOrDefault(c => c.StartFrame == start && c.EndFrame == end);
        if (existing != null) return existing;
        var scene = new Clip { Name = name, StartFrame = start, EndFrame = end };
        project.Clips.Add(scene);
        return scene;
    }

    /// <summary>
    /// Lays out the imported game on the storyboard: one row per level chained
    /// along the success path, death scenes beneath each level wired from the
    /// Death port (up to two distinct ones per scene).
    /// </summary>
    private static void BuildStoryboard(LdpProject project, HashSet<Guid> deathSceneIds)
    {
        StoryGraph graph = project.Graph;
        graph.Nodes.Clear();
        graph.Edges.Clear();

        var start = new StoryNode { Kind = NodeKind.Start, X = 40, Y = 60 };
        graph.Nodes.Add(start);

        const double nodeW = 190, nodeH = 118, gapX = 70, rowH = 320;
        Dictionary<Guid, StoryNode> deathNodes = [];
        StoryNode previous = start;

        for (int levelIndex = 0; levelIndex < project.Levels.Count; levelIndex++)
        {
            GameLevel level = project.Levels[levelIndex];
            double y = 60 + levelIndex * rowH;
            double x = 40 + nodeW + gapX;

            for (int s = 0; s < level.SceneIds.Count; s++)
            {
                Clip? scene = project.Clips.FirstOrDefault(c => c.Id == level.SceneIds[s]);
                if (scene == null) continue;

                var node = new StoryNode { Kind = NodeKind.Clip, ClipId = scene.Id, X = x, Y = y };
                graph.Nodes.Add(node);
                graph.Edges.Add(new StoryEdge
                {
                    FromNode = previous.Id,
                    FromPort = previous.Kind == NodeKind.Start ? PortKind.Out : PortKind.Success,
                    ToNode = node.Id,
                });

                // Wire up to two distinct deaths used by this scene's moves.
                foreach (Guid deathId in scene.Interactions
                             .Where(m => m.DeathClipId != null)
                             .Select(m => m.DeathClipId!.Value)
                             .Distinct()
                             .Take(2))
                {
                    if (!deathNodes.TryGetValue(deathId, out StoryNode? deathNode))
                    {
                        deathNode = new StoryNode
                        {
                            Kind = NodeKind.Clip,
                            ClipId = deathId,
                            X = x - 40,
                            Y = y + nodeH + 46,
                        };
                        deathNodes[deathId] = deathNode;
                        graph.Nodes.Add(deathNode);
                    }
                    graph.Edges.Add(new StoryEdge { FromNode = node.Id, FromPort = PortKind.Death, ToNode = deathNode.Id });
                }

                previous = node;
                x += nodeW + gapX;
            }
        }
    }
}

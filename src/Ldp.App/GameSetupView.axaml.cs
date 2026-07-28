using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Ldp.Project;
using System;
using System.Linq;

namespace Ldp.App;

/// <summary>
/// Assignment panel for the framework's non-game slots. Range slots take a
/// scene from the bin ("⟵ scene"); still slots take the frame currently shown
/// in the editor ("⟵ frame"). The exporter later reads these straight into
/// the script's Section 2 variables.
/// </summary>
public partial class GameSetupView : UserControl
{
    private LdpProject? _project;
    private Func<Clip?>? _selectedScene;
    private Func<int?>? _currentFrame;
    private IReadOnlyDictionary<string, string> _templateDefaults = new Dictionary<string, string>();

    /// <summary>Levels whose advanced row (replay, mirrors, intro) is open.
    /// Held by reference so it survives the panel rebuilds every edit triggers.</summary>
    private readonly HashSet<GameLevel> _expandedLevels = [];

    public event Action? SlotsChanged;
    public event Action<int>? GotoFrameRequested;

    /// <summary>Level structure changed — the scenes list badges and storyboard
    /// chips are now stale, and the project needs saving.</summary>
    public event Action? LevelsChanged;

    /// <summary>Supplies the paths the app is working with, for the FILE
    /// LOCATIONS section. Set by the window, which owns app-level settings.</summary>
    public Func<IReadOnlyList<(string Label, string Value, string Hint)>>? PathsProvider { get; set; }

    /// <summary>Cached picture for a scene, so a filled range slot shows what it
    /// actually is rather than a frame number. Null when none has been made yet.
    /// The window owns decoding and caching; this panel only draws.</summary>
    public Func<Clip, IImage?>? SceneThumbnail { get; set; }

    /// <summary>Cached picture of one global frame, for the single-frame slots.</summary>
    public Func<int, IImage?>? FrameThumbnail { get; set; }

    /// <summary>The author wants this scene's thumbnail taken from the frame the
    /// editor is currently showing.</summary>
    public event Action<Clip>? SetSceneThumbnailRequested;

    /// <summary>The author wants to point the project at a different Hypseus install.</summary>
    public event Action? RelocateHypseusRequested;

    public GameSetupView()
    {
        InitializeComponent();
    }

    public void SetProject(LdpProject? project, Func<Clip?> selectedScene, Func<int?> currentFrame,
                           IReadOnlyDictionary<string, string>? templateDefaults = null)
    {
        _project = project;
        _selectedScene = selectedScene;
        _currentFrame = currentFrame;
        if (templateDefaults != null) _templateDefaults = templateDefaults;
        Rebuild();
    }

    public void Refresh() => Rebuild();

    private void Rebuild()
    {
        // Every level edit rebuilds the whole page; without restoring the scroll
        // position the view would snap to the top on each one, and the LEVELS
        // section lives near the bottom.
        Vector offset = SetupScroll.Offset;
        SlotsPanel.Children.Clear();
        SummaryText.Text = "";
        if (_project == null) return;

        AddHeader("GAME INFO (written into the script)");
        SlotsPanel.Children.Add(TextRow("Game name", _project.Name,
            v => _project.Name = v,
            hint: "The internal title (singeSetGameName + README), e.g. \"Sonic the Hedgehog, The Movie\". Independent of the folder/file name."));
        // Source-material credits, kept next to the title they describe. All
        // three are optional and only reach the script header when filled in.
        SlotsPanel.Children.Add(TextRow("Studio", _project.Studio,
            v => _project.Studio = v.Trim(),
            placeholder: "Studio, production company, or channel",
            hint: "Who made the source material — not you. The author credit is the field below."));
        SlotsPanel.Children.Add(TextRow("Copyright", _project.Copyright,
            v => _project.Copyright = v.Trim(),
            placeholder: "© 2019 Twentieth Century Fox",
            hint: "Copyright line for the source material, written into the script header."));
        SlotsPanel.Children.Add(TextRow("URL", _project.Url,
            v => _project.Url = v.Trim(),
            placeholder: "IMDB, TheTVDB, YouTube, other website..",
            hint: "Where the source material can be looked up. Written into the script header."));
        SlotsPanel.Children.Add(TextRow("Game folder", _project.GameFolder,
            v => _project.GameFolder = LdpProject.SanitizeFolder(v),
            hint: "Folder + script/file base name (no spaces). Drives MYDIR and the exported file names."));
        SlotsPanel.Children.Add(TextRow("Author *", _project.Author,
            v => _project.Author = v, hint: "Required. Credited in the script header."));
        SlotsPanel.Children.Add(TextRow("Game version", _project.GameVersion,
            v => _project.GameVersion = v, hint: "Bump when you release script changes."));
        SlotsPanel.Children.Add(TextRow("Date *", _project.GameDate,
            v => _project.GameDate = v.Trim(),
            placeholder: DateTime.Now.ToString("yyyy-MM-dd"),
            hint: "Required, in YYYY-MM-DD form (e.g. 2026-07-14)."));
        SlotsPanel.Children.Add(TextRow("Synopsis", _project.Synopsis,
            v => _project.Synopsis = v, multiline: true, minHeight: 0,
            hint: "One or two sentences about the story (grows as you type)."));
        SlotsPanel.Children.Add(TextRow("Author notes", _project.AuthorNotes,
            v => _project.AuthorNotes = v, multiline: true,
            hint: "History, credits, install tips - free form, kept in the README."));
        SlotsPanel.Children.Add(FrameworkRow());
        SlotsPanel.Children.Add(ReadOnlyRow("Movie FPS",
            _project.Videos.Count == 0
                ? "— add a video —"
                : $"{_project.Videos[0].Fps:F3} (auto-detected; all videos must match)"));

        AddHeader("FILE LOCATIONS");
        SlotsPanel.Children.Add(PathsBlock());

        AddSection("ATTRACT & TITLE", SlotCatalog.Ranges
            .Where(r => r.Slot is RangeSlot.Title or RangeSlot.Intro01 or RangeSlot.Intro02
                        or RangeSlot.Intro03 or RangeSlot.IntroGame)
            .Select(RangeRow).ToList());

        AddSection("SYSTEM VIDEOS", SlotCatalog.Ranges
            .Where(r => r.Slot is RangeSlot.Continue or RangeSlot.LevelClear or RangeSlot.GetReady
                        or RangeSlot.SupDeath or RangeSlot.GameOver or RangeSlot.GameOverAlt
                        or RangeSlot.NewHighScore or RangeSlot.EnterHighScore or RangeSlot.Rankings
                        or RangeSlot.Map)
            .Select(RangeRow).ToList());

        AddSection("MENU & STILL FRAMES", SlotCatalog.Stills
            .Where(s => s.Slot is not (StillSlot.DifficultyEasy or StillSlot.DifficultyNormal
                        or StillSlot.DifficultyHard or StillSlot.DifficultyExtreme))
            .Select(StillRow).ToList());

        AddSection("DIFFICULTY SELECT FRAMES", SlotCatalog.Stills
            .Where(s => s.Slot is StillSlot.DifficultyEasy or StillSlot.DifficultyNormal
                        or StillSlot.DifficultyHard or StillSlot.DifficultyExtreme)
            .Select(StillRow).ToList());

        AddHeader("LEVELS (the play order the framework runs)");
        SlotsPanel.Children.Add(LevelsBlock());

        AddHeader("SCORING (leave blank to keep the shown default)");
        foreach (ScoringCatalog.Entry entry in ScoringCatalog.Entries)
            SlotsPanel.Children.Add(ScoringRow(entry));

        AddHeader("LANGUAGE TRACKS");
        SlotsPanel.Children.Add(LanguagesBlock());

        UpdateSummary();
        Dispatcher.UIThread.Post(() => SetupScroll.Offset = offset, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Where the app is reading and writing. The Hypseus install in particular
    /// is chosen once in the New Project wizard and then never shown again,
    /// which leaves an author with no way to check it — or to notice it has
    /// gone stale after moving the install.
    /// </summary>
    private Control PathsBlock()
    {
        var panel = new StackPanel { Spacing = 2 };
        IReadOnlyList<(string Label, string Value, string Hint)> paths =
            PathsProvider?.Invoke() ?? [];
        if (paths.Count == 0)
        {
            panel.Children.Add(Faint("No paths to show yet."));
            return panel;
        }

        foreach ((string label, string value, string hint) in paths)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("190,*"), Margin = new Thickness(0, 1) };
            TextBlock name = Faint(label);
            name.FontSize = 13;
            name.Foreground = (IBrush?)this.FindResource("FgPrimary");
            if (hint.Length > 0) ToolTip.SetTip(name, hint);
            row.Children.Add(name);

            bool missing = value.Length == 0;
            var path = new SelectableTextBlock
            {
                Text = missing ? "— not set —" : value,
                Foreground = (IBrush?)this.FindResource(missing ? "PortDeath" : "FgMuted"),
                FontFamily = new FontFamily("Consolas,monospace"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            if (!missing)
            {
                ToolTip.SetTip(path, "Select to copy, or right-click → Copy");
                AddCopyMenu(path, value);
            }
            Grid.SetColumn(path, 1);
            row.Children.Add(path);
            panel.Children.Add(row);
        }

        var relocate = new Button
        {
            Content = "Change Hypseus folder…",
            Focusable = false,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
        };
        ToolTip.SetTip(relocate, "Point this project at a different Hypseus Singe install (after moving or reinstalling it).");
        relocate.Click += (_, _) => RelocateHypseusRequested?.Invoke();
        panel.Children.Add(relocate);
        return panel;
    }

    // ---------- Levels ----------

    /// <summary>
    /// Play-order editor. Levels are what the framework actually runs: a scene
    /// only reaches the exported script once a level holds it, no matter how it
    /// is wired on the storyboard. Selection lives in the SCENES list (which
    /// already multi-selects), so this panel owns structure — order, titles,
    /// intros — rather than picking.
    /// </summary>
    private Control LevelsBlock()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "A scene reaches the exported game only when a level holds it — storyboard wires alone " +
                   "aren't enough. Select scenes in the SCENES list (Ctrl+click for several), then " +
                   "right-click → Assign to Level.",
            Foreground = (IBrush?)this.FindResource("FgFaint"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var add = new Button { Content = "＋ Add Level", Focusable = false };
        add.Click += (_, _) =>
        {
            GameLevel created = _project!.AddLevel();
            Commit();
            SummaryText.Text = $"Added '{created.Title}' — now assign scenes to it.";
        };
        bar.Children.Add(add);

        var build = new Button { Content = "⚙ Build Level from Storyboard", Focusable = false };
        ToolTip.SetTip(build,
            "Creates one level holding every scene on the storyboard's success chain, in order. " +
            "Death scenes hang off Death ports, so they are never swept in. Split it afterwards with ＋ Add Level.");
        build.Click += (_, _) =>
        {
            if (_project!.BuildLevelFromStoryboard($"LEVEL {_project.Levels.Count + 1}") is not { } level)
            {
                SummaryText.Text = "The storyboard has no chained scenes to build a level from.";
                return;
            }
            Commit();
            SummaryText.Text = $"Built '{level.Title}' from the storyboard — {level.SceneIds.Count} scenes in chain order.";
        };
        bar.Children.Add(build);
        panel.Children.Add(bar);

        if (_project!.Levels.Count == 0)
            panel.Children.Add(Notice(
                "No levels yet — the exported script has no playable content (finalstage = 0, empty setupMoves).",
                "PortDeath"));

        for (int i = 0; i < _project.Levels.Count; i++)
            panel.Children.Add(LevelCard(_project.Levels[i], i));

        List<Clip> stranded = _project.UnassignedChainScenes();
        if (stranded.Count > 0)
            panel.Children.Add(Notice(
                $"⚠ {stranded.Count} scene(s) on the storyboard's success chain belong to no level and will not " +
                $"be exported: {string.Join(", ", stranded.Take(4).Select(c => c.Name))}" +
                (stranded.Count > 4 ? ", …" : ""),
                "AccentAmber"));

        return panel;
    }

    /// <summary>
    /// Applies a level edit. The window owns the response — persist, repaint
    /// this panel, refresh the scene badges and storyboard chips — so a change
    /// never repaints from two places and fights itself.
    /// </summary>
    private void Commit() => LevelsChanged?.Invoke();

    /// <summary>
    /// Commit from inside a LostFocus handler. Deferred a dispatcher pass so the
    /// panel is not rebuilt mid focus-transition, which would destroy the very
    /// control the focus is moving to.
    /// </summary>
    private void CommitDeferred() => Dispatcher.UIThread.Post(Commit);

    private Control LevelCard(GameLevel level, int index)
    {
        LdpProject project = _project!;
        var stack = new StackPanel { Spacing = 3 };
        var card = new Border
        {
            Background = (IBrush?)this.FindResource("BgNode"),
            BorderBrush = (IBrush?)this.FindResource("Divider"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6),
            Margin = new Thickness(0, 2),
            Child = stack,
        };

        // ---- Header: L# · title · reorder · clipboard · delete ----
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        header.Children.Add(Chip($"L{index + 1}", "Accent"));

        var title = new TextBox
        {
            Text = level.Title,
            PlaceholderText = "Level title (shown in game)",
            FontSize = 13,
            Margin = new Thickness(8, 0),
        };
        title.LostFocus += (_, _) =>
        {
            string text = (title.Text ?? "").Trim();
            if (text == level.Title) return;
            level.Title = text;
            CommitDeferred(); // the storyboard band label carries this title too
        };
        Grid.SetColumn(title, 1);
        header.Children.Add(title);

        int sceneCount = level.SceneIds.Count;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        buttons.Children.Add(SmallButton("▲", "Move this level earlier in the play order",
            index > 0, () => { project.MoveLevel(level, -1); Commit(); }));
        buttons.Children.Add(SmallButton("▼", "Move this level later in the play order",
            index < project.Levels.Count - 1, () => { project.MoveLevel(level, +1); Commit(); }));
        buttons.Children.Add(SmallButton("✂", $"Cut all {sceneCount} scene(s) — paste moves them into another level",
            sceneCount > 0, () => { SceneClipboard.Set(level.SceneIds, cut: true); Commit(); }));
        buttons.Children.Add(SmallButton("⧉", $"Copy all {sceneCount} scene(s) — paste duplicates them over the same frames",
            sceneCount > 0, () => { SceneClipboard.Set(level.SceneIds, cut: false); Commit(); }));
        buttons.Children.Add(SmallButton("📋", SceneClipboard.PasteLabel + " into this level",
            SceneClipboard.HasContent, () => PasteInto(level)));
        buttons.Children.Add(SmallButton("✕", "Delete this level (its scenes stay in the project, unassigned)",
            true, () =>
            {
                project.Levels.Remove(level);
                _expandedLevels.Remove(level);
                Commit();
                SummaryText.Text = $"Level deleted — its {sceneCount} scene(s) are now unassigned.";
            }));
        Grid.SetColumn(buttons, 2);
        header.Children.Add(buttons);
        stack.Children.Add(header);

        // ---- Info line ----
        bool expanded = _expandedLevels.Contains(level);
        var info = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(2, 1, 0, 0) };
        info.Children.Add(FrameLink($"starts {level.StartFrame:D6}", level.StartFrame));
        if (level.HasIntro) info.Children.Add(FrameLink($"intro ends {level.IntroEndFrame:D6}", level.IntroEndFrame));
        info.Children.Add(Faint($"{sceneCount} scene{(sceneCount == 1 ? "" : "s")}"));
        // Visible without expanding: a non-default death behaviour rewrites the
        // play order, so it must not hide behind "▸ more".
        if (level.Replay == GameLevel.DefaultReplay)
        {
            info.Children.Add(Faint(ReplayCatalog.Display(level.Replay)));
        }
        else
        {
            TextBlock odd = Faint($"⚠ {ReplayCatalog.Display(level.Replay)} — open ▸ more to reset");
            odd.Foreground = (IBrush?)this.FindResource("PortDeath");
            info.Children.Add(odd);
        }
        var more = new Button
        {
            Content = expanded ? "▾ less" : "▸ more",
            Focusable = false,
            FontSize = 11,
            Padding = new Thickness(6, 0),
        };
        more.Click += (_, _) =>
        {
            if (!_expandedLevels.Remove(level)) _expandedLevels.Add(level);
            Rebuild();
        };
        info.Children.Add(more);
        stack.Children.Add(info);

        if (expanded) stack.Children.Add(LevelAdvanced(level));

        // ---- Scene rows ----
        if (sceneCount == 0)
        {
            stack.Children.Add(Faint("No scenes yet — select them in the SCENES list, then right-click → Assign to Level."));
        }
        else
        {
            for (int s = 0; s < sceneCount; s++)
                stack.Children.Add(LevelSceneRow(level, s));
        }

        return card;
    }

    /// <summary>Replay behavior, mirror offsets and the intro passage — the
    /// Level[] fields most games never touch.</summary>
    private Control LevelAdvanced(GameLevel level)
    {
        var panel = new StackPanel
        {
            Spacing = 3,
            Margin = new Thickness(2, 4, 0, 4),
        };

        // Death behaviour is deliberately NOT editable here. Anything but the
        // -1 loop default feeds the framework's LvlOrder requeue arithmetic and
        // reorders the game, which is far outside what this app is for — so the
        // only thing offered is a way to clear a value that shouldn't be there
        // (imported from a hand-written script, or left by an older build).
        var deathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,Auto") };
        deathRow.Children.Add(Faint("On death"));
        bool defaultReplay = level.Replay == GameLevel.DefaultReplay;
        var deathValue = new TextBlock
        {
            Text = ReplayCatalog.Display(level.Replay) + (defaultReplay ? "" : "  ⚠ not the default"),
            Foreground = (IBrush?)this.FindResource(defaultReplay ? "FgFaint" : "PortDeath"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(deathValue, 1);
        deathRow.Children.Add(deathValue);
        if (!defaultReplay)
        {
            var reset = new Button { Content = "Reset to default", Focusable = false, FontSize = 11 };
            ToolTip.SetTip(reset, "Put this level back on \"replay until passed\", the behaviour a game should ship with.");
            reset.Click += (_, _) => { level.Replay = GameLevel.DefaultReplay; Commit(); };
            Grid.SetColumn(reset, 2);
            deathRow.Children.Add(reset);
        }
        panel.Children.Add(deathRow);

        panel.Children.Add(NumberRow("Intro starts at frame", level.StartFrame, v =>
        {
            level.StartFrame = v;
            if (level.IntroEndFrame <= v) level.IntroEndFrame = v + 1;
        }, "Global frame the level begins on. Leave it matching the first scene unless the level opens with a skippable intro passage."));
        panel.Children.Add(NumberRow("Intro ends at frame", level.IntroEndFrame, v => level.IntroEndFrame = v,
            "End of the skippable intro. Keep it at start+1 for no intro — the level then follows its first scene automatically."));
        panel.Children.Add(NumberRow("Mirror offset", level.Mirror, v => level.Mirror = v,
            "Frame offset of an exact mirrored copy of this level's video (0 = none)."));
        panel.Children.Add(NumberRow("Death mirror offset", level.DeathMirror, v => level.DeathMirror = v,
            "Frame offset of mirrored death videos (0 = none)."));
        return panel;
    }

    private Control LevelSceneRow(GameLevel level, int sceneIndex)
    {
        LdpProject project = _project!;
        Guid id = level.SceneIds[sceneIndex];
        Clip? clip = project.Clips.Find(c => c.Id == id);

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            Margin = new Thickness(12, 1, 0, 1),
        };
        // A scene with no moves is fatal to the framework, not just empty: it
        // clears the move table before setupMoves and then indexes it unguarded,
        // and the scene can never complete. Flag it where the author assigns it.
        bool noMoves = clip is { Interactions.Count: 0 };
        row.Children.Add(Chip($"S{sceneIndex + 1}", clip == null || noMoves ? "PortDeath" : "AccentAmber"));

        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0),
        };
        label.Children.Add(new TextBlock
        {
            Text = clip?.Name ?? "(scene missing from the project)",
            Foreground = (IBrush?)this.FindResource(clip == null ? "PortDeath" : "FgPrimary"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (noMoves)
        {
            var flag = new TextBlock
            {
                Text = "⚠ no moves",
                Foreground = (IBrush?)this.FindResource("PortDeath"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(flag,
                "The framework crashes on a level scene with no moves, and the scene can never complete. " +
                "Add at least one move - a Skip move covers a passage with no action.");
            label.Children.Add(flag);
        }
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        if (clip != null)
        {
            Control range = FrameLink($"{clip.StartFrame:D6}–{clip.EndFrame:D6}", clip.StartFrame);
            range.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(range, 2);
            row.Children.Add(range);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        buttons.Children.Add(SmallButton("▲", "Play this scene earlier in the level",
            sceneIndex > 0, () => { project.MoveSceneInLevel(level, id, -1); Commit(); }));
        buttons.Children.Add(SmallButton("▼", "Play this scene later in the level",
            sceneIndex < level.SceneIds.Count - 1, () => { project.MoveSceneInLevel(level, id, +1); Commit(); }));
        buttons.Children.Add(SmallButton("✕", "Remove from this level (the scene itself is kept)",
            true, () => { project.RemoveFromLevels([id]); Commit(); }));
        Grid.SetColumn(buttons, 3);
        row.Children.Add(buttons);
        return row;
    }

    /// <summary>
    /// Cut scenes MOVE into the level; copied scenes are DUPLICATED over the
    /// same frames, so a passage can play again in a later level without being
    /// stolen from the one that already owns it.
    /// </summary>
    private void PasteInto(GameLevel level)
    {
        if (_project == null || !SceneClipboard.HasContent) return;

        List<Guid> ids = [];
        foreach (Guid id in SceneClipboard.Ids)
        {
            if (_project.Clips.Find(c => c.Id == id) is not { } source) continue;
            if (SceneClipboard.IsCut)
            {
                ids.Add(id);
                continue;
            }
            Clip copy = source.Duplicate();
            _project.Clips.Add(copy);
            ids.Add(copy.Id);
        }
        if (ids.Count == 0) return;

        bool moved = SceneClipboard.IsCut;
        _project.AssignToLevel(level, ids);
        if (moved) SceneClipboard.Clear();
        Commit();
        SummaryText.Text = moved
            ? $"Moved {ids.Count} scene(s) into '{level.Title}'."
            : $"Copied {ids.Count} scene(s) into '{level.Title}' as duplicates over the same frames.";
    }

    // ---------- Small building blocks ----------

    private Border Chip(string text, string brushKey) => new()
    {
        Background = (IBrush?)this.FindResource(brushKey),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            Foreground = (IBrush?)this.FindResource("BgCanvas"),
            FontFamily = new FontFamily("Consolas,monospace"),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
        },
    };

    private Button SmallButton(string glyph, string tip, bool enabled, Action action)
    {
        var button = new Button
        {
            Content = glyph,
            Focusable = false,
            IsEnabled = enabled,
            FontSize = 12,
            Width = 30,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private TextBlock Faint(string text) => new()
    {
        Text = text,
        Foreground = (IBrush?)this.FindResource("FgFaint"),
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
    };

    private Border Notice(string text, string brushKey) => new()
    {
        BorderBrush = (IBrush?)this.FindResource(brushKey),
        BorderThickness = new Thickness(3, 0, 0, 0),
        Background = (IBrush?)this.FindResource("BgPanel"),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 5),
        Child = new TextBlock
        {
            Text = text,
            Foreground = (IBrush?)this.FindResource(brushKey),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        },
    };

    /// <summary>A frame number that jumps the editor there when clicked.</summary>
    private TextBlock FrameLink(string text, int frame)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = (IBrush?)this.FindResource("FrameText"),
            FontFamily = new FontFamily("Consolas,monospace"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        ToolTip.SetTip(block, "Click to view this frame · right-click to copy the number");
        block.PointerPressed += (_, _) => GotoFrameRequested?.Invoke(frame);
        AddCopyMenu(block, frame.ToString());
        return block;
    }

    /// <summary>
    /// Right-click "Copy" on a read-only value. Left-click already jumps the
    /// editor to a frame, so selection can't be used for copying — but the
    /// numbers on this page are exactly what an author needs to lift out and
    /// paste somewhere else.
    /// </summary>
    private void AddCopyMenu(Control target, string text)
    {
        var item = new MenuItem { Header = $"Copy  {text}" };
        item.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetValueAsync(Avalonia.Input.DataFormat.Text, text);
            SummaryText.Text = $"Copied {text} to the clipboard.";
        };
        var menu = new ContextMenu();
        menu.Items.Add(item);
        target.ContextMenu = menu;
    }

    private Control NumberRow(string label, int value, Action<int> commit, string hint)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,110,*") };
        var name = Faint(label);
        grid.Children.Add(name);

        var box = new TextBox
        {
            Text = value.ToString(),
            FontFamily = new FontFamily("Consolas,monospace"),
            FontSize = 12,
        };
        box.LostFocus += (_, _) =>
        {
            if (!int.TryParse((box.Text ?? "").Trim(), out int parsed))
            {
                box.Text = value.ToString(); // reject non-numeric
                return;
            }
            if (parsed == value) return;
            commit(parsed);
            CommitDeferred();
        };
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        TextBlock hintBlock = Faint(hint);
        hintBlock.Margin = new Thickness(10, 0, 0, 0);
        hintBlock.TextTrimming = TextTrimming.CharacterEllipsis;
        hintBlock.TextWrapping = TextWrapping.NoWrap;
        ToolTip.SetTip(hintBlock, hint);
        Grid.SetColumn(hintBlock, 2);
        grid.Children.Add(hintBlock);
        return grid;
    }

    private Control ScoringRow(ScoringCatalog.Entry entry)
    {
        string? current = _project!.ScriptValues.TryGetValue(entry.LuaName, out string? v) ? v : null;
        string placeholder = _templateDefaults.TryGetValue(entry.LuaName, out string? d)
            ? $"default {d}" : "default";

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("190,120,*"), Margin = new Thickness(0, 2) };
        var nameBlock = new TextBlock
        {
            Text = entry.Display,
            Foreground = (IBrush?)this.FindResource("FgPrimary"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(nameBlock, $"{entry.Hint}  (Lua: {entry.LuaName})");
        grid.Children.Add(nameBlock);

        var box = new TextBox
        {
            Text = current ?? "",
            PlaceholderText = placeholder,
            FontFamily = new FontFamily("Consolas,monospace"),
            FontSize = 13,
        };
        box.LostFocus += (_, _) =>
        {
            string text = (box.Text ?? "").Trim();
            if (text.Length == 0)
            {
                if (_project!.ScriptValues.Remove(entry.LuaName)) { SlotsChanged?.Invoke(); }
            }
            else if (int.TryParse(text, out int _))
            {
                if (!_project!.ScriptValues.TryGetValue(entry.LuaName, out string? existing) || existing != text)
                {
                    _project.ScriptValues[entry.LuaName] = text;
                    SlotsChanged?.Invoke();
                }
            }
            else
            {
                box.Text = current ?? ""; // reject non-numeric
            }
        };
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);

        var luaHint = new TextBlock
        {
            Text = entry.Hint,
            Foreground = (IBrush?)this.FindResource("FgFaint"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(luaHint, 2);
        grid.Children.Add(luaHint);
        return grid;
    }

    private Control LanguagesBlock()
    {
        var panel = new StackPanel { Spacing = 4 };

        // Ensure at least English so playback/export always has a primary track.
        if (_project!.Languages.Count == 0)
            _project.Languages.Add(new GameLanguage { Name = "English", Suffix = "" });

        panel.Children.Add(new TextBlock
        {
            Text = "Name shown in the menu, and the .ogg suffix (primary track = empty, e.g. main.ogg; \"-fre\" → main-fre.ogg).",
            Foreground = (IBrush?)this.FindResource("FgFaint"),
            FontSize = 11,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        for (int i = 0; i < _project.Languages.Count; i++)
        {
            GameLanguage lang = _project.Languages[i];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,140,36"), Margin = new Thickness(0, 1) };

            var nameBox = new TextBox { Text = lang.Name, PlaceholderText = "Language name", FontSize = 13 };
            nameBox.LostFocus += (_, _) => { lang.Name = nameBox.Text ?? ""; SlotsChanged?.Invoke(); };
            row.Children.Add(nameBox);

            var suffixBox = new TextBox
            {
                Text = lang.Suffix,
                PlaceholderText = "(suffix)",
                FontFamily = new FontFamily("Consolas,monospace"),
                FontSize = 13,
            };
            suffixBox.LostFocus += (_, _) => { lang.Suffix = suffixBox.Text ?? ""; SlotsChanged?.Invoke(); };
            Grid.SetColumn(suffixBox, 1);
            row.Children.Add(suffixBox);

            GameLanguage captured = lang;
            var remove = new Button { Content = "✕", Focusable = false, Width = 32 };
            remove.Click += (_, _) =>
            {
                if (_project!.Languages.Count <= 1) return; // keep at least one
                _project.Languages.Remove(captured);
                Rebuild();
                SlotsChanged?.Invoke();
            };
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            panel.Children.Add(row);
        }

        var add = new Button { Content = "＋ Add language", Focusable = false, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        add.Click += (_, _) =>
        {
            _project!.Languages.Add(new GameLanguage { Name = "New Language", Suffix = "-lang" });
            Rebuild();
            SlotsChanged?.Invoke();
        };
        panel.Children.Add(add);
        return panel;
    }

    private void UpdateSummary()
    {
        if (_project == null) return;
        int requiredTotal = SlotCatalog.Ranges.Count(r => r.Required) + SlotCatalog.Stills.Count(s => s.Required);
        int requiredFilled =
            SlotCatalog.Ranges.Count(r => r.Required && _project.Slots.Ranges.ContainsKey(r.Slot)) +
            SlotCatalog.Stills.Count(s => s.Required && _project.Slots.Stills.ContainsKey(s.Slot));
        // Level state sits next to the slot count because "no levels" is the one
        // setup gap that makes the exported script unplayable outright.
        string levels = _project.Levels.Count == 0
            ? "⚠ no levels — nothing playable"
            : $"{_project.Levels.Count} level(s), {_project.Levels.Sum(l => l.SceneIds.Count)} scenes";
        SummaryText.Text = $"{requiredFilled}/{requiredTotal} required slots filled · {levels}";
    }

    private void AddHeader(string text)
    {
        SlotsPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = (IBrush?)this.FindResource("FgFaint"),
            FontSize = 11,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 14, 0, 4),
        });
    }

    private Control RangeRow(SlotCatalog.RangeInfo info)
    {
        Clip? assigned = _project!.Slots.Ranges.TryGetValue(info.Slot, out Guid id)
            ? _project.Clips.FirstOrDefault(c => c.Id == id)
            : null;

        string valueText = assigned != null
            ? $"{assigned.Name}  ({assigned.StartFrame}–{assigned.EndFrame})"
            : info.Required ? "— required —" : "—";

        return Row(info.Display, info.Hint, valueText,
            assigned == null && info.Required,
            gotoFrame: assigned?.StartFrame,
            assignLabel: "⟵ scene",
            onAssign: () =>
            {
                if (_selectedScene?.Invoke() is not { } scene) return "Select a scene in the bin first.";
                _project.Slots.Ranges[info.Slot] = scene.Id;
                return null;
            },
            onClear: () => _project.Slots.Ranges.Remove(info.Slot),
            thumbnail: assigned != null ? SceneThumbnail?.Invoke(assigned) : null,
            // A scene is a passage, so which frame stands for it is a choice —
            // the same one the scenes bin offers with its own camera button.
            onRecapture: assigned != null ? () => SetSceneThumbnailRequested?.Invoke(assigned) : null);
    }

    private Control StillRow(SlotCatalog.StillInfo info)
    {
        int? frame = _project!.Slots.Stills.TryGetValue(info.Slot, out int f) ? f : null;
        string valueText = frame?.ToString("D6") ?? (info.Required ? "— required —" : "—");

        return Row(info.Display, info.Hint, valueText,
            frame == null && info.Required,
            gotoFrame: frame,
            assignLabel: "⟵ frame",
            onAssign: () =>
            {
                if (_currentFrame?.Invoke() is not { } current) return "Open a video and jog to the frame first.";
                _project.Slots.Stills[info.Slot] = current;
                return null;
            },
            onClear: () => _project.Slots.Stills.Remove(info.Slot),
            // A still slot IS one frame, so there is nothing to choose — the
            // picture is simply that frame, and no camera button is offered.
            thumbnail: frame is { } shown ? FrameThumbnail?.Invoke(shown) : null);
    }

    private Control TextRow(string label, string value, Action<string> commit,
                            bool multiline = false, string hint = "", string placeholder = "",
                            int minHeight = 84)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("190,*"),
            Margin = new Thickness(0, 2),
        };
        var nameBlock = new TextBlock
        {
            Text = label,
            Foreground = (IBrush?)this.FindResource("FgPrimary"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
        };
        if (!string.IsNullOrEmpty(hint)) ToolTip.SetTip(nameBlock, hint);
        grid.Children.Add(nameBlock);

        var box = new TextBox
        {
            Text = value,
            PlaceholderText = placeholder,
            FontSize = 13,
            AcceptsReturn = multiline,
            // Multiline boxes grow with their content (no fixed height); the
            // min sets where they start.
            MinHeight = multiline ? minHeight : 0,
            TextWrapping = multiline ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap,
        };
        box.LostFocus += (_, _) =>
        {
            string text = box.Text ?? "";
            if (text == value) return;
            commit(text);
            UpdateSummary();
            SlotsChanged?.Invoke();
        };
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        return grid;
    }

    private sealed record FrameworkChoice(GameFramework Value, string Label)
    {
        public override string ToString() => Label;
    }

    private Control FrameworkRow()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("190,*"),
            Margin = new Thickness(0, 2),
        };
        var nameBlock = new TextBlock
        {
            Text = "Framework",
            Foreground = (IBrush?)this.FindResource("FgPrimary"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(nameBlock,
            "Structure = the current standard (new games). Framework = the older pre-2025 global. " +
            "FrameworkKimmy = the stacked-move derivative for punishing games.");
        grid.Children.Add(nameBlock);

        List<FrameworkChoice> choices = GameFrameworkInfo.Ordered
            .Select(f => new FrameworkChoice(f, f.Display()))
            .ToList();
        var combo = new ComboBox
        {
            ItemsSource = choices,
            SelectedItem = choices.First(c => c.Value == _project!.Framework),
            FontSize = 13,
            MinWidth = 240,
        };
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is FrameworkChoice choice && choice.Value != _project!.Framework)
            {
                _project.Framework = choice.Value;
                SlotsChanged?.Invoke();
            }
        };
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);
        return grid;
    }

    private Control ReadOnlyRow(string label, string value)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("190,*"),
            Margin = new Thickness(0, 2),
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = (IBrush?)this.FindResource("FgPrimary"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var valueBlock = new TextBlock
        {
            Text = value,
            Foreground = (IBrush?)this.FindResource("FgMuted"),
            FontFamily = new FontFamily("Consolas,monospace"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(valueBlock);
        return grid;
    }

    /// <summary>Width of a slot's preview picture. Height follows the video's own
    /// aspect, so a 16:9 game gets 36px rows and a 4:3 game 48px — only where a
    /// slot is actually filled. Empty slots draw nothing and stay one line high,
    /// which keeps the long list of unassigned slots as quick to scan as before.</summary>
    private const int ThumbWidth = 64;

    private Control Row(string name, string hint, string valueText, bool missingRequired,
                        int? gotoFrame, string assignLabel, Func<string?> onAssign, Action onClear,
                        IImage? thumbnail = null, Action? onRecapture = null)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"190,{ThumbWidth + 10},*,86,40"),
        };

        var nameBlock = new TextBlock
        {
            Text = name,
            Foreground = (IBrush?)this.FindResource("FgPrimary"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!string.IsNullOrEmpty(hint)) ToolTip.SetTip(nameBlock, hint);
        grid.Children.Add(nameBlock);

        if (thumbnail != null)
        {
            var picture = new Image
            {
                Source = thumbnail,
                Width = ThumbWidth,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var frame = new Border
            {
                Child = picture,
                BorderBrush = (IBrush?)this.FindResource("Divider"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 10, 2),
            };
            if (gotoFrame is { } jump)
            {
                frame.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
                frame.PointerPressed += (_, _) => GotoFrameRequested?.Invoke(jump);
            }

            if (onRecapture != null)
            {
                // Sits on the picture itself rather than claiming another column,
                // so adding it costs the compact row layout nothing.
                var camera = new Button
                {
                    Content = "📷",
                    FontSize = 12,
                    Padding = new Thickness(3, 0),
                    MinWidth = 0,
                    MinHeight = 0,
                    Focusable = false,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 8, 0),
                    Opacity = 0.9,
                };
                ToolTip.SetTip(camera, "Use the frame the editor is showing as this scene's picture");
                camera.Click += (_, _) => onRecapture();
                var stack = new Panel();
                stack.Children.Add(frame);
                stack.Children.Add(camera);
                Grid.SetColumn(stack, 1);
                grid.Children.Add(stack);
            }
            else
            {
                Grid.SetColumn(frame, 1);
                grid.Children.Add(frame);
            }
        }

        var valueBlock = new TextBlock
        {
            Text = valueText,
            Foreground = (IBrush?)this.FindResource(missingRequired ? "PortDeath" : "AccentAmber"),
            FontFamily = new FontFamily("Consolas,monospace"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (gotoFrame is { } target)
        {
            valueBlock.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
            valueBlock.PointerPressed += (_, _) => GotoFrameRequested?.Invoke(target);
            ToolTip.SetTip(valueBlock, "Click to view this frame · right-click to copy the number");
            AddCopyMenu(valueBlock, target.ToString());
        }
        Grid.SetColumn(valueBlock, 2);
        grid.Children.Add(valueBlock);

        var assign = new Button { Content = assignLabel, Focusable = false, FontSize = 12, Width = 80 };
        assign.Click += (_, _) =>
        {
            string? error = onAssign();
            if (error != null) { SummaryText.Text = error; return; }
            Rebuild();
            SlotsChanged?.Invoke();
        };
        Grid.SetColumn(assign, 3);
        grid.Children.Add(assign);

        var clear = new Button { Content = "✕", Focusable = false, FontSize = 12, Width = 32 };
        clear.Click += (_, _) =>
        {
            onClear();
            Rebuild();
            SlotsChanged?.Invoke();
        };
        Grid.SetColumn(clear, 4);
        grid.Children.Add(clear);

        // The row's frame, not the row itself: hover and the unfilled-required
        // tint are both classes, so neither is a local value that would stop
        // the other from applying.
        var shell = new Border
        {
            Child = grid,
            Padding = new Thickness(8, 3),
            CornerRadius = new CornerRadius(3),
        };
        shell.Classes.Add("slotrow");
        if (missingRequired) shell.Classes.Add("missing");
        return shell;
    }

    /// <summary>
    /// One titled group of rows on a single card, the way the LEVELS section
    /// already works. Rows used to sit on the window background with nothing
    /// separating them, so a long column of them had no visible structure.
    /// </summary>
    private void AddSection(string header, IReadOnlyList<Control> rows)
    {
        AddHeader(header);
        if (rows.Count == 0) return;

        var stack = new StackPanel();
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
                stack.Children.Add(new Border
                {
                    Height = 1,
                    Background = (IBrush?)this.FindResource("Divider"),
                    Margin = new Thickness(8, 0),
                });
            stack.Children.Add(rows[i]);
        }

        SlotsPanel.Children.Add(new Border
        {
            Child = stack,
            Background = (IBrush?)this.FindResource("BgPanel"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
        });
    }
}

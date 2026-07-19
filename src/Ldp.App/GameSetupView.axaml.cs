using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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

    public event Action? SlotsChanged;
    public event Action<int>? GotoFrameRequested;

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
        SlotsPanel.Children.Clear();
        SummaryText.Text = "";
        if (_project == null) return;

        AddHeader("GAME INFO (written into the script)");
        SlotsPanel.Children.Add(TextRow("Game name", _project.Name,
            v => _project.Name = v,
            hint: "The internal title (singeSetGameName + README), e.g. \"Sonic the Hedgehog, The Movie\". Independent of the folder/file name."));
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

        AddHeader("ATTRACT & TITLE");
        foreach (SlotCatalog.RangeInfo info in SlotCatalog.Ranges.Where(r =>
                     r.Slot is RangeSlot.Title or RangeSlot.Intro01 or RangeSlot.Intro02
                     or RangeSlot.Intro03 or RangeSlot.IntroGame))
            SlotsPanel.Children.Add(RangeRow(info));

        AddHeader("SYSTEM VIDEOS");
        foreach (SlotCatalog.RangeInfo info in SlotCatalog.Ranges.Where(r =>
                     r.Slot is RangeSlot.Continue or RangeSlot.LevelClear or RangeSlot.GetReady
                     or RangeSlot.SupDeath or RangeSlot.GameOver or RangeSlot.GameOverAlt
                     or RangeSlot.NewHighScore or RangeSlot.EnterHighScore or RangeSlot.Rankings
                     or RangeSlot.Map))
            SlotsPanel.Children.Add(RangeRow(info));

        AddHeader("MENU & STILL FRAMES");
        foreach (SlotCatalog.StillInfo info in SlotCatalog.Stills.Where(s =>
                     s.Slot is not (StillSlot.DifficultyEasy or StillSlot.DifficultyNormal
                     or StillSlot.DifficultyHard or StillSlot.DifficultyExtreme)))
            SlotsPanel.Children.Add(StillRow(info));

        AddHeader("DIFFICULTY SELECT FRAMES");
        foreach (SlotCatalog.StillInfo info in SlotCatalog.Stills.Where(s =>
                     s.Slot is StillSlot.DifficultyEasy or StillSlot.DifficultyNormal
                     or StillSlot.DifficultyHard or StillSlot.DifficultyExtreme))
            SlotsPanel.Children.Add(StillRow(info));

        AddHeader("SCORING (leave blank to keep the shown default)");
        foreach (ScoringCatalog.Entry entry in ScoringCatalog.Entries)
            SlotsPanel.Children.Add(ScoringRow(entry));

        AddHeader("LANGUAGE TRACKS");
        SlotsPanel.Children.Add(LanguagesBlock());

        UpdateSummary();
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
            Watermark = placeholder,
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
            Text = "Name shown in the menu, and the .ogg suffix (primary track = empty, e.g. main.ogg; \"_russian\" → main_russian.ogg).",
            Foreground = (IBrush?)this.FindResource("FgFaint"),
            FontSize = 11,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        for (int i = 0; i < _project.Languages.Count; i++)
        {
            GameLanguage lang = _project.Languages[i];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,140,36"), Margin = new Thickness(0, 1) };

            var nameBox = new TextBox { Text = lang.Name, Watermark = "Language name", FontSize = 13 };
            nameBox.LostFocus += (_, _) => { lang.Name = nameBox.Text ?? ""; SlotsChanged?.Invoke(); };
            row.Children.Add(nameBox);

            var suffixBox = new TextBox
            {
                Text = lang.Suffix,
                Watermark = "(suffix)",
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
            _project!.Languages.Add(new GameLanguage { Name = "New Language", Suffix = "_lang" });
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
        SummaryText.Text = $"{requiredFilled}/{requiredTotal} required slots filled";
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
            onClear: () => _project.Slots.Ranges.Remove(info.Slot));
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
            onClear: () => _project.Slots.Stills.Remove(info.Slot));
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
            Watermark = placeholder,
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

    private Control Row(string name, string hint, string valueText, bool missingRequired,
                        int? gotoFrame, string assignLabel, Func<string?> onAssign, Action onClear)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("190,*,86,40"),
            Margin = new Thickness(0, 1),
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
            ToolTip.SetTip(valueBlock, "Click to view this frame");
        }
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(valueBlock);

        var assign = new Button { Content = assignLabel, Focusable = false, FontSize = 12, Width = 80 };
        assign.Click += (_, _) =>
        {
            string? error = onAssign();
            if (error != null) { SummaryText.Text = error; return; }
            Rebuild();
            SlotsChanged?.Invoke();
        };
        Grid.SetColumn(assign, 2);
        grid.Children.Add(assign);

        var clear = new Button { Content = "✕", Focusable = false, FontSize = 12, Width = 32 };
        clear.Click += (_, _) =>
        {
            onClear();
            Rebuild();
            SlotsChanged?.Invoke();
        };
        Grid.SetColumn(clear, 3);
        grid.Children.Add(clear);

        return grid;
    }
}

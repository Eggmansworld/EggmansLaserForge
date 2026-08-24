using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Ldp.Project;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ldp.App;

/// <summary>One player move, as the strip needs it.</summary>
/// <param name="Frame">Global frame the move is due at.</param>
/// <param name="InSelectedScene">Drawn bright; every other move is drawn faint.</param>
/// <param name="Violates">Breaks the spacing rules, so it wants attention.</param>
public sealed record TimelineMoveTick(int Frame, bool InSelectedScene, bool Violates);

/// <summary>
/// The whole of a video's frame line, drawn above the transport slider: what
/// every stretch is used for, and — the point of it — what nothing uses.
///
/// This replaced a strip that drew the SELECTED scene and nothing else, which
/// left one short band floating in a wide empty bar. The empty space carried no
/// meaning: it looked identical whether it held the rest of the game or footage
/// nobody had touched. Now the bar is always full, so a run of spare video is
/// something you SEE rather than something you have to go looking for.
///
/// Drawn rather than built from shapes because a feature-length game runs to
/// hundreds of scenes, and a Canvas of hundreds of Rectangles is rebuilt in full
/// on every scene change.
/// </summary>
public sealed class TimelineStrip : Control
{
    // Row geometry. Levels on top because they are the coarsest grouping, moves
    // at the bottom because they sit closest to the slider you nudge them with.
    private const double LevelRowTop = 0;
    private const double LevelRowHeight = 11;
    private const double CoverageTop = 15;
    private const double CoverageHeight = 26;
    private const double MoveRowTop = 44;
    private const double MoveRowHeight = 12;
    public const double PreferredHeight = MoveRowTop + MoveRowHeight;

    /// <summary>Nothing narrower than this is drawn, or a still frame on a
    /// 100,000-frame video would round away to nothing at all.</summary>
    private const double MinimumMark = 2;

    private TimelineMap _map = TimelineMap.Empty;
    private IReadOnlyList<TimelineMoveTick> _moves = [];
    private int _currentFrame;
    private Guid? _selectedClipId;
    private int? _markIn;
    private int? _markOut;
    private bool _dragging;
    private string _tip = "";

    // Prepared in Update, not in Render. The playhead repaints the whole strip
    // on every frame, so anything the frame number cannot change is worked out
    // once — otherwise playback rebuilds a FormattedText per level, 30 times a
    // second, to draw labels that never moved.
    private readonly List<TimelineSpan> _ordered = [];
    private readonly List<(TimelineLevelBand Band, FormattedText? Label, int Index)> _levelLabels = [];

    /// <summary>Raised when the user clicks or drags on the strip, with the global frame.</summary>
    public event EventHandler<int>? FrameRequested;

    public TimelineStrip()
    {
        Height = PreferredHeight;
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = true;
    }

    /// <summary>Hands the strip everything it draws, in one call, and repaints.</summary>
    public void Update(TimelineMap map, int currentFrame, Guid? selectedClipId,
                       IReadOnlyList<TimelineMoveTick> moves, int? markIn, int? markOut)
    {
        _map = map ?? TimelineMap.Empty;
        _currentFrame = currentFrame;
        _selectedClipId = selectedClipId;
        _moves = moves ?? [];
        _markIn = markIn;
        _markOut = markOut;

        // Longest first, so a short span sitting inside a long one — a slot
        // inside a level's footage, a death reused mid-scene — ends up on top
        // instead of being buried by whatever happened to be drawn after it.
        _ordered.Clear();
        _ordered.AddRange(_map.Spans);
        _ordered.Sort((x, y) => y.FrameCount.CompareTo(x.FrameCount));

        _levelLabels.Clear();
        for (int i = 0; i < _map.Levels.Count; i++)
        {
            TimelineLevelBand band = _map.Levels[i];
            _levelLabels.Add((band, MakeLabel($"{band.Number}. {band.Title}"), i));
        }

        InvalidateVisual();
    }

    private FormattedText MakeLabel(string text) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 9,
            Brush("FgPrimary") ?? Brushes.White);

    /// <summary>Moves only the playhead. Called on every frame change, so it
    /// avoids rebuilding anything the frame number cannot have altered.</summary>
    public void SetFrame(int currentFrame)
    {
        if (_currentFrame == currentFrame) return;
        _currentFrame = currentFrame;
        InvalidateVisual();
    }

    /// <summary>Moves only the In/Out brackets, leaving the map and moves alone.</summary>
    public void SetMarks(int? markIn, int? markOut)
    {
        if (_markIn == markIn && _markOut == markOut) return;
        _markIn = markIn;
        _markOut = markOut;
        InvalidateVisual();
    }

    private IBrush? Brush(string key) => this.FindResource(key) as IBrush;

    private double XFor(int frame)
    {
        int span = Math.Max(1, _map.TotalFrames - 1);
        double x = (frame - _map.FirstFrame) / (double)span * Bounds.Width;
        return Math.Clamp(x, 0, Bounds.Width);
    }

    private int FrameFor(double x)
    {
        if (Bounds.Width <= 1) return _map.FirstFrame;
        double t = Math.Clamp(x / Bounds.Width, 0, 1);
        return _map.FirstFrame + (int)Math.Round(t * Math.Max(0, _map.TotalFrames - 1));
    }

    /// <summary>A span's rectangle, never thinner than <see cref="MinimumMark"/>.</summary>
    private Rect RectFor(int startFrame, int endFrame, double top, double height)
    {
        double left = XFor(startFrame);
        // +1 because both ends are inclusive: a one-frame span still occupies a frame.
        double right = XFor(endFrame + 1);
        double width = Math.Max(MinimumMark, right - left);
        if (left + width > Bounds.Width) left = Math.Max(0, Bounds.Width - width);
        return new Rect(left, top, width, height);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double width = Bounds.Width;
        if (width <= 1) return;

        // The coverage row's own background IS the "unused" colour, so spare
        // footage needs no drawing — it is simply where nothing was painted.
        var coverage = new Rect(0, CoverageTop, width, CoverageHeight);
        context.FillRectangle(Brush("TimelineUnused") ?? Brushes.Black, coverage);

        if (_map.TotalFrames <= 0)
        {
            context.DrawRectangle(null, new Pen(Brush("Divider"), 1), coverage);
            return;
        }

        DrawLevelBands(context);
        DrawSpans(context);
        DrawMarks(context);
        context.DrawRectangle(null, new Pen(Brush("Divider"), 1), coverage);
        DrawMoves(context);
        DrawPlayhead(context);
    }

    private void DrawLevelBands(DrawingContext context)
    {
        IBrush a = Brush("TimelineLevelBand") ?? Brushes.SlateGray;
        IBrush b = Brush("TimelineLevelBandAlt") ?? Brushes.LightSlateGray;

        foreach ((TimelineLevelBand band, FormattedText? label, int index) in _levelLabels)
        {
            Rect r = RectFor(band.StartFrame, band.EndFrame, LevelRowTop, LevelRowHeight);
            context.FillRectangle(index % 2 == 0 ? a : b, r, 2);

            // A band shows its full label, or nothing. A title clipped to two
            // letters is not a shorter label, it is a wrong one — the tooltip
            // and the line above the strip already name it in full.
            if (label == null || label.Width + 6 > r.Width) continue;
            using (context.PushClip(r))
                context.DrawText(label, new Point(r.X + 3, r.Y + (LevelRowHeight - label.Height) / 2));
        }
    }

    private void DrawSpans(DrawingContext context)
    {
        foreach (TimelineSpan span in _ordered)
        {
            Rect r = RectFor(span.StartFrame, span.EndFrame, CoverageTop + 1, CoverageHeight - 2);
            if (span.Role == TimelineRole.Unassigned)
            {
                // Hollow: the footage is marked but no part of the game plays it.
                // An outline says "present but not wired in" without needing a key.
                IBrush? edge = Brush("TimelineUnassigned");
                context.DrawRectangle(new SolidColorBrush(Colors.White, 0.06), new Pen(edge, 1, DashStyle.Dash), r, 2, 2);
            }
            else
            {
                context.FillRectangle(SpanBrush(span.Role), r, 2);
            }

            if (span.ClipId is { } id && id == _selectedClipId)
                context.DrawRectangle(null, new Pen(Brush("FgPrimary"), 1.5), r.Inflate(1), 3, 3);
        }
    }

    // Fallbacks are not decoration: a null brush reaching FillRectangle throws
    // inside the paint loop, and a strip that cannot find a theme resource
    // should still draw the shape of the game.
    private IBrush SpanBrush(TimelineRole role) => role switch
    {
        TimelineRole.Gameplay => Brush("TimelineGameplay") ?? Brushes.DeepSkyBlue,
        TimelineRole.LevelIntro => Brush("TimelineIntro") ?? Brushes.SteelBlue,
        TimelineRole.Death => Brush("TimelineDeath") ?? Brushes.IndianRed,
        TimelineRole.Slot => Brush("TimelineSlot") ?? Brushes.MediumPurple,
        TimelineRole.Still => Brush("TimelineStill") ?? Brushes.Plum,
        _ => Brush("TimelineUnassigned") ?? Brushes.LightSteelBlue,
    };

    /// <summary>The In/Out brackets, so a span being marked is seen in context
    /// with everything already on the timeline rather than as two numbers.</summary>
    private void DrawMarks(DrawingContext context)
    {
        if (_markIn == null && _markOut == null) return;
        var pen = new Pen(Brush("AccentAmber") ?? Brushes.Goldenrod, 1.5);
        double top = CoverageTop - 2;
        double bottom = CoverageTop + CoverageHeight + 2;

        if (_markIn is { } mi)
        {
            double x = XFor(mi);
            context.DrawLine(pen, new Point(x, top), new Point(x, bottom));
            context.DrawLine(pen, new Point(x, top), new Point(x + 5, top));
        }
        if (_markOut is { } mo)
        {
            double x = XFor(mo + 1);
            context.DrawLine(pen, new Point(x, top), new Point(x, bottom));
            context.DrawLine(pen, new Point(x - 5, top), new Point(x, top));
        }
        if (_markIn is { } a2 && _markOut is { } b2 && b2 >= a2)
            context.FillRectangle(new SolidColorBrush(Colors.White, 0.10),
                                  RectFor(a2, b2, CoverageTop + 1, CoverageHeight - 2));
    }

    private void DrawMoves(DrawingContext context)
    {
        if (_moves.Count == 0) return;
        IBrush bright = Brush("AccentAmber") ?? Brushes.Goldenrod;
        IBrush bad = Brush("PortDeath") ?? Brushes.IndianRed;
        var faint = new SolidColorBrush(Color.Parse("#E8B04C"), 0.30);

        foreach (TimelineMoveTick move in _moves)
        {
            // The selected scene's moves stand full height; the rest are stubs,
            // present so the density of the game is visible at a glance without
            // competing with the scene actually being edited.
            double height = move.InSelectedScene ? MoveRowHeight : MoveRowHeight * 0.55;
            double x = XFor(move.Frame);
            var r = new Rect(Math.Max(0, x - 1), MoveRowTop + (MoveRowHeight - height), 2, height);
            IBrush fill = move.Violates ? bad : move.InSelectedScene ? bright : faint;
            context.FillRectangle(fill, r);
        }
    }

    private void DrawPlayhead(DrawingContext context)
    {
        IBrush head0 = Brush("TimelinePlayhead") ?? Brushes.Gold;
        double x = XFor(_currentFrame);
        var pen = new Pen(head0, 1);
        context.DrawLine(pen, new Point(x, LevelRowTop), new Point(x, MoveRowTop + MoveRowHeight));

        // A head on the line, so the position is findable when it sits over a
        // bright span it would otherwise disappear into.
        var head = new StreamGeometry();
        using (StreamGeometryContext g = head.Open())
        {
            g.BeginFigure(new Point(x - 4, LevelRowTop), true);
            g.LineTo(new Point(x + 4, LevelRowTop));
            g.LineTo(new Point(x, LevelRowTop + 5));
            g.EndFigure(true);
        }
        context.DrawGeometry(head0, null, head);
    }

    // ---------- Pointer: click or drag anywhere on the strip to go there ----------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        FrameRequested?.Invoke(this, FrameFor(e.GetPosition(this).X));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        double x = e.GetPosition(this).X;
        if (_dragging)
        {
            FrameRequested?.Invoke(this, FrameFor(x));
            e.Handled = true;
            return;
        }
        // Only on change: setting the tip on every mouse move restarts the
        // tooltip timer, so it never settles long enough to actually appear.
        string tip = DescribeFrame(FrameFor(x));
        if (tip == _tip) return;
        _tip = tip;
        ToolTip.SetTip(this, tip);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>
    /// What a frame is, in words — the hover tooltip, and the same sentence the
    /// window shows for the frame under the playhead.
    /// </summary>
    public string DescribeFrame(int frame)
    {
        if (_map.TotalFrames <= 0) return "";
        string where = _map.LevelAt(frame) is { } band ? $"Level {band.Number} · {band.Title} — " : "";
        if (_map.SpanAt(frame) is { } span)
        {
            string role = span.Role switch
            {
                TimelineRole.Gameplay => "Scene",
                TimelineRole.LevelIntro => "Level intro",
                TimelineRole.Death => "Death",
                TimelineRole.Slot => "Slot",
                TimelineRole.Still => "Still",
                _ => "Unassigned scene",
            };
            return $"{where}{role}: {span.Name}   ({span.StartFrame:D6}–{span.EndFrame:D6})";
        }

        foreach ((int start, int end) in _map.UnusedRuns)
            if (frame >= start && frame <= end)
                return $"Unused video — {end - start + 1:N0} frames ({start:D6}–{end:D6}), nothing in the game plays this";
        return "Unused video";
    }
}

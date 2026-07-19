using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Ldp.Project;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ldp.App;

/// <summary>
/// ComfyUI-style storyboard canvas: pan/zoom world, draggable clip nodes with
/// typed output ports (success/death/timeout), bezier wires, wire dragging.
/// Owns only presentation + graph edits; playback and clip lookup stay in
/// MainWindow via events.
/// </summary>
public partial class StoryboardView : UserControl
{
    private const double NodeW = 190;
    private const double NodeH = 118;
    private const double StartW = 130;
    private const double StartH = 46;
    private const double PortR = 7;
    private const double PortHitR = 16;

    public event Action? GraphChanged;
    public event Action<Clip>? NodeActivated;      // jump editor to the scene (edit)
    public event Action<Clip>? PlaySceneRequested; // play just this one scene
    public event Action<IReadOnlyList<Clip>>? PlayFlowRequested;

    private LdpProject? _project;
    private Func<Guid, ClipItem?>? _clipLookup;

    private Matrix _view = Matrix.Identity;
    private readonly Dictionary<Guid, Control> _nodeControls = [];
    private readonly Dictionary<StoryEdge, Avalonia.Controls.Shapes.Path> _edgePaths = [];

    // Interaction state
    private Point _lastPointer;
    private bool _panning;
    private StoryNode? _dragNode;
    private Point _dragNodeStart;
    private Point _dragPointerStartWorld;
    private (StoryNode Node, PortKind Port)? _wireFrom;
    private Avalonia.Controls.Shapes.Path? _tempWire;
    private StoryNode? _selectedNode;
    private StoryEdge? _selectedEdge;

    public StoryboardView()
    {
        InitializeComponent();
        Viewport.PointerPressed += OnViewportPressed;
        Viewport.PointerMoved += OnViewportMoved;
        Viewport.PointerReleased += OnViewportReleased;
        Viewport.PointerWheelChanged += OnViewportWheel;

        // Our pan/zoom math treats (0,0) as the transform origin; the default
        // origin is the control's center, which skews hit-testing when zoomed.
        World.RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute);

        DragDrop.SetAllowDrop(Viewport, true);
        Viewport.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(ClipDragFormat)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        });
        Viewport.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>Drag-and-drop data format carrying a clip id from the scene bin.</summary>
    public static readonly DataFormat<string> ClipDragFormat =
        DataFormat.CreateStringApplicationFormat("ldp-clip-id");

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_project == null || _clipLookup == null) return;
        if (e.DataTransfer.TryGetValue(ClipDragFormat) is not { } idText ||
            !Guid.TryParse(idText, out Guid clipId)) return;
        if (_clipLookup(clipId) is not { } item) return;

        Point world = ToWorld(e.GetPosition(Viewport));
        AddClipNode(item.Clip, autoChain: false, at: world);
        e.Handled = true;
    }

    // ---------- Public API ----------

    public void SetProject(LdpProject? project, Func<Guid, ClipItem?> clipLookup)
    {
        _project = project;
        _clipLookup = clipLookup;
        _selectedNode = null;
        _selectedEdge = null;

        if (project != null)
        {
            project.Graph.Heal();
            if (project.Graph.Start == null)
                project.Graph.Nodes.Add(new StoryNode { Kind = NodeKind.Start, X = 60, Y = 240 });
        }

        Rebuild();
    }

    /// <summary>
    /// Adds a scene node. With autoChain it wires onto the success-chain tail
    /// (quick filmstrip building); without, it lands loose at <paramref name="at"/>
    /// (or a free spot) for manual wiring.
    /// </summary>
    public void AddClipNode(Clip clip, bool autoChain = true, Point? at = null)
    {
        if (_project == null) return;
        StoryGraph graph = _project.Graph;

        double x = 60, y = 240;
        if (at is { } p)
        {
            x = p.X - NodeW / 2;
            y = p.Y - NodeH / 2;
        }
        else if (graph.Nodes.Count > 0)
        {
            StoryNode rightmost = graph.Nodes.OrderByDescending(n => n.X).First();
            x = rightmost.X + NodeW + 70;
            y = rightmost.Y;
        }

        var node = new StoryNode { Kind = NodeKind.Clip, ClipId = clip.Id, X = x, Y = y };
        graph.Nodes.Add(node);

        if (autoChain)
        {
            StoryNode? tail = FindSuccessTail(graph, excluding: node.Id);
            if (tail != null)
            {
                PortKind port = tail.Kind == NodeKind.Start ? PortKind.Out : PortKind.Success;
                if (graph.EdgeFrom(tail.Id, port) == null)
                    graph.Edges.Add(new StoryEdge { FromNode = tail.Id, FromPort = port, ToNode = node.Id });
            }
        }

        Rebuild();
        GraphChanged?.Invoke();
    }

    /// <summary>Re-renders nodes (e.g. after clip interactions change).</summary>
    public void Refresh() => Rebuild();

    public void RemoveNodesForClip(Guid clipId)
    {
        if (_project == null) return;
        List<StoryNode> doomed = _project.Graph.Nodes.Where(n => n.ClipId == clipId).ToList();
        if (doomed.Count == 0) return;
        foreach (StoryNode node in doomed) _project.Graph.RemoveNode(node.Id);
        Rebuild();
        GraphChanged?.Invoke();
    }

    private static StoryNode? FindSuccessTail(StoryGraph graph, Guid excluding)
    {
        StoryNode? node = graph.Start;
        HashSet<Guid> visited = [];
        while (node != null && visited.Add(node.Id))
        {
            PortKind port = node.Kind == NodeKind.Start ? PortKind.Out : PortKind.Success;
            StoryEdge? edge = graph.EdgeFrom(node.Id, port);
            StoryNode? next = edge != null ? graph.NodeById(edge.ToNode) : null;
            if (next == null || next.Id == excluding) return node;
            node = next;
        }
        return node;
    }

    // ---------- Rendering ----------

    private void Rebuild()
    {
        NodeLayer.Children.Clear();
        EdgeLayer.Children.Clear();
        _nodeControls.Clear();
        _edgePaths.Clear();
        if (_project == null) return;

        foreach (StoryNode node in _project.Graph.Nodes)
        {
            Control control = BuildNode(node);
            _nodeControls[node.Id] = control;
            NodeLayer.Children.Add(control);
            PositionNode(node);
        }
        foreach (StoryEdge edge in _project.Graph.Edges.ToList())
        {
            if (_project.Graph.NodeById(edge.FromNode) == null || _project.Graph.NodeById(edge.ToNode) == null)
            {
                _project.Graph.Edges.Remove(edge); // heal dangling edges
                continue;
            }
            var path = new Avalonia.Controls.Shapes.Path
            {
                Stroke = PortBrush(edge.FromPort),
                StrokeThickness = 2.5,
            };
            _edgePaths[edge] = path;
            EdgeLayer.Children.Add(path);
            UpdateEdgeGeometry(edge);
        }
        ApplyView();
        UpdateSelectionVisuals();
    }

    private Control BuildNode(StoryNode node)
    {
        bool isStart = node.Kind == NodeKind.Start;
        double w = isStart ? StartW : NodeW;
        double h = isStart ? StartH : NodeH;

        var canvas = new Canvas { Width = w, Height = h, Tag = node };

        var body = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(9),
            Background = (IBrush?)this.FindResource(isStart ? "BgBar" : "BgNode"),
            BorderThickness = new Thickness(1.5),
            BorderBrush = (IBrush?)this.FindResource("Divider"),
            Tag = node,
        };

        if (isStart)
        {
            body.Child = new TextBlock
            {
                Text = "▶ GAME START",
                Foreground = (IBrush?)this.FindResource("Accent"),
                FontWeight = FontWeight.Bold,
                FontSize = 13,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
        }
        else
        {
            ClipItem? clip = node.ClipId is { } id ? _clipLookup?.Invoke(id) : null;
            var stack = new StackPanel { Margin = new Thickness(8, 6) };
            stack.Children.Add(new TextBlock
            {
                Text = clip?.Name ?? "(missing clip)",
                Foreground = (IBrush?)this.FindResource("FgPrimary"),
                FontSize = 12,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            stack.Children.Add(new Image
            {
                Source = clip?.Thumbnail,
                Height = 62,
                Margin = new Thickness(0, 4),
                Stretch = Stretch.UniformToFill,
            });
            int moveCount = clip?.Clip.Interactions.Count ?? 0;
            stack.Children.Add(new TextBlock
            {
                Text = (clip?.Range ?? "") + (moveCount > 0 ? $" · {moveCount} moves" : ""),
                Foreground = (IBrush?)this.FindResource("FgFaint"),
                FontFamily = new FontFamily("Consolas,monospace"),
                FontSize = 10,
            });

            // Mini-strip of interaction markers along the clip's timeline.
            if (clip != null && clip.Clip.Interactions.Count > 0)
            {
                var strip = new Canvas { Height = 5, Margin = new Thickness(0, 3, 0, 0) };
                double stripW = NodeW - 16;
                int span = Math.Max(1, clip.Clip.FrameCount - 1);
                foreach (InteractionMarker marker in clip.Clip.Interactions)
                {
                    var tick = new Avalonia.Controls.Shapes.Rectangle
                    {
                        Width = 3,
                        Height = 5,
                        Fill = (IBrush?)this.FindResource(
                            marker.Input is InputKind.Button1 or InputKind.Button2 ? "PortSuccess" : "Accent"),
                    };
                    Canvas.SetLeft(tick, (marker.Frame - clip.Clip.StartFrame) / (double)span * stripW);
                    strip.Children.Add(tick);
                }
                stack.Children.Add(strip);
            }
            body.Child = stack;
        }

        canvas.Children.Add(body);

        // Input port (left edge) for everything except Start.
        if (!isStart)
            canvas.Children.Add(MakePort(node, null, -PortR, h / 2 - PortR, (IBrush?)this.FindResource("FgMuted")));

        // Output ports (right edge).
        foreach ((PortKind port, double frac) in OutputPorts(node))
            canvas.Children.Add(MakePort(node, port, w - PortR, h * frac - PortR, PortBrush(port)));

        body.PointerPressed += (_, e) => OnNodePressed(node, e);

        // Double-click plays just this scene (fast play-testing without
        // sitting through the whole 45-minute flow).
        body.DoubleTapped += (_, _) =>
        {
            if (ClipFor(node) is { } item) PlaySceneRequested?.Invoke(item.Clip);
        };

        if (!isStart) body.ContextMenu = BuildNodeMenu(node);
        return canvas;
    }

    private ClipItem? ClipFor(StoryNode node) =>
        node.ClipId is { } id ? _clipLookup?.Invoke(id) : null;

    private ContextMenu BuildNodeMenu(StoryNode node)
    {
        var menu = new ContextMenu();

        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        menu.Items.Add(Item("▶ Play this scene", () =>
        {
            SelectNode(node);
            if (ClipFor(node) is { } item) PlaySceneRequested?.Invoke(item.Clip);
        }));
        menu.Items.Add(Item("▶ Play flow from here", () =>
        {
            SelectNode(node);
            if (_project == null || _clipLookup == null) return;
            List<Clip> clips = _project.Graph.SuccessPathFrom(node)
                .Select(id => _clipLookup(id)?.Clip)
                .Where(c => c != null).Select(c => c!).ToList();
            if (clips.Count > 0) PlayFlowRequested?.Invoke(clips);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Jump to scene start (edit)", () =>
        {
            SelectNode(node);
            if (ClipFor(node) is { } item) NodeActivated?.Invoke(item.Clip);
        }));
        menu.Items.Add(Item("Delete node", () =>
        {
            if (_project == null) return;
            _project.Graph.RemoveNode(node.Id);
            _selectedNode = null;
            Rebuild();
            GraphChanged?.Invoke();
        }));
        return menu;
    }

    private static IEnumerable<(PortKind Port, double Fraction)> OutputPorts(StoryNode node) =>
        node.Kind == NodeKind.Start
            ? [(PortKind.Out, 0.5)]
            : [(PortKind.Success, 0.3), (PortKind.Death, 0.6), (PortKind.Timeout, 0.85)];

    private Ellipse MakePort(StoryNode node, PortKind? outputPort, double x, double y, IBrush? brush)
    {
        var port = new Ellipse
        {
            Width = PortR * 2,
            Height = PortR * 2,
            Fill = brush,
            Stroke = (IBrush?)this.FindResource("BgCanvas"),
            StrokeThickness = 1.5,
            Tag = (node, outputPort),
        };
        Canvas.SetLeft(port, x);
        Canvas.SetTop(port, y);
        if (outputPort is { } op)
        {
            port.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed) return;
                _wireFrom = (node, op);
                _tempWire = new Avalonia.Controls.Shapes.Path
                {
                    Stroke = PortBrush(op),
                    StrokeThickness = 2.5,
                    StrokeDashArray = [4, 3],
                };
                EdgeLayer.Children.Add(_tempWire);
                e.Pointer.Capture(Viewport);
                e.Handled = true;
            };
            ToolTip.SetTip(port, op.ToString());
        }
        return port;
    }

    private IBrush? PortBrush(PortKind port) => (IBrush?)this.FindResource(port switch
    {
        PortKind.Success or PortKind.Out => "PortSuccess",
        PortKind.Death => "PortDeath",
        _ => "PortTimeout",
    });

    private void PositionNode(StoryNode node)
    {
        if (!_nodeControls.TryGetValue(node.Id, out Control? control)) return;
        Canvas.SetLeft(control, node.X);
        Canvas.SetTop(control, node.Y);
    }

    private (double W, double H) NodeSize(StoryNode node) =>
        node.Kind == NodeKind.Start ? (StartW, StartH) : (NodeW, NodeH);

    private Point OutputPortCenter(StoryNode node, PortKind port)
    {
        (double w, double h) = NodeSize(node);
        // Fall back to mid-height for port kinds the node doesn't have -
        // damaged data must never crash the canvas.
        double frac = 0.5;
        foreach ((PortKind p, double f) in OutputPorts(node))
            if (p == port) { frac = f; break; }
        return new Point(node.X + w, node.Y + h * frac);
    }

    private Point InputPortCenter(StoryNode node)
    {
        (double _, double h) = NodeSize(node);
        return new Point(node.X, node.Y + h / 2);
    }

    private void UpdateEdgeGeometry(StoryEdge edge)
    {
        if (_project == null || !_edgePaths.TryGetValue(edge, out Avalonia.Controls.Shapes.Path? path)) return;
        StoryNode? from = _project.Graph.NodeById(edge.FromNode);
        StoryNode? to = _project.Graph.NodeById(edge.ToNode);
        if (from == null || to == null) return;
        path.Data = WireGeometry(OutputPortCenter(from, edge.FromPort), InputPortCenter(to));
    }

    private static StreamGeometry WireGeometry(Point a, Point b)
    {
        double bend = Math.Max(40, Math.Abs(b.X - a.X) * 0.5);
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(a, false);
            ctx.CubicBezierTo(new Point(a.X + bend, a.Y), new Point(b.X - bend, b.Y), b);
            ctx.EndFigure(false);
        }
        return geometry;
    }

    private void RedrawEdgesTouching(Guid nodeId)
    {
        foreach (StoryEdge edge in _edgePaths.Keys)
            if (edge.FromNode == nodeId || edge.ToNode == nodeId)
                UpdateEdgeGeometry(edge);
    }

    // ---------- Pan / zoom ----------

    private void ApplyView() => World.RenderTransform = new MatrixTransform(_view);

    private Point ToWorld(Point viewportPoint)
    {
        if (_view.TryInvert(out Matrix inverse))
            return viewportPoint.Transform(inverse);
        return viewportPoint;
    }

    private void OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        double factor = e.Delta.Y > 0 ? 1.12 : 1 / 1.12;
        double newScale = _view.M11 * factor;
        if (newScale is < 0.15 or > 3.0) return;

        Point p = e.GetPosition(Viewport);
        _view = _view * Matrix.CreateTranslation(-p.X, -p.Y)
                      * Matrix.CreateScale(factor, factor)
                      * Matrix.CreateTranslation(p.X, p.Y);
        ApplyView();
        e.Handled = true;
    }

    private void OnViewportPressed(object? sender, PointerPressedEventArgs e)
    {
        _lastPointer = e.GetPosition(Viewport);

        // Port presses and node presses mark the event handled; anything that
        // arrives here is empty canvas.
        if (e.Handled) return;
        PointerPointProperties props = e.GetCurrentPoint(Viewport).Properties;
        if (props.IsLeftButtonPressed || props.IsMiddleButtonPressed)
        {
            _panning = true;
            SelectNode(null);
            SelectEdge(HitTestEdge(ToWorld(_lastPointer)));
            if (_selectedEdge != null) _panning = false;
            e.Pointer.Capture(Viewport);
        }
    }

    private void OnViewportMoved(object? sender, PointerEventArgs e)
    {
        Point pos = e.GetPosition(Viewport);
        Point delta = new(pos.X - _lastPointer.X, pos.Y - _lastPointer.Y);
        _lastPointer = pos;

        if (_wireFrom is { } wire)
        {
            _tempWire!.Data = WireGeometry(OutputPortCenter(wire.Node, wire.Port), ToWorld(pos));
        }
        else if (_dragNode is { } node)
        {
            Point world = ToWorld(pos);
            node.X = _dragNodeStart.X + (world.X - _dragPointerStartWorld.X);
            node.Y = _dragNodeStart.Y + (world.Y - _dragPointerStartWorld.Y);
            PositionNode(node);
            RedrawEdgesTouching(node.Id);
        }
        else if (_panning)
        {
            _view = _view * Matrix.CreateTranslation(delta.X, delta.Y);
            ApplyView();
        }
    }

    private void OnViewportReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_wireFrom is { } wire)
        {
            Point world = ToWorld(e.GetPosition(Viewport));
            StoryNode? target = HitTestInputPort(world);
            EdgeLayer.Children.Remove(_tempWire!);
            _tempWire = null;
            _wireFrom = null;

            if (_project != null && target != null && target.Id != wire.Node.Id)
            {
                // Death scenes commonly come in pairs, so the Death port holds
                // up to two targets; every other port holds one (re-wiring
                // replaces the oldest).
                int maxEdges = wire.Port == PortKind.Death ? 2 : 1;
                _project.Graph.Edges.RemoveAll(x =>
                    x.FromNode == wire.Node.Id && x.FromPort == wire.Port && x.ToNode == target.Id);
                List<StoryEdge> existing = _project.Graph.Edges
                    .Where(x => x.FromNode == wire.Node.Id && x.FromPort == wire.Port)
                    .ToList();
                while (existing.Count >= maxEdges)
                {
                    _project.Graph.Edges.Remove(existing[0]);
                    existing.RemoveAt(0);
                }
                _project.Graph.Edges.Add(new StoryEdge
                {
                    FromNode = wire.Node.Id,
                    FromPort = wire.Port,
                    ToNode = target.Id,
                });
                Rebuild();
                GraphChanged?.Invoke();
            }
        }
        else if (_dragNode != null)
        {
            _dragNode = null;
            GraphChanged?.Invoke(); // position changed
        }
        _panning = false;
        e.Pointer.Capture(null);
    }

    private void OnNodePressed(StoryNode node, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed) return;
        SelectNode(node);
        _dragNode = node;
        _dragNodeStart = new Point(node.X, node.Y);
        _dragPointerStartWorld = ToWorld(e.GetPosition(Viewport));
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    // ---------- Selection / deletion ----------

    private void SelectNode(StoryNode? node)
    {
        _selectedNode = node;
        if (node != null) _selectedEdge = null;
        UpdateSelectionVisuals();
    }

    private void SelectEdge(StoryEdge? edge)
    {
        _selectedEdge = edge;
        if (edge != null) _selectedNode = null;
        UpdateSelectionVisuals();
    }

    private void UpdateSelectionVisuals()
    {
        foreach ((Guid id, Control control) in _nodeControls)
        {
            if (((Canvas)control).Children.OfType<Border>().FirstOrDefault() is { } body)
                body.BorderBrush = (IBrush?)this.FindResource(
                    _selectedNode?.Id == id ? "Accent" : "Divider");
        }
        foreach ((StoryEdge edge, Avalonia.Controls.Shapes.Path path) in _edgePaths)
            path.StrokeThickness = ReferenceEquals(edge, _selectedEdge) ? 4.5 : 2.5;
    }

    private StoryNode? HitTestInputPort(Point world)
    {
        if (_project == null) return null;
        foreach (StoryNode node in _project.Graph.Nodes)
        {
            if (node.Kind == NodeKind.Start) continue;
            Point center = InputPortCenter(node);
            if ((world - center) is { } d && Math.Sqrt(d.X * d.X + d.Y * d.Y) <= PortHitR)
                return node;
        }
        return null;
    }

    private StoryEdge? HitTestEdge(Point world)
    {
        if (_project == null) return null;
        foreach (StoryEdge edge in _edgePaths.Keys)
        {
            StoryNode? from = _project.Graph.NodeById(edge.FromNode);
            StoryNode? to = _project.Graph.NodeById(edge.ToNode);
            if (from == null || to == null) continue;
            // Sample the bezier and take the nearest segment distance.
            Point a = OutputPortCenter(from, edge.FromPort);
            Point b = InputPortCenter(to);
            double bend = Math.Max(40, Math.Abs(b.X - a.X) * 0.5);
            Point c1 = new(a.X + bend, a.Y), c2 = new(b.X - bend, b.Y);
            Point prev = a;
            for (int i = 1; i <= 24; i++)
            {
                double t = i / 24.0;
                Point p = Bezier(a, c1, c2, b, t);
                if (DistanceToSegment(world, prev, p) < 7) return edge;
                prev = p;
            }
        }
        return null;
    }

    private static Point Bezier(Point p0, Point p1, Point p2, Point p3, double t)
    {
        double u = 1 - t;
        return new Point(
            u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
            u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y);
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        Vector ab = b - a;
        double lengthSq = ab.X * ab.X + ab.Y * ab.Y;
        double t = lengthSq < 1e-9 ? 0 : Math.Clamp(((p - a).X * ab.X + (p - a).Y * ab.Y) / lengthSq, 0, 1);
        Point proj = new(a.X + ab.X * t, a.Y + ab.Y * t);
        return Math.Sqrt((p.X - proj.X) * (p.X - proj.X) + (p.Y - proj.Y) * (p.Y - proj.Y));
    }

    /// <summary>Called by the window's key handler while the storyboard is visible.</summary>
    public void HandleKey(KeyEventArgs e)
    {
        if (!IsVisible || _project == null || e.Source is TextBox) return;
        if (e.Key is Key.Delete or Key.Back)
        {
            if (_selectedNode is { } node && node.Kind != NodeKind.Start)
            {
                _project.Graph.RemoveNode(node.Id);
                _selectedNode = null;
                Rebuild();
                GraphChanged?.Invoke();
                e.Handled = true;
            }
            else if (_selectedEdge is { } edge)
            {
                _project.Graph.Edges.Remove(edge);
                _selectedEdge = null;
                Rebuild();
                GraphChanged?.Invoke();
                e.Handled = true;
            }
        }
    }

    // ---------- Toolbar ----------

    private void OnPlayFlow(object? sender, RoutedEventArgs e)
    {
        if (_project == null || _clipLookup == null) return;
        List<Clip> clips = _project.Graph.SuccessPathClips()
            .Select(id => _clipLookup(id)?.Clip)
            .Where(c => c != null)
            .Select(c => c!)
            .ToList();
        if (clips.Count > 0) PlayFlowRequested?.Invoke(clips);
    }

    private void OnFitView(object? sender, RoutedEventArgs e)
    {
        if (_project == null || _project.Graph.Nodes.Count == 0) return;
        double minX = _project.Graph.Nodes.Min(n => n.X) - 60;
        double minY = _project.Graph.Nodes.Min(n => n.Y) - 60;
        double maxX = _project.Graph.Nodes.Max(n => n.X + NodeSize(n).W) + 60;
        double maxY = _project.Graph.Nodes.Max(n => n.Y + NodeSize(n).H) + 60;

        double scaleX = Viewport.Bounds.Width / (maxX - minX);
        double scaleY = Viewport.Bounds.Height / (maxY - minY);
        double scale = Math.Clamp(Math.Min(scaleX, scaleY), 0.15, 1.2);

        _view = Matrix.CreateTranslation(-minX, -minY) * Matrix.CreateScale(scale, scale);
        ApplyView();
    }

    private void OnAutoLayout(object? sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        StoryGraph graph = _project.Graph;

        // Main line: the success chain, left to right. Everything else drops
        // to rows beneath, grouped under the node that links to it.
        Dictionary<Guid, int> depth = [];
        List<StoryNode> chain = [];
        StoryNode? node = graph.Start;
        HashSet<Guid> visited = [];
        while (node != null && visited.Add(node.Id))
        {
            chain.Add(node);
            PortKind port = node.Kind == NodeKind.Start ? PortKind.Out : PortKind.Success;
            StoryEdge? edge = graph.EdgeFrom(node.Id, port);
            node = edge != null ? graph.NodeById(edge.ToNode) : null;
        }
        for (int i = 0; i < chain.Count; i++)
        {
            chain[i].X = 60 + i * (NodeW + 70);
            chain[i].Y = 200;
            depth[chain[i].Id] = i;
        }

        double orphanY = 200 + NodeH + 90;
        foreach (StoryNode other in graph.Nodes.Where(n => !visited.Contains(n.Id)))
        {
            StoryEdge? incoming = graph.Edges.Find(x => x.ToNode == other.Id && visited.Contains(x.FromNode));
            double x = incoming != null && depth.TryGetValue(incoming.FromNode, out int i)
                ? 60 + i * (NodeW + 70) + NodeW * 0.5
                : 60;
            other.X = x;
            other.Y = orphanY;
            orphanY += NodeH + 40;
        }

        Rebuild();
        GraphChanged?.Invoke();
        OnFitView(null, null!);
    }
}

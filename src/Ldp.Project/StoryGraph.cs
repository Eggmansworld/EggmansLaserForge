namespace Ldp.Project;

/// <summary>
/// The storyboard: a directed graph of clips, ComfyUI-style. Nodes reference
/// clips by id; edges leave a node through a typed output port (what happened
/// in the game) and enter another node. The success chain from the Start node
/// is the "main line" of the game and drives storyboard playback.
/// </summary>
public sealed class StoryGraph
{
    public List<StoryNode> Nodes { get; set; } = [];
    public List<StoryEdge> Edges { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public StoryNode? Start => Nodes.Find(n => n.Kind == NodeKind.Start);

    /// <summary>
    /// Repairs graphs written by older builds (or otherwise damaged data) so
    /// bad persisted state can never take the canvas down:
    /// - a clip node without a clip that has outgoing edges was once the Start
    ///   node (a serializer bug demoted it); restore it, drop other empties
    /// - port kinds are coerced to ports the node actually has
    /// - dangling edges are removed
    /// </summary>
    public void Heal()
    {
        if (Start == null)
        {
            List<StoryNode> orphans = Nodes.Where(n => n.Kind == NodeKind.Clip && n.ClipId == null).ToList();
            StoryNode? candidate = orphans.FirstOrDefault(n => Edges.Any(e => e.FromNode == n.Id))
                                   ?? orphans.FirstOrDefault();
            if (candidate != null)
            {
                candidate.Kind = NodeKind.Start;
                foreach (StoryNode extra in orphans.Where(n => n != candidate).ToList())
                    RemoveNode(extra.Id);
            }
        }

        // Only one Start survives.
        foreach (StoryNode extra in Nodes.Where(n => n.Kind == NodeKind.Start).Skip(1).ToList())
            RemoveNode(extra.Id);

        Edges.RemoveAll(e => NodeById(e.FromNode) == null || NodeById(e.ToNode) == null);
        foreach (StoryEdge edge in Edges)
        {
            NodeKind kind = NodeById(edge.FromNode)!.Kind;
            if (kind == NodeKind.Start && edge.FromPort != PortKind.Out) edge.FromPort = PortKind.Out;
            if (kind == NodeKind.Clip && edge.FromPort == PortKind.Out) edge.FromPort = PortKind.Success;
        }
    }

    public StoryNode? NodeById(Guid id) => Nodes.Find(n => n.Id == id);

    /// <summary>Single edge leaving (node, port), or null.</summary>
    public StoryEdge? EdgeFrom(Guid nodeId, PortKind port) =>
        Edges.Find(e => e.FromNode == nodeId && e.FromPort == port);

    /// <summary>
    /// Walks the success chain from Start and returns the clips in play order.
    /// Cycles (e.g. attract loops) terminate the walk at the first revisit.
    /// </summary>
    public List<Guid> SuccessPathClips() => SuccessPathFrom(Start);

    /// <summary>
    /// Walks the success chain starting at a given node (inclusive) and returns
    /// the clips in play order. Used to play the whole flow, or just the tail
    /// from a chosen scene during play-testing.
    /// </summary>
    public List<Guid> SuccessPathFrom(StoryNode? start)
    {
        List<Guid> clips = [];
        HashSet<Guid> visited = [];
        StoryNode? node = start;
        while (node != null && visited.Add(node.Id))
        {
            if (node.ClipId is { } clipId) clips.Add(clipId);
            PortKind port = node.Kind == NodeKind.Start ? PortKind.Out : PortKind.Success;
            StoryEdge? edge = EdgeFrom(node.Id, port);
            node = edge != null ? NodeById(edge.ToNode) : null;
        }
        return clips;
    }

    public void RemoveNode(Guid nodeId)
    {
        Nodes.RemoveAll(n => n.Id == nodeId);
        Edges.RemoveAll(e => e.FromNode == nodeId || e.ToNode == nodeId);
    }
}

public enum NodeKind
{
    Start,
    Clip,
}

public enum PortKind
{
    /// <summary>The single output of the Start node.</summary>
    Out,
    Success,
    Death,
    Timeout,
}

public sealed class StoryNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public NodeKind Kind { get; set; } = NodeKind.Clip;

    /// <summary>Clip this node plays; null for Start.</summary>
    public Guid? ClipId { get; set; }

    // Canvas position (world coordinates).
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class StoryEdge
{
    public Guid FromNode { get; set; }
    public PortKind FromPort { get; set; }
    public Guid ToNode { get; set; }
}

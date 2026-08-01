using Ldp.Project;

namespace Ldp.EngineProbe;

/// <summary>
/// Checks for the one definition of "death scene" and the order it numbers in.
///
/// This used to be written out three times and the three disagreed. The
/// exporter counted the curated pool, per-move deaths and Death-wire targets;
/// the storyboard's DEATH chip skipped per-move deaths; and the scenes list's
/// Deaths filter counted ONLY per-move deaths — which nothing could set, so
/// that chip matched nothing at all in a project built here rather than
/// imported.
///
/// Order is the part that has to hold: Death[n] numbers are what every move
/// referencing a death points at, so a curated pool must keep its imported
/// positions and anything found later must append.
/// </summary>
public static class DeathScenesTest
{
    public static void Run(Action<string, bool> Check)
    {
        OrderChecks(Check);
        FallbackChecks(Check);
        ExportChecks(Check);
    }

    /// <summary>Wires one scene's Death port to another, the way the storyboard does.</summary>
    private static void WireDeath(LdpProject project, Guid fromClip, Guid toClip)
    {
        StoryNode from = new() { ClipId = fromClip };
        StoryNode to = new() { ClipId = toClip };
        project.Graph.Nodes.Add(from);
        project.Graph.Nodes.Add(to);
        project.Graph.Edges.Add(new StoryEdge { FromNode = from.Id, FromPort = PortKind.Death, ToNode = to.Id });
    }

    private static (LdpProject Project, Clip Play, Clip WireDeath, Clip MoveDeath, Clip Spare) Build()
    {
        var project = new LdpProject { Name = "Deaths", Author = "Eggman", GameDate = "2026-08-01" };
        Clip play = new() { Name = "Play", StartFrame = 100, EndFrame = 900 };
        Clip wireDeath = new() { Name = "Death by wire", StartFrame = 1000, EndFrame = 1100 };
        Clip moveDeath = new() { Name = "Death by move", StartFrame = 1200, EndFrame = 1300 };
        Clip spare = new() { Name = "Spare death", StartFrame = 1400, EndFrame = 1500 };
        project.Clips.AddRange([play, wireDeath, moveDeath, spare]);
        return (project, play, wireDeath, moveDeath, spare);
    }

    private static void OrderChecks(Action<string, bool> Check)
    {
        (LdpProject project, Clip play, Clip wireDeath, Clip moveDeath, Clip spare) = Build();

        Check("deaths: an untouched project has none", project.DeathScenes().Count == 0);

        // A curated spare nothing points at must survive — that is the whole
        // reason the pool exists separately from the wires.
        project.DeathPool.Add(spare.Id);
        Check("deaths: a curated spare counts", project.DeathScenes().SequenceEqual([spare.Id]));

        // A per-move death counts even with no wire anywhere.
        play.Interactions.Add(new InteractionMarker { Frame = 200, Input = InputKind.Up, DeathClipId = moveDeath.Id });
        Check("deaths: a per-move death counts",
              project.DeathScenes().SequenceEqual([spare.Id, moveDeath.Id]));

        // And a Death wire counts.
        WireDeath(project, play.Id, wireDeath.Id);
        Check("deaths: a wired death counts",
              project.DeathScenes().SequenceEqual([spare.Id, moveDeath.Id, wireDeath.Id]));

        Check("deaths: the pool keeps its position at the front",
              project.DeathScenes()[0] == spare.Id);
        Check("deaths: numbering is 1-based and follows the order",
              project.DeathNumbers()[spare.Id] == 1 &&
              project.DeathNumbers()[moveDeath.Id] == 2 &&
              project.DeathNumbers()[wireDeath.Id] == 3);

        // A scene reachable both ways is listed once.
        project.DeathPool.Add(wireDeath.Id);
        Check("deaths: a scene counted twice appears once",
              project.DeathScenes().Count(id => id == wireDeath.Id) == 1);
        Check("deaths: the pool position wins for a scene counted twice",
              project.DeathScenes().SequenceEqual([spare.Id, wireDeath.Id, moveDeath.Id]));

        // A pool entry whose scene was deleted must not become a phantom row.
        project.DeathPool.Add(Guid.NewGuid());
        Check("deaths: a pool id with no scene is dropped", project.DeathScenes().Count == 3);
    }

    private static void FallbackChecks(Action<string, bool> Check)
    {
        (LdpProject project, Clip play, Clip wireDeath, Clip _, Clip spare) = Build();
        GameLevel level = project.AddLevel();
        level.SceneIds.Add(play.Id);

        Check("fallback: nothing set means no default", project.DefaultDeathFor(play.Id) == null);

        level.DefaultDeathClipId = spare.Id;
        Check("fallback: the level's default applies", project.DefaultDeathFor(play.Id) == spare.Id);

        // A scene's own wire must beat its level's fallback.
        WireDeath(project, play.Id, wireDeath.Id);
        Check("fallback: the scene's own wire wins", project.DefaultDeathFor(play.Id) == wireDeath.Id);

        // A scene in no level gets nothing from a level fallback.
        Clip orphan = new() { Name = "Orphan", StartFrame = 2000, EndFrame = 2100 };
        project.Clips.Add(orphan);
        Check("fallback: a scene in no level has no default",
              project.DefaultDeathFor(orphan.Id) == null);
    }

    private static void ExportChecks(Action<string, bool> Check)
    {
        (LdpProject project, Clip play, Clip wireDeath, Clip moveDeath, Clip spare) = Build();
        GameLevel level = project.AddLevel();
        level.SceneIds.Add(play.Id);
        project.DeathPool.Add(spare.Id);

        // Three moves, one per state the exporter resolves.
        play.Interactions.Add(new InteractionMarker { Frame = 200, Input = InputKind.Up });
        play.Interactions.Add(new InteractionMarker { Frame = 400, Input = InputKind.Down, DeathClipId = moveDeath.Id });
        play.Interactions.Add(new InteractionMarker { Frame = 600, Input = InputKind.Left, ExplicitNoDeath = true });

        WireDeath(project, play.Id, wireDeath.Id);

        string script = SingeTemplate.Apply(project, SingeTemplate.DefaultTemplate).Script;
        Dictionary<Guid, int> numbers = project.DeathNumbers();

        Check("export: the inheriting move takes the scene's wired death",
              script.Contains($"{{200, 220, UP, {numbers[wireDeath.Id]}}}"));
        Check("export: the explicit move takes its own death",
              script.Contains($"{{400, 420, DOWN, {numbers[moveDeath.Id]}}}"));
        Check("export: an explicit no-death writes 0",
              script.Contains("{600, 620, LEFT, 0}"));
        Check("export: the curated spare still reaches the Death table",
              script.Contains($"Death[{numbers[spare.Id]}]"));

        // With no wire, the level's fallback is what a move inherits.
        var second = new LdpProject { Name = "Fallback", Author = "Eggman", GameDate = "2026-08-01" };
        Clip play2 = new() { Name = "Play", StartFrame = 100, EndFrame = 900 };
        Clip death2 = new() { Name = "Level death", StartFrame = 1000, EndFrame = 1100 };
        second.Clips.AddRange([play2, death2]);
        GameLevel level2 = second.AddLevel();
        level2.SceneIds.Add(play2.Id);
        level2.DefaultDeathClipId = death2.Id;
        second.DeathPool.Add(death2.Id);
        play2.Interactions.Add(new InteractionMarker { Frame = 200, Input = InputKind.Up });

        SingeTemplate.Result fallbackResult = SingeTemplate.Apply(second, SingeTemplate.DefaultTemplate);
        Check("export: the level fallback reaches a move that names none",
              fallbackResult.Script.Contains("{200, 220, UP, 1}"));
        Check("export: a move covered by the fallback draws no missing-death warning",
              !fallbackResult.Warnings.Any(w => w.Contains("no death scene")));
    }
}

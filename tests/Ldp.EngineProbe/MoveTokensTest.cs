using Ldp.Project;

namespace Ldp.EngineProbe;

/// <summary>
/// Checks that no move is lost importing a game that uses more than the six
/// inputs this editor authors.
///
/// Before this, an unrecognised token failed to parse and the move was dropped
/// with a log line — the game still exported, quietly missing its hardest
/// beats. The round-trip checks below are the real contract: whatever the
/// script said comes back out saying the same thing, including for moves the
/// editor has no way to author.
///
/// The tiers come from the framework, not from taste. Double moves are two
/// concurrent held states (main.singe:3078, `p1UP and p1LEFT`), so they need two
/// keys at once rather than a thumbstick. Holds are read as a pair — the move
/// after a hold is expected to be LETGO (main.singe:2296) — so they are only
/// valid together.
/// </summary>
public static class MoveTokensTest
{
    public static void Run(Action<string, bool> Check)
    {
        CatalogChecks(Check);
        RoundTripChecks(Check);
        ReportChecks(Check);
    }

    private static void CatalogChecks(Action<string, bool> Check)
    {
        Check("moves: the eight double moves are catalogued",
              MoveTokens.All.Count(i => i.Tier == MoveTokens.Tier.Double) == 8);
        Check("moves: diagonals are double moves",
              MoveTokens.TierOf("UPLEFT") == MoveTokens.Tier.Double &&
              MoveTokens.TierOf("DOWNRIGHT") == MoveTokens.Tier.Double);
        Check("moves: button-plus-direction are double moves too",
              MoveTokens.TierOf("ACTUP") == MoveTokens.Tier.Double &&
              MoveTokens.TierOf("ACTRIGHT") == MoveTokens.Tier.Double);
        Check("moves: holds and LETGO share a tier",
              MoveTokens.TierOf("HOLDUP") == MoveTokens.Tier.Hold &&
              MoveTokens.TierOf("LETGO") == MoveTokens.Tier.Hold);
        Check("moves: mash rates are a tier of their own",
              MoveTokens.TierOf("MASH") == MoveTokens.Tier.Rate &&
              MoveTokens.TierOf("MASHMAX") == MoveTokens.Tier.Rate);
        Check("moves: branch constructs are their own tier",
              MoveTokens.TierOf("CHOOSE") == MoveTokens.Tier.Branch &&
              MoveTokens.TierOf("YESNO") == MoveTokens.Tier.Branch);
        Check("moves: an invented token is unknown", !MoveTokens.IsKnown("WOBBLE"));
        Check("moves: lookup ignores case", MoveTokens.IsKnown("upleft"));

        Check("moves: only double and hold are offered for authoring",
              MoveTokens.Authorable.All(i => i.Tier is MoveTokens.Tier.Double or MoveTokens.Tier.Hold) &&
              MoveTokens.Authorable.Count() == 14);
        Check("moves: CHOOSE is never offered for authoring",
              !MoveTokens.Authorable.Any(i => i.Token == "CHOOSE"));

        // Token <-> kind must agree in both directions for everything authorable.
        Check("moves: every authorable token maps to a kind and back",
              MoveTokens.Authorable.All(i =>
                  MoveTokens.KindOf(i.Token) is { } k && MoveTokens.TokenOf(k) == i.Token));
        Check("moves: rate and branch tokens map to no kind",
              MoveTokens.All.Where(i => i.Tier is MoveTokens.Tier.Rate or MoveTokens.Tier.Branch)
                            .All(i => MoveTokens.KindOf(i.Token) == null));

        Check("moves: the five holds are recognised as holds",
              new[] { InputKind.HoldUp, InputKind.HoldDown, InputKind.HoldLeft,
                      InputKind.HoldRight, InputKind.HoldButton }.All(MoveTokens.IsHold));
        Check("moves: LetGo is not itself a hold", !MoveTokens.IsHold(InputKind.LetGo));
        Check("moves: an ordinary input is not a hold", !MoveTokens.IsHold(InputKind.Up));
    }

    /// <summary>Import then export: what went in must come back out unchanged.</summary>
    private static void RoundTripChecks(Action<string, bool> Check)
    {
        string script = Script("""
                        move[n] = {200, 220, UPLEFT, 0};n=n+1
                        move[n] = {300, 320, ACTUP, 0};n=n+1
                        move[n] = {400, 420, HOLDLEFT, 0};n=n+1
                        move[n] = {430, 450, LETGO, 0};n=n+1
                        move[n] = {500, 520, MASHMAX, 0};n=n+1
                        move[n] = {600, 620, CHOOSE, 0};n=n+1
                        move[n] = {700, 720, UP, 0, BUTTON1};n=n+1
            """);

        var project = new LdpProject { Name = "Advanced", Author = "Eggman", GameDate = "2026-08-01" };
        SingeImporter.Result result = SingeImporter.Import(project, script);

        Check("moves: every move survives import", result.Moves == 7);
        Check("moves: nothing was skipped",
              !result.Warnings.Any(w => w.Contains("skipped")));

        List<InteractionMarker> moves = project.Clips.SelectMany(c => c.Interactions)
            .OrderBy(m => m.Frame).ToList();
        Check("moves: a diagonal imports as its own kind", moves[0].Input == InputKind.UpLeft);
        Check("moves: button-plus-direction imports as its own kind", moves[1].Input == InputKind.ActUp);
        Check("moves: a hold imports as its own kind", moves[2].Input == InputKind.HoldLeft);
        Check("moves: LETGO imports as its own kind", moves[3].Input == InputKind.LetGo);
        Check("moves: a mash rate imports as Advanced", moves[4].Input == InputKind.Advanced);
        Check("moves: the mash token is kept verbatim", moves[4].RawInput == "MASHMAX");
        Check("moves: a branch construct imports as Advanced", moves[5].Input == InputKind.Advanced);
        Check("moves: the branch token is kept verbatim", moves[5].RawInput == "CHOOSE");
        Check("moves: a modelled kind stores no raw token", moves[0].RawInput == null);
        Check("moves: the alternate input still imports", moves[6].AltInput == InputKind.Button1);

        // Export, and read the move lines straight back.
        string exported = SingeTemplate.Apply(project, SingeTemplate.DefaultTemplate).Script;
        foreach (string token in (string[])["UPLEFT", "ACTUP", "HOLDLEFT", "LETGO", "MASHMAX", "CHOOSE"])
            Check($"moves: {token} is written back out", exported.Contains($", {token}, "));
        Check("moves: no advanced move degraded into WAY", !exported.Contains(", WAY, "));
        Check("moves: the alternate input is written back", exported.Contains(", UP, 0, BUTTON1}"));

        // Re-importing the export must land in the same place.
        var second = new LdpProject { Name = "Advanced2" };
        SingeImporter.Import(second, exported);
        List<InteractionMarker> again = second.Clips.SelectMany(c => c.Interactions)
            .OrderBy(m => m.Frame).ToList();
        Check("moves: a second round trip is stable",
              again.Count == moves.Count &&
              again.Zip(moves).All(p => p.First.Input == p.Second.Input &&
                                        p.First.RawInput == p.Second.RawInput &&
                                        p.First.Frame == p.Second.Frame));

        // Duplicating a scene must not quietly drop the token.
        Clip source = project.Clips.First(c => c.Interactions.Any(m => m.RawInput == "MASHMAX"));
        Clip copy = source.Duplicate();
        Check("moves: duplicating a scene keeps raw tokens",
              copy.Interactions.Any(m => m.RawInput == "MASHMAX"));

        // Saving and reloading likewise.
        string path = Path.Combine(Path.GetTempPath(), "ldp-moves-" + Guid.NewGuid().ToString("N")[..8] + ".ldproj");
        try
        {
            ProjectFile.Save(project, path);
            LdpProject reloaded = ProjectFile.Load(path);
            List<InteractionMarker> saved = reloaded.Clips.SelectMany(c => c.Interactions)
                .OrderBy(m => m.Frame).ToList();
            Check("moves: raw tokens survive save and reload",
                  saved[4].RawInput == "MASHMAX" && saved[5].RawInput == "CHOOSE");
            Check("moves: modelled kinds survive save and reload",
                  saved[0].Input == InputKind.UpLeft && saved[2].Input == InputKind.HoldLeft);
        }
        finally
        {
            try { File.Delete(path); } catch { /* temp file */ }
        }

        // A token no framework defines is still refused, so a typo in a
        // hand-edited script is reported rather than silently invented.
        var bogus = new LdpProject { Name = "Bogus" };
        SingeImporter.Result bogusResult = SingeImporter.Import(bogus,
            Script("                        move[n] = {200, 220, WOBBLE, 0};n=n+1"));
        Check("moves: an undefined token is still reported",
              bogusResult.Warnings.Any(w => w.Contains("WOBBLE") && w.Contains("skipped")));
    }

    private static void ReportChecks(Action<string, bool> Check)
    {
        var project = new LdpProject { Name = "Report" };
        SingeImporter.Result result = SingeImporter.Import(project, Script("""
                        move[n] = {200, 220, UPLEFT, 0};n=n+1
                        move[n] = {300, 320, MASHMAX, 0};n=n+1
            """));

        Check("moves: double moves are reported together",
              result.Warnings.Any(w => w.Contains("Up + Left") && w.Contains("two keys at once")));
        Check("moves: the report says no gamepad is needed",
              result.Warnings.Any(w => w.Contains("no gamepad required")));
        Check("moves: an unauthorable move is named individually",
              result.Warnings.Any(w => w.Contains("MASHMAX") && w.Contains("cannot be edited here yet")));

        // Silence when a game uses nothing unusual.
        var plain = new LdpProject { Name = "Plain" };
        SingeImporter.Result plainResult = SingeImporter.Import(plain,
            Script("                        move[n] = {200, 220, UP, 0};n=n+1"));
        Check("moves: an ordinary game draws no advanced-move report",
              !plainResult.Warnings.Any(w => w.Contains("two keys at once") ||
                                             w.Contains("cannot be edited here yet")));
    }

    /// <summary>A one-level, one-scene script wrapping the given move lines.</summary>
    private static string Script(string moveLines) => $$"""
        Level[1] = {"Test", 100, 101, 1, 0, 0, -1}

        function setupMoves(thisLevel, thisScene)

            if thisLevel == 1 then
                if thisScene == 1 then
                    sceneStart = 100
                    sceneEnd = 900
        {{moveLines}}
                end
            end

        end
        """;
}

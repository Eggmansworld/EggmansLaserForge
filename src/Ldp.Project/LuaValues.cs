using System.Text.RegularExpressions;

namespace Ldp.Project;

/// <summary>
/// Resolves the frame numbers in a Singe script, including the ones written as
/// arithmetic against a base.
///
/// Plenty of authors never type an absolute frame for the menu block. They find
/// one landmark and count from it:
///
///     offsetMenus   = 49666
///     frameOptions  = offsetMenus +0
///     frameVictory  = offsetMenus +3
///     frameRankings = offsetMenus +12
///
/// which is perfectly good Lua and completely invisible to a pattern that only
/// accepts digits after the '='. Those slots used to import as unset, silently,
/// because a regex miss looks exactly like an absent line.
///
/// A value may also be built on another derived value, so resolution runs to a
/// fixed point rather than in one pass. Anything still unresolved after that is
/// reported instead of being guessed at — a forward reference to a name the
/// script never defines is the author's bug, and worth seeing.
/// </summary>
public static partial class LuaValues
{
    /// <summary>How many resolution sweeps before a chain is called circular.</summary>
    private const int MaxPasses = 16;

    /// <summary>
    /// `name = 123`, `name = other`, or `name = other +12` / `other - 4`.
    /// Anchored per line, trailing comment allowed. Anything more involved than
    /// one addition is left alone deliberately: guessing at arithmetic nobody
    /// writes in these scripts would risk inventing a frame number.
    /// </summary>
    [GeneratedRegex(@"^[ \t]*([A-Za-z_]\w*)[ \t]*=[ \t]*(?:(\d+)|([A-Za-z_]\w*)(?:[ \t]*([+-])[ \t]*(\d+))?)[ \t]*(?:--.*)?$",
                    RegexOptions.Multiline)]
    private static partial Regex AssignmentPattern();

    /// <summary>An inline value: a literal, or a name with an optional offset.</summary>
    [GeneratedRegex(@"^[ \t]*(?:(\d+)|([A-Za-z_]\w*)(?:[ \t]*([+-])[ \t]*(\d+))?)[ \t]*$")]
    private static partial Regex ValuePattern();

    public sealed record Table(IReadOnlyDictionary<string, int> Values, IReadOnlyList<string> Unresolved)
    {
        public bool TryGet(string name, out int value) => Values.TryGetValue(name, out value);
    }

    /// <summary>
    /// Every `name = value` in the script, with derived values folded down to
    /// real numbers. Later assignments win, matching the order Lua would run
    /// them in.
    /// </summary>
    public static Table Build(string script)
    {
        // A script saved on Windows ends its lines with CR LF, and CR is neither
        // space nor tab, so it would sit between the value and the end-of-line
        // anchor and stop every pattern here from matching. Normalised once at
        // the door rather than tolerated in each regex.
        script = script.Replace("\r\n", "\n").Replace('\r', '\n');

        Dictionary<string, int> values = [];
        // name -> (base name, delta). Kept in encounter order so a later
        // assignment to the same name overwrites an earlier one either way.
        Dictionary<string, (string Base, int Delta)> derived = [];

        foreach (Match m in AssignmentPattern().Matches(script))
        {
            string name = m.Groups[1].Value;
            if (m.Groups[2].Success)
            {
                values[name] = int.Parse(m.Groups[2].Value);
                derived.Remove(name);
                continue;
            }

            // `altCfg = false` is an assignment, not arithmetic. Without this the
            // keyword reads as a base name nothing ever defines, and every
            // boolean in the script would be reported as an unresolved frame.
            string baseName = m.Groups[3].Value;
            if (baseName is "true" or "false" or "nil") continue;

            int delta = m.Groups[5].Success
                ? (m.Groups[4].Value == "-" ? -1 : 1) * int.Parse(m.Groups[5].Value)
                : 0;
            derived[name] = (baseName, delta);
            values.Remove(name);
        }

        for (int pass = 0; pass < MaxPasses && derived.Count > 0; pass++)
        {
            bool progressed = false;
            foreach (string name in derived.Keys.ToList())
            {
                (string baseName, int delta) = derived[name];
                if (!values.TryGetValue(baseName, out int baseValue)) continue;
                values[name] = baseValue + delta;
                derived.Remove(name);
                progressed = true;
            }
            if (!progressed) break;
        }

        return new Table(values, derived.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Reads one value that may be a literal or an expression, using
    /// <paramref name="table"/> for names. Null when it cannot be resolved.
    /// </summary>
    public static int? Resolve(string text, Table table)
    {
        Match m = ValuePattern().Match(text);
        if (!m.Success) return null;
        if (m.Groups[1].Success) return int.Parse(m.Groups[1].Value);
        if (!table.Values.TryGetValue(m.Groups[2].Value, out int baseValue)) return null;
        if (!m.Groups[4].Success) return baseValue;
        return baseValue + (m.Groups[3].Value == "-" ? -1 : 1) * int.Parse(m.Groups[4].Value);
    }
}

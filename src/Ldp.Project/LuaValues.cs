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
/// The operand order is not fixed either. An author who thinks of a move as a
/// DEADLINE writes the window backwards from it:
///
///     gap = 10
///     move[1] = {5679-gap, 5679, BUTTON1, 2}
///
/// so the number comes first and the name second. Same arithmetic, mirrored,
/// and a pattern that only accepts `name +/- number` misses every line of it.
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
    /// `name = 123`, `name = other`, `name = other +12`, or `name = 5679-other`.
    /// Anchored per line, trailing comment allowed. Anything more involved than
    /// one addition is left alone deliberately: guessing at arithmetic nobody
    /// writes in these scripts would risk inventing a frame number.
    /// </summary>
    [GeneratedRegex(@"^[ \t]*(?<lhs>[A-Za-z_]\w*)[ \t]*=[ \t]*(?:(?<num>\d+)[ \t]*(?<numOp>[+-])[ \t]*(?<numName>[A-Za-z_]\w*)|(?<lit>\d+)|(?<name>[A-Za-z_]\w*)(?:[ \t]*(?<op>[+-])[ \t]*(?<delta>\d+))?)[ \t]*(?:--.*)?$",
                    RegexOptions.Multiline)]
    private static partial Regex AssignmentPattern();

    /// <summary>An inline value: a literal, or a name and a number either way round.</summary>
    [GeneratedRegex(@"^[ \t]*(?:(?<num>\d+)[ \t]*(?<numOp>[+-])[ \t]*(?<numName>[A-Za-z_]\w*)|(?<lit>\d+)|(?<name>[A-Za-z_]\w*)(?:[ \t]*(?<op>[+-])[ \t]*(?<delta>\d+))?)[ \t]*$")]
    private static partial Regex ValuePattern();

    /// <summary>
    /// The expression forms above, reduced to one shape: take
    /// <see cref="Base"/>'s value, negate it when the name was subtracted FROM a
    /// number, then add <see cref="Delta"/>. `other +12`, `other -12`,
    /// `5679-other` and `5679+other` are all this.
    /// </summary>
    private readonly record struct Derived(string Base, int Delta, bool NegateBase)
    {
        public int Apply(int baseValue) => (NegateBase ? -baseValue : baseValue) + Delta;
    }

    /// <summary>Reads one match's right-hand side. Null value means it is a
    /// literal (already in <paramref name="literal"/>); null both means the
    /// right-hand side was a keyword rather than arithmetic.</summary>
    private static (int? Literal, Derived? Expression) ReadOperands(Match m)
    {
        if (m.Groups["lit"].Success) return (int.Parse(m.Groups["lit"].Value), null);

        if (m.Groups["num"].Success)
        {
            // `5679 - gap` / `5679 + gap`: the literal is the constant and the
            // name carries the sign.
            string name = m.Groups["numName"].Value;
            if (IsKeyword(name)) return (null, null);
            return (null, new Derived(name, int.Parse(m.Groups["num"].Value),
                                      m.Groups["numOp"].Value == "-"));
        }

        // `altCfg = false` is an assignment, not arithmetic. Without this the
        // keyword reads as a base name nothing ever defines, and every boolean
        // in the script would be reported as an unresolved frame.
        string baseName = m.Groups["name"].Value;
        if (IsKeyword(baseName)) return (null, null);

        int delta = m.Groups["delta"].Success
            ? (m.Groups["op"].Value == "-" ? -1 : 1) * int.Parse(m.Groups["delta"].Value)
            : 0;
        return (null, new Derived(baseName, delta, false));
    }

    private static bool IsKeyword(string name) => name is "true" or "false" or "nil";

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
        // Kept in encounter order so a later assignment to the same name
        // overwrites an earlier one either way.
        Dictionary<string, Derived> derived = [];

        foreach (Match m in AssignmentPattern().Matches(script))
        {
            string name = m.Groups["lhs"].Value;
            (int? literal, Derived? expression) = ReadOperands(m);

            if (literal is { } fixedValue)
            {
                values[name] = fixedValue;
                derived.Remove(name);
            }
            else if (expression is { } expr)
            {
                derived[name] = expr;
                values.Remove(name);
            }
        }

        for (int pass = 0; pass < MaxPasses && derived.Count > 0; pass++)
        {
            bool progressed = false;
            foreach (string name in derived.Keys.ToList())
            {
                Derived expr = derived[name];
                if (!values.TryGetValue(expr.Base, out int baseValue)) continue;
                values[name] = expr.Apply(baseValue);
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

        (int? literal, Derived? expression) = ReadOperands(m);
        if (literal is { } fixedValue) return fixedValue;
        if (expression is not { } expr) return null;
        return table.Values.TryGetValue(expr.Base, out int baseValue) ? expr.Apply(baseValue) : null;
    }
}

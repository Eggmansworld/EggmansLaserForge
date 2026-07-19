using System.Text;
using System.Text.RegularExpressions;

namespace Ldp.Project;

/// <summary>
/// Fills a community-authored .singe template with project content, producing
/// a complete runnable script the author never has to open in a text editor.
///
/// The marking contract is deliberately tiny so a game author can turn any
/// working script into a template:
///
///  1. Nothing needs marking for values the app owns: any `name = value`
///     line whose variable name the app recognizes (offsets, frames,
///     finalstage, MovieFPS, ...) is rewritten with the project's value.
///     Spacing and the line's trailing comment are preserved untouched.
///     `singeSetGameName("...")` and `MYDIR = ...` are special-cased.
///
///  2. `--@APP` appended to a line flags "a game creator must supply this".
///     If the app can fill it, it does (same as rule 1). If it can't, the
///     line passes through unchanged and a warning names it - that list is
///     the to-do list of settings the app should grow next.
///
///  3. Whole blocks the app regenerates are wrapped in marker lines:
///         --@APP-BEGIN deaths
///         ...anything here is replaced...
///         --@APP-END deaths
///     Block names: readme, deaths, levels, moves. The marker lines stay in
///     the output, so an exported script is itself a valid template.
///
///  4. Everything else - framework plumbing, helper comments, sounds,
///     sprites, scoring - passes through verbatim.
/// </summary>
public static partial class SingeTemplate
{
    public sealed record Result(string Script, List<string> Warnings);

    private static string? _defaultTemplate;

    /// <summary>
    /// The community template embedded in this assembly (Eggman's fully
    /// marked-up known-good script). Always available, so export never has
    /// to fall back to a from-scratch skeleton.
    /// </summary>
    public static string DefaultTemplate
    {
        get
        {
            if (_defaultTemplate == null)
            {
                using Stream stream = typeof(SingeTemplate).Assembly
                    .GetManifestResourceStream("Ldp.Project.default.template.singe")
                    ?? throw new InvalidOperationException("Embedded default template missing from build.");
                using var reader = new StreamReader(stream);
                _defaultTemplate = reader.ReadToEnd();
            }
            return _defaultTemplate;
        }
    }

    [GeneratedRegex(@"--\s*@APP-BEGIN\s+(\w+)")]
    private static partial Regex BlockBeginPattern();

    [GeneratedRegex(@"--\s*@APP-END\s+(\w+)")]
    private static partial Regex BlockEndPattern();

    [GeneratedRegex(@"^(\s*)([A-Za-z_]\w*)(\s*=\s*)(.*?)(\s*)$")]
    private static partial Regex AssignmentPattern();

    [GeneratedRegex(@"^(\s*)singeSetGameName\s*\(.*?\)(.*)$")]
    private static partial Regex GameNamePattern();

    [GeneratedRegex(@"^(\s*)MYDIR\s*=.*?$")]
    private static partial Regex MyDirPattern();

    [GeneratedRegex(@"^(\s*)dofile\s*\(\s*(?:BASEDIR|MYDIR)\s*\.\.\s*""/(\w+)/globals\.singe""\s*\)(.*)$")]
    private static partial Regex FrameworkDofilePattern();

    /// <summary>
    /// Reads every simple `name = value` assignment from a template so the UI
    /// can show the template's own defaults (e.g. scoring constants) as
    /// placeholder text. Table/multi-line values are ignored.
    /// </summary>
    public static Dictionary<string, string> ExtractDefaults(string template)
    {
        Dictionary<string, string> defaults = new(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in template.Replace("\r\n", "\n").Split('\n'))
        {
            int commentAt = rawLine.IndexOf("--", StringComparison.Ordinal);
            string code = commentAt >= 0 ? rawLine[..commentAt] : rawLine;
            Match m = AssignmentPattern().Match(code);
            if (m.Success)
            {
                string value = m.Groups[4].Value.Trim();
                if (value.Length > 0 && !value.Contains('{'))
                    defaults[m.Groups[2].Value] = value;
            }
        }
        return defaults;
    }

    public static Result Apply(LdpProject project, string template)
    {
        List<string> warnings = [];
        var gen = SingeGen.Build(project, warnings);
        var output = new StringBuilder();

        string[] lines = template.Replace("\r\n", "\n").Split('\n');
        // A trailing newline splits into a final empty element; dropping it
        // keeps repeated fills from growing the file by one line each pass.
        int lineCount = lines.Length;
        if (lineCount > 0 && lines[^1].Length == 0) lineCount--;

        int i = 0;
        while (i < lineCount)
        {
            string line = lines[i];

            Match blockBegin = BlockBeginPattern().Match(line);
            if (blockBegin.Success)
            {
                string blockName = blockBegin.Groups[1].Value.ToLowerInvariant();
                output.AppendLine(line); // keep the marker so exports stay templates

                // Skip template content up to the matching END marker.
                int end = i + 1;
                while (end < lineCount && !BlockEndPattern().IsMatch(lines[end])) end++;
                if (end >= lineCount)
                {
                    warnings.Add($"Template: '--@APP-BEGIN {blockName}' has no matching --@APP-END; " +
                                 "block left unfilled");
                    i++;
                    continue;
                }

                string? generated = blockName switch
                {
                    "readme" => gen.BuildReadme(),
                    "langopt" => gen.BuildLangOpt(),
                    "deaths" => gen.BuildDeathLines(),
                    "levels" => gen.BuildLevelLines(),
                    "moves" => gen.BuildMovesFunction(),
                    _ => null,
                };
                if (generated == null)
                {
                    warnings.Add($"Template: unknown block '{blockName}' - its original content was kept");
                    for (int k = i + 1; k < end; k++) output.AppendLine(lines[k]);
                }
                else
                {
                    output.AppendLine(generated);
                }

                output.AppendLine(lines[end]); // the END marker line
                i = end + 1;
                continue;
            }

            output.AppendLine(SubstituteLine(line, i + 1, gen, project, warnings));
            i++;
        }

        // Trailing newline normalization: match input's general shape.
        string script = output.ToString();
        return new Result(script, warnings);
    }

    private static string SubstituteLine(string line, int lineNumber, SingeGen gen,
                                         LdpProject project, List<string> warnings)
    {
        // Split off a trailing comment (first "--" occurrence) so values are
        // replaced without disturbing the author's helper text.
        int commentAt = line.IndexOf("--", StringComparison.Ordinal);
        string code = commentAt >= 0 ? line[..commentAt] : line;
        string comment = commentAt >= 0 ? line[commentAt..] : "";
        bool marked = comment.Contains("@APP", StringComparison.OrdinalIgnoreCase);

        // Pure comment lines (including markers on their own) pass through.
        if (code.TrimEnd().Length == 0) return line;

        Match gameName = GameNamePattern().Match(code);
        if (gameName.Success)
            return $"{gameName.Groups[1].Value}singeSetGameName(\"{project.Name}\"){gameName.Groups[2].Value}{comment}";

        Match myDir = MyDirPattern().Match(code);
        if (myDir.Success)
            return $"{myDir.Groups[1].Value}MYDIR = BASEDIR .. \"/\" .. \"{project.EffectiveGameFolder}\"" +
                   (comment.Length > 0 ? "\t" + comment : "");

        // The framework bootstrap follows the project's framework choice
        // (including the BASEDIR-vs-MYDIR anchor for global vs standalone), so
        // one template serves Framework/FrameworkKimmy/Structure games alike.
        Match dofile = FrameworkDofilePattern().Match(code);
        if (dofile.Success)
        {
            return dofile.Groups[1].Value + SingeGen.FrameworkDofile(project.Framework) +
                   dofile.Groups[3].Value + comment;
        }

        Match assign = AssignmentPattern().Match(code);
        if (assign.Success)
        {
            string name = assign.Groups[2].Value;
            string? value = gen.TryValue(name);
            if (value != null)
            {
                return assign.Groups[1].Value + name + assign.Groups[3].Value + value +
                       assign.Groups[5].Value + comment;
            }
            // Scoring is app-managed but optional: an unset one deliberately
            // keeps the template default, so that isn't worth a warning.
            bool optional = ScoringCatalog.Entries.Any(s => s.LuaName == name);
            if (marked && !optional)
                warnings.Add($"Template line {lineNumber}: '{name}' is marked @APP but the app has no " +
                             "setting for it yet - value left as-is");
            return line;
        }

        if (marked)
            warnings.Add($"Template line {lineNumber}: marked @APP but not recognized - left as-is");
        return line;
    }
}

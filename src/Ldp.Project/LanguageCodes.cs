using System;
using System.Collections.Generic;
using System.Linq;

namespace Ldp.Project;

/// <summary>
/// ISO 639-2 language-code helpers for the extra ".ogg language track" exports.
/// A track tagged <c>fre</c> becomes suffix <c>-fre</c> → <c>MyVideo-fre.ogg</c>,
/// and "French" as the display name for the Game Setup language list.
/// </summary>
public static class LanguageCodes
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "English",
        ["fre"] = "French", ["fra"] = "French",
        ["ger"] = "German", ["deu"] = "German",
        ["spa"] = "Spanish",
        ["ita"] = "Italian",
        ["rus"] = "Russian",
        ["jpn"] = "Japanese",
        ["chi"] = "Chinese", ["zho"] = "Chinese",
        ["kor"] = "Korean",
        ["por"] = "Portuguese",
        ["dut"] = "Dutch", ["nld"] = "Dutch",
        ["pol"] = "Polish",
        ["cze"] = "Czech", ["ces"] = "Czech",
        ["slo"] = "Slovak", ["slk"] = "Slovak",
        ["hun"] = "Hungarian",
        ["tha"] = "Thai",
        ["hin"] = "Hindi",
        ["dan"] = "Danish",
        ["fin"] = "Finnish",
        ["nor"] = "Norwegian",
        ["swe"] = "Swedish",
        ["tur"] = "Turkish",
        ["ara"] = "Arabic",
        ["heb"] = "Hebrew",
        ["gre"] = "Greek", ["ell"] = "Greek",
        ["ukr"] = "Ukrainian",
        ["vie"] = "Vietnamese",
        ["ind"] = "Indonesian",
        ["may"] = "Malay", ["msa"] = "Malay",
        ["rum"] = "Romanian", ["ron"] = "Romanian",
        ["bul"] = "Bulgarian",
        ["hrv"] = "Croatian",
        ["srp"] = "Serbian",
        ["slv"] = "Slovenian",
        ["cat"] = "Catalan",
        ["ice"] = "Icelandic", ["isl"] = "Icelandic",
        ["est"] = "Estonian",
        ["lav"] = "Latvian",
        ["lit"] = "Lithuanian",
    };

    /// <summary>Human name for a code ("fre" → "French"); unknown codes are shown
    /// capitalized as-is so nothing is ever nameless.</summary>
    public static string DisplayName(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Equals("und", StringComparison.OrdinalIgnoreCase))
            return "Unknown";
        if (Names.TryGetValue(code.Trim(), out string? name)) return name;
        string t = code.Trim().ToLowerInvariant();
        return char.ToUpperInvariant(t[0]) + t[1..];
    }

    /// <summary>The file suffix for a track: <c>-</c> + its language code
    /// (<c>-fre</c>). Untagged tracks fall back to their audio ordinal
    /// (<c>-a3</c> for the third audio stream) so the name stays unique.</summary>
    public static string SuffixFor(AudioTrackInfo track)
    {
        string lang = track.Language.Trim().ToLowerInvariant();
        bool usable = lang.Length is >= 2 and <= 4 && lang != "und" && lang.All(char.IsAsciiLetter);
        return "-" + (usable ? lang : $"a{track.Ordinal + 1}");
    }

    /// <summary>Makes a suffix unique among <paramref name="taken"/> (two Spanish
    /// tracks become <c>-spa</c> and <c>-spa2</c>) and records the result.</summary>
    public static string Unique(string suffix, ISet<string> taken)
    {
        string candidate = suffix;
        for (int n = 2; !taken.Add(candidate); n++)
            candidate = suffix + n;
        return candidate;
    }
}

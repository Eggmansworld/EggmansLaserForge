using System;
using System.Collections.Generic;
using System.IO;

namespace Ldp.App;

/// <summary>
/// Installs the two global Singe frameworks (Framework, FrameworkKimmy) that
/// ship with the app into a Hypseus install's singe/ folder. End users would
/// otherwise have to download and place these by hand — a common source of
/// confusion — so the app just puts them where they belong.
/// </summary>
public static class FrameworkInstaller
{
    private static readonly string[] GlobalFrameworks = ["Framework", "FrameworkKimmy"];

    private static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "Frameworks");

    /// <summary>
    /// Copies any global framework not already present into &lt;hypseusRoot&gt;/singe/.
    /// Existing frameworks are left untouched (never clobber a user's copy).
    /// Returns the names of the frameworks that were installed.
    /// </summary>
    public static List<string> EnsureInstalled(string hypseusRoot)
    {
        List<string> installed = [];
        string singeDir = Path.Combine(hypseusRoot, "singe");
        Directory.CreateDirectory(singeDir);

        foreach (string fw in GlobalFrameworks)
        {
            string src = Path.Combine(BundledRoot, fw);
            string dst = Path.Combine(singeDir, fw);
            if (!Directory.Exists(src)) continue;                       // not bundled (dev build?)
            if (File.Exists(Path.Combine(dst, "globals.singe"))) continue; // already there

            CopyDirectory(src, dst);
            installed.Add(fw);
        }
        return installed;
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (string file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dst, Path.GetRelativePath(src, file)), overwrite: false);
    }
}

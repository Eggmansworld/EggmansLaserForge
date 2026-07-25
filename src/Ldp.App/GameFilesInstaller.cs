using System;
using System.Collections.Generic;
using System.IO;
using Ldp.Project;

namespace Ldp.App;

/// <summary>
/// Supplies the app's bundled support-file set to <see cref="SupportFiles"/>
/// (the copy rules, including never overwriting, live there and are tested).
/// Pairs with <see cref="FrameworkInstaller"/>, which does the same job for the
/// global frameworks in the Hypseus singe/ folder.
/// </summary>
public static class GameFilesInstaller
{
    private static string BundledRoot => Path.Combine(AppContext.BaseDirectory, "DefaultGameFiles");

    /// <summary>Whether the app has the bundled set at all (a dev build may not).</summary>
    public static bool Available => Directory.Exists(BundledRoot);

    /// <summary>Fills in whatever support files the game folder is missing.</summary>
    public static List<string> EnsureInstalled(string gameFolder) =>
        SupportFiles.InstallFrom(BundledRoot, gameFolder);

    public static string Describe(IReadOnlyList<string> added) => SupportFiles.Describe(added);
}

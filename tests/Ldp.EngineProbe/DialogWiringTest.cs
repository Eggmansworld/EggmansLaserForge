namespace Ldp.EngineProbe;

/// <summary>
/// Guards the one way an Avalonia dialog can look correct, compile clean, and
/// still be dead on arrival.
///
/// Avalonia's source generator writes the method that binds every Name="..."
/// control to its field:
///
///     public void InitializeComponent(bool loadXaml = true)
///     {
///         if (loadXaml) AvaloniaXamlLoader.Load(this);
///         HeadlineText = this.FindNameScope()?.Find&lt;TextBlock&gt;("HeadlineText");
///         ...
///     }
///
/// Hand-writing the parameterless overload —
///
///     private void InitializeComponent() =&gt; AvaloniaXamlLoader.Load(this);
///
/// — is not an error and not a warning. Overload resolution simply prefers the
/// exact match, so the markup loads, the window renders, and every named field
/// stays null. The first line of the constructor that touches one throws a
/// NullReferenceException naming nothing.
///
/// That shipped in 0.1.15 in both dialogs added that release, and turned an
/// import that had already succeeded into "Import failed: Object reference not
/// set to an instance of an object".
/// </summary>
public static class DialogWiringTest
{
    public static void Run(Action<string, bool> Check)
    {
        string[] roots =
        [
            @"C:\Eggmansworld\EggmansLaserForge\src\Ldp.App",
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Ldp.App"),
        ];
        if (roots.FirstOrDefault(Directory.Exists) is not { } appDir)
        {
            Console.WriteLine("  dialog wiring: SKIPPED (src/Ldp.App not found)");
            return;
        }

        List<string> shadowed = [];
        foreach (string file in Directory.EnumerateFiles(appDir, "*.axaml.cs"))
            foreach (string line in File.ReadAllLines(file))
            {
                // A declaration, not the call: the call has no return type in
                // front of it, the declaration always does.
                string t = line.Trim();
                if (t.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!t.Contains("InitializeComponent()", StringComparison.Ordinal)) continue;
                if (t.Contains("void InitializeComponent()", StringComparison.Ordinal))
                    shadowed.Add(Path.GetFileName(file));
            }

        Check("dialogs: no hand-written InitializeComponent() shadows the generated one" +
              (shadowed.Count > 0 ? $" (found in {string.Join(", ", shadowed.Distinct())})" : ""),
              shadowed.Count == 0);
    }
}

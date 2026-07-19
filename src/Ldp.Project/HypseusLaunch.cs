namespace Ldp.Project;

/// <summary>
/// Builds the command line that runs a game in Hypseus Singe. Paths are
/// relative to the Hypseus install root (where hypseus.exe lives), matching
/// how games are launched by hand.
/// </summary>
public static class HypseusLaunch
{
    /// <summary>Arguments after hypseus.exe for the given game folder.</summary>
    public static string Arguments(string gameFolder) =>
        $"singe vldp -framefile singe\\{gameFolder}\\{gameFolder}.txt " +
        $"-script singe\\{gameFolder}\\{gameFolder}.singe " +
        "-fullscreen -linear_scale -volume_nonvldp 40 -volume_vldp 64";

    /// <summary>The full, copy-pasteable command.</summary>
    public static string Command(string gameFolder) => "hypseus.exe " + Arguments(gameFolder);
}

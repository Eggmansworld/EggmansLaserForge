using System;
using System.IO;
using System.Text.Json;

namespace Ldp.App;

/// <summary>Tiny persisted app state (last project path etc.).</summary>
public sealed class AppSettings
{
    public string? LastProjectPath { get; set; }

    /// <summary>Last Hypseus Singe install root chosen, to prefill the New Project wizard.</summary>
    public string? HypseusRoot { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EggmansLaserdiscPublisher", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception) { /* corrupted settings are not worth crashing over */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception) { }
    }
}

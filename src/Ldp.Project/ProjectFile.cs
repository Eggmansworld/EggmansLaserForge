using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ldp.Project;

/// <summary>
/// Atomic load/save of .ldproj files. Saves write to a temp file first and
/// swap it in (keeping a .bak of the previous version), so a crash mid-write
/// can never destroy hours of work.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".ldproj";

    // WhenWritingNull only: value-type defaults (enum 0, int 0) MUST be
    // written, or properties whose C# initializer differs from default(T)
    // deserialize wrong (this corrupted Start nodes into clip nodes once).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes a project to its canonical JSON (used for undo snapshots too).</summary>
    public static string Serialize(LdpProject project) => JsonSerializer.Serialize(project, JsonOptions);

    public static LdpProject Deserialize(string json) =>
        JsonSerializer.Deserialize<LdpProject>(json, JsonOptions)
        ?? throw new InvalidDataException("Invalid project JSON.");

    public static void Save(LdpProject project, string path)
    {
        string json = JsonSerializer.Serialize(project, JsonOptions);
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);

        if (File.Exists(path))
            File.Replace(tempPath, path, path + ".bak");
        else
            File.Move(tempPath, path);
    }

    public static LdpProject Load(string path)
    {
        string json = File.ReadAllText(path);
        LdpProject project = JsonSerializer.Deserialize<LdpProject>(json, JsonOptions)
            ?? throw new InvalidDataException($"'{path}' does not contain a valid project.");
        return project;
    }

    /// <summary>Resolves a VideoSource path relative to the project file location.</summary>
    public static string ResolveVideoPath(string projectPath, VideoSource video) =>
        System.IO.Path.IsPathRooted(video.Path)
            ? video.Path
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(
                  System.IO.Path.GetDirectoryName(projectPath)!, video.Path));

    /// <summary>Stores a video path relative to the project when possible.</summary>
    public static string RelativizeVideoPath(string projectPath, string videoPath)
    {
        string projectDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(projectPath))!;
        string relative = System.IO.Path.GetRelativePath(projectDir, System.IO.Path.GetFullPath(videoPath));
        // GetRelativePath returns the input unchanged only when roots differ.
        return relative.StartsWith("..", StringComparison.Ordinal) && System.IO.Path.IsPathRooted(relative)
            ? videoPath
            : relative;
    }

    /// <summary>Directory for project sidecar data (clip thumbnails etc.), created on demand.</summary>
    public static string CacheDir(string projectPath)
    {
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(projectPath)!,
            System.IO.Path.GetFileNameWithoutExtension(projectPath) + ".ldpcache");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

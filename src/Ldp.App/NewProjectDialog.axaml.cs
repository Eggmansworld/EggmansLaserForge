using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Ldp.Project;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Ldp.App;

/// <summary>
/// Guides the author through creating a game folder inside their Hypseus
/// Singe install. On success, <see cref="Result"/> holds the paths the app
/// then uses for the project (which lives in that folder so export and test
/// launch always know where everything is).
/// </summary>
public partial class NewProjectDialog : Window
{
    public sealed record CreateResult(string HypseusRoot, string GameFolder, string ProjectPath);

    public CreateResult? Result { get; private set; }

    private string? _hypseusRoot;

    public NewProjectDialog()
    {
        InitializeComponent();
    }

    public NewProjectDialog(string? initialHypseusRoot) : this()
    {
        if (!string.IsNullOrWhiteSpace(initialHypseusRoot) && IsHypseusRoot(initialHypseusRoot))
            SetHypseusRoot(initialHypseusRoot);
    }

    private static bool IsHypseusRoot(string dir) =>
        File.Exists(Path.Combine(dir, "hypseus.exe"));

    private void SetHypseusRoot(string dir)
    {
        _hypseusRoot = dir;
        HypseusStatus.Text = "✓ " + dir;
        UpdatePreview();
    }

    private async void OnLocateHypseus(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select your Hypseus Singe folder (the one containing hypseus.exe)",
        });
        string? dir = folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
        if (dir == null) return;

        if (!IsHypseusRoot(dir))
        {
            // Be forgiving: maybe they picked the parent or the singe folder.
            string? found = ProbeForHypseus(dir);
            if (found == null)
            {
                ErrorText.Text = $"hypseus.exe was not found in '{dir}'. " +
                                 "Pick the folder that contains hypseus.exe.";
                ErrorText.IsVisible = true;
                return;
            }
            dir = found;
        }
        ErrorText.IsVisible = false;
        SetHypseusRoot(dir);
    }

    private static string? ProbeForHypseus(string dir)
    {
        if (IsHypseusRoot(dir)) return dir;
        // One level up (user picked singe/) or down (picked the parent).
        try
        {
            string? parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (parent != null && IsHypseusRoot(parent)) return parent;
            foreach (string sub in Directory.EnumerateDirectories(dir))
                if (IsHypseusRoot(sub)) return sub;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private void OnFolderNameChanged(object? sender, TextChangedEventArgs e)
    {
        // Live-convert only: turn invalid characters (spaces especially) into
        // underscores in place, but keep what the user typed otherwise —
        // including trailing underscores while they're still typing. The final
        // trimming happens in SanitizeFolder at create time.
        string raw = FolderNameBox.Text ?? "";
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
            sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        string clean = sb.ToString();
        if (clean != raw)
        {
            int caret = FolderNameBox.CaretIndex;
            FolderNameBox.Text = clean;
            FolderNameBox.CaretIndex = Math.Min(caret, clean.Length);
        }
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        string folder = LdpProject.SanitizeFolder(FolderNameBox.Text ?? "");
        bool hasName = !string.IsNullOrWhiteSpace(FolderNameBox.Text);
        bool ready = _hypseusRoot != null && hasName;
        CreateButton.IsEnabled = ready;

        if (_hypseusRoot != null && hasName)
        {
            string gameDir = Path.Combine(_hypseusRoot, "singe", folder);
            PathPreview.Text = $"Game folder:  {gameDir}\nProject file:  {Path.Combine(gameDir, folder + ".ldproj")}";
        }
        else
        {
            PathPreview.Text = _hypseusRoot == null
                ? "Locate your Hypseus folder above to continue."
                : "Enter a game folder name.";
        }
    }

    private void OnOpenDownload(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://github.com/DirtBagXon/hypseus-singe/releases",
                UseShellExecute = true,
            });
        }
        catch (Exception) { }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        if (_hypseusRoot == null) return;
        string folder = LdpProject.SanitizeFolder(FolderNameBox.Text ?? "");
        string singeDir = Path.Combine(_hypseusRoot, "singe");
        string gameDir = Path.Combine(singeDir, folder);
        string projectPath = Path.Combine(gameDir, folder + ".ldproj");

        try
        {
            if (Directory.Exists(gameDir) && Directory.EnumerateFileSystemEntries(gameDir).Any())
            {
                ErrorText.Text = $"'{gameDir}' already exists and isn't empty. " +
                                 "Pick a different name, or open the existing project instead.";
                ErrorText.IsVisible = true;
                return;
            }
            Directory.CreateDirectory(gameDir);
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Could not create the game folder: " + ex.Message;
            ErrorText.IsVisible = true;
            return;
        }

        Result = new CreateResult(_hypseusRoot, folder, projectPath);
        Close();
    }
}

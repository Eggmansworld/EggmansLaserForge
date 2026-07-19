using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Ldp.Project;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Ldp.App;

/// <summary>
/// Shows the ready-to-run Hypseus command for the exported game, launches it,
/// and links to the log (Hypseus closes on error with no on-screen message).
/// </summary>
public partial class TestHypseusDialog : Window
{
    private readonly string _hypseusRoot;
    private readonly string _gameFolder;

    public TestHypseusDialog()
    {
        InitializeComponent();
        _hypseusRoot = "";
        _gameFolder = "";
    }

    public TestHypseusDialog(string hypseusRoot, string gameFolder, IReadOnlyList<string> warnings) : this()
    {
        _hypseusRoot = hypseusRoot;
        _gameFolder = gameFolder;
        CommandBox.Text = HypseusLaunch.Command(gameFolder);

        List<string> blocking = warnings
            .Where(w => w.Contains("framework") || w.Contains("AUTHOR is required") ||
                        w.Contains("Required slot not assigned"))
            .ToList();
        if (blocking.Count > 0)
        {
            WarnText.Text = "Heads up before it will run: " + string.Join(" · ", blocking.Take(3)) +
                            (blocking.Count > 3 ? $" (+{blocking.Count - 3} more)" : "");
            WarnText.IsVisible = true;
        }
    }

    private void OnRun(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(_hypseusRoot, "hypseus.exe"),
                Arguments = HypseusLaunch.Arguments(_gameFolder),
                WorkingDirectory = _hypseusRoot,
                UseShellExecute = false,
            });
            RunButton.Content = "▶ Running…";
        }
        catch (Exception ex)
        {
            WarnText.Text = "Failed to launch: " + ex.Message + " — check the log.";
            WarnText.IsVisible = true;
        }
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is { } clip) await clip.SetValueAsync(DataFormat.Text, CommandBox.Text ?? "");
    }

    private void OnOpenLogFolder(object? sender, RoutedEventArgs e) =>
        OpenInShell(Path.Combine(_hypseusRoot, "logs"));

    private void OnOpenLog(object? sender, RoutedEventArgs e) =>
        OpenInShell(Path.Combine(_hypseusRoot, "logs", "hypseus.log"));

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OpenInShell(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                WarnText.Text = $"'{path}' doesn't exist yet — run the game once to create the log.";
                WarnText.IsVisible = true;
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            WarnText.Text = "Could not open: " + ex.Message;
            WarnText.IsVisible = true;
        }
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;
using System.Diagnostics;
using System.Reflection;

namespace Ldp.App;

public partial class AboutDialog : Window
{
    private const string ProjectUrl = "https://github.com/Eggmansworld/EggmansLaserForge";
    private const string CoffeeUrl = "https://buymeacoffee.com/eggmansworld";

    public AboutDialog()
    {
        InitializeComponent();

        Version? v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v != null) VersionText.Text = $"Version {v.Major}.{v.Minor}.{v.Build}";
    }

    private void OnOpenProject(object? sender, PointerPressedEventArgs e) => Open(ProjectUrl);
    private void OnBuyCoffee(object? sender, RoutedEventArgs e) => Open(CoffeeUrl);
    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception) { /* opening a browser is best-effort */ }
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Ldp.App;

/// <summary>Asks how a batch of scenes should land on the storyboard:
/// auto-linked in scenes-list order, or loose. Null result = cancelled.</summary>
public partial class AddScenesDialog : Window
{
    public bool? LinkInOrder { get; private set; }

    public AddScenesDialog()
    {
        InitializeComponent();
    }

    public AddScenesDialog(int sceneCount) : this()
    {
        MessageText.Text = $"Add {sceneCount} scenes to the storyboard?";
        Opened += (_, _) => LinkButton.Focus();
    }

    private void OnLinked(object? sender, RoutedEventArgs e) { LinkInOrder = true; Close(); }
    private void OnUnlinked(object? sender, RoutedEventArgs e) { LinkInOrder = false; Close(); }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

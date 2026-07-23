using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Ldp.App;

/// <summary>Small shared "give it a new name" prompt. <see cref="Result"/> is the
/// entered text (untrimmed beyond outer whitespace), or null when cancelled.</summary>
public partial class RenameDialog : Window
{
    public string? Result { get; private set; }

    public RenameDialog()
    {
        InitializeComponent();
    }

    public RenameDialog(string title, string prompt, string initial) : this()
    {
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Opened += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnOk(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        string text = (InputBox.Text ?? "").Trim();
        if (text.Length == 0)
        {
            ErrorText.Text = "Enter a name (or Cancel).";
            ErrorText.IsVisible = true;
            return;
        }
        Result = text;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

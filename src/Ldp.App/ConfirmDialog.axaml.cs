using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Ldp.App;

/// <summary>
/// Yes/no for an edit that touches a lot at once. Returns true only on the
/// confirm button — closing it any other way is a no.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string headline, string body, string confirmLabel = "Go ahead") : this()
    {
        HeadlineText.Text = headline;
        BodyText.Text = body;
        OkButton.Content = confirmLabel;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

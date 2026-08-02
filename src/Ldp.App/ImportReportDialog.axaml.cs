using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Ldp.App;

/// <summary>
/// Shown after an import that had anything to say.
///
/// An import is the one operation that quietly rewrites an author's data: it
/// corrects level intros that swallow gameplay, resolves frames written as
/// arithmetic, and flags moves this editor cannot author. The status bar is the
/// wrong place for that — it is one line, it scrolls away behind the thumbnail
/// pass that follows, and the author is usually watching the storyboard fill in.
/// So the same lines that go to the log get a stop-and-read as well.
/// </summary>
public partial class ImportReportDialog : Window
{
    private string _warnings = "";

    public ImportReportDialog()
    {
        InitializeComponent();
    }

    public ImportReportDialog(string fileName, string summary, IReadOnlyList<string> warnings) : this()
    {
        HeadlineText.Text = warnings.Count == 1
            ? $"Imported {fileName} — 1 thing to look at"
            : $"Imported {fileName} — {warnings.Count} things to look at";
        SubText.Text = $"{summary}. Nothing was lost; these are notes about what the script said and what " +
                       "was adjusted. They're in the log too.";
        _warnings = string.Join(Environment.NewLine, warnings.Select((w, i) => $"{i + 1}.  {w}"));
        WarningsText.Text = _warnings;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } clipboard) return;
        await clipboard.SetValueAsync(DataFormat.Text, _warnings);
        CopyButton.Content = "Copied";
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

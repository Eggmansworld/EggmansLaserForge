using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ldp.Project;
using System.Linq;

namespace Ldp.App;

/// <summary>
/// Edits a scene's name and its global frame boundaries in place. Recreating a
/// scene to change its range would throw away every move authored inside it,
/// which is exactly what an author needs to avoid when a boundary turns out to
/// be one frame wrong.
///
/// Moves are deliberately left where they are: their frame numbers are absolute
/// positions in the video, so shifting a scene edge must not drag them along.
/// </summary>
public partial class EditSceneDialog : Window
{
    private LdpProject? _project;
    private Clip? _clip;

    /// <summary>The edited values, or null when cancelled.</summary>
    public (string Name, int Start, int End)? Result { get; private set; }

    public EditSceneDialog()
    {
        InitializeComponent();
    }

    public EditSceneDialog(LdpProject project, Clip clip) : this()
    {
        _project = project;
        _clip = clip;
        NameBox.Text = clip.Name;
        StartBox.Text = clip.StartFrame.ToString();
        EndBox.Text = clip.EndFrame.ToString();

        NameBox.TextChanged += (_, _) => Describe();
        StartBox.TextChanged += (_, _) => Describe();
        EndBox.TextChanged += (_, _) => Describe();
        Describe();

        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    /// <summary>Parsed values, or null when either box isn't a number.</summary>
    private (int Start, int End)? Frames() =>
        int.TryParse((StartBox.Text ?? "").Trim(), out int start) &&
        int.TryParse((EndBox.Text ?? "").Trim(), out int end)
            ? (start, end)
            : null;

    /// <summary>
    /// Live read-out of what the entered range actually means: how long it is,
    /// which video it lands in, and which moves it would leave stranded.
    /// </summary>
    private void Describe()
    {
        if (_project == null || _clip == null) return;
        if (Frames() is not { } f)
        {
            InfoText.Text = "Enter whole frame numbers.";
            return;
        }

        var lines = new System.Collections.Generic.List<string>();
        if (f.End >= f.Start)
            lines.Add($"length   {f.End - f.Start + 1} frames");

        int videoIndex = _project.VideoIndexOf(f.Start);
        lines.Add(videoIndex < 0
            ? "video    *** frame is past every video, or in a gap ***"
            : $"video    [{videoIndex}] {System.IO.Path.GetFileName(_project.Videos[videoIndex].Path)}");

        int outside = _clip.Interactions.Count(m => m.Frame < f.Start || m.Frame > f.End);
        if (_clip.Interactions.Count > 0)
            lines.Add(outside == 0
                ? $"moves    all {_clip.Interactions.Count} stay inside the range"
                : $"moves    {outside} of {_clip.Interactions.Count} would fall OUTSIDE the range");

        // Frame 0 is the framework's "not set" sentinel and its seek-to-scene
        // guard can never fire for it, so a scene must not begin there.
        if (f.Start == 0)
            lines.Add("warning  frame 0 is the framework's \"not set\" value - start at 1 or later");

        InfoText.Text = string.Join("\n", lines);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnOk(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        if (_project == null) return;

        string name = (NameBox.Text ?? "").Trim();
        if (name.Length == 0) { Fail("Enter a scene name (or Cancel)."); return; }

        if (Frames() is not { } f) { Fail("Start and end must both be whole frame numbers."); return; }
        if (f.Start < 0) { Fail("The start frame cannot be negative."); return; }
        if (f.End < f.Start) { Fail("The end frame must not come before the start frame."); return; }

        int startVideo = _project.VideoIndexOf(f.Start);
        int endVideo = _project.VideoIndexOf(f.End);
        if (startVideo < 0) { Fail($"Frame {f.Start} lands in a gap or past the last video."); return; }
        if (endVideo < 0) { Fail($"Frame {f.End} lands in a gap or past the last video."); return; }

        // A scene is played out of one video, so a range that straddles a
        // boundary could never play back correctly.
        if (startVideo != endVideo)
        {
            Fail($"The range crosses a video boundary ({System.IO.Path.GetFileName(_project.Videos[startVideo].Path)} " +
                 $"to {System.IO.Path.GetFileName(_project.Videos[endVideo].Path)}). A scene has to sit inside one video.");
            return;
        }

        Result = (name, f.Start, f.End);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}

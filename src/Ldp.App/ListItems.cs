using Avalonia.Media.Imaging;
using Ldp.Project;

namespace Ldp.App;

/// <summary>Video list row.</summary>
public sealed class VideoItem
{
    public required int Index { get; init; }
    public required VideoSource Source { get; init; }
    public string Title => System.IO.Path.GetFileName(Source.Path);
    public string Range
    {
        get
        {
            int digits = System.Math.Max(6, (Source.GlobalBase + Source.PictureCount - 1).ToString().Length);
            return $"{Source.GlobalBase.ToString().PadLeft(digits, '0')} – " +
                   $"{(Source.GlobalBase + Source.PictureCount - 1).ToString().PadLeft(digits, '0')}";
        }
    }
}

/// <summary>Interactions panel row.</summary>
public sealed class InteractionItem : System.ComponentModel.INotifyPropertyChanged
{
    private static readonly Avalonia.Media.IBrush NormalBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E4E7F0"));
    private static readonly Avalonia.Media.IBrush WarnBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F0605C"));
    private static readonly Avalonia.Media.IBrush ActiveBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FFE24C")); // bright yellow, current
    private static readonly Avalonia.Media.IBrush UpcomingBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E8B04C")); // amber, on deck

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public required InteractionMarker Marker { get; init; }
    public required bool Warn { get; init; }

    /// <summary>Playhead is inside this move's window right now.</summary>
    private bool _active;
    public bool Active
    {
        get => _active;
        set { if (_active != value) { _active = value; Notify(); } }
    }

    /// <summary>The next move the playhead will reach (the one "on deck").</summary>
    private bool _upcoming;
    public bool Upcoming
    {
        get => _upcoming;
        set { if (_upcoming != value) { _upcoming = value; Notify(); } }
    }

    public string Display =>
        $"{(_active ? "▶ " : Warn ? "⚠ " : "")}{Marker.Frame.ToString().PadLeft(6, '0')}" +
        (Marker.EndFrameOverride is { } end ? $"–{end.ToString().PadLeft(6, '0')}" : "") +
        $"  {Glyph(Marker.Input)} {Marker.Input}";

    public Avalonia.Media.IBrush Brush =>
        _active ? ActiveBrush : _upcoming ? UpcomingBrush : Warn ? WarnBrush : NormalBrush;

    public Avalonia.Media.FontWeight Weight =>
        _active ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal;

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new(nameof(Active)));
        PropertyChanged?.Invoke(this, new(nameof(Upcoming)));
        PropertyChanged?.Invoke(this, new(nameof(Brush)));
        PropertyChanged?.Invoke(this, new(nameof(Weight)));
        PropertyChanged?.Invoke(this, new(nameof(Display)));
    }

    public static string Glyph(InputKind input) => input switch
    {
        InputKind.Up => "↑",
        InputKind.Down => "↓",
        InputKind.Left => "←",
        InputKind.Right => "→",
        InputKind.Button1 => "🅐",
        InputKind.Button2 => "🅑",
        InputKind.AnyDirection => "✛",
        _ => "⏭",
    };
}

/// <summary>Clip bin row.</summary>
public sealed class ClipItem
{
    public required Clip Clip { get; init; }
    public Bitmap? Thumbnail { get; set; }
    public string Name => Clip.Name;
    public string Range =>
        $"{Clip.StartFrame.ToString().PadLeft(6, '0')} – {Clip.EndFrame.ToString().PadLeft(6, '0')}  ({Clip.FrameCount} fr)";
}

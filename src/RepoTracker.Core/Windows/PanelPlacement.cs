namespace RepoTracker.Core.Windows;

public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public enum TaskbarEdge { Bottom, Left, Top, Right }

/// <summary>Coordinates are physical pixels; logical design dimensions scale once, at this boundary.</summary>
public static class PanelPlacement
{
    public static TaskbarEdge FindTaskbarEdge(PixelRect monitor, PixelRect workArea)
    {
        var insets = new (TaskbarEdge Edge, double Size)[]
        {
            (TaskbarEdge.Bottom, monitor.Bottom - workArea.Bottom),
            (TaskbarEdge.Top, workArea.Y - monitor.Y),
            (TaskbarEdge.Left, workArea.X - monitor.X),
            (TaskbarEdge.Right, monitor.Right - workArea.Right)
        };
        // Auto-hidden taskbars may reserve no work area. Bottom-right remains a visible fallback.
        return insets.MaxBy(item => item.Size).Edge;
    }

    public static PixelRect Place(PixelRect workArea, double dpi, TaskbarEdge edge,
        PixelRect? previous = null, double uiScale = 1)
    {
        if (!double.IsFinite(dpi) || dpi <= 0 || !double.IsFinite(uiScale) || uiScale <= 0 || !IsValid(workArea))
            throw new ArgumentOutOfRangeException(nameof(workArea));

        var scale = dpi / 96;
        var margin = Math.Min(12 * scale, Math.Min(workArea.Width, workArea.Height) / 4);
        var width = Math.Min(760 * scale * uiScale, workArea.Width - 2 * margin);
        var height = Math.Min(760 * scale * uiScale, workArea.Height - 2 * margin);
        var x = previous?.X ?? (edge == TaskbarEdge.Left ? workArea.X + margin : workArea.Right - width - margin);
        var y = previous?.Y ?? (edge == TaskbarEdge.Top ? workArea.Y + margin : workArea.Bottom - height - margin);
        return new PixelRect(
            Math.Clamp(x, workArea.X + margin, workArea.Right - width - margin),
            Math.Clamp(y, workArea.Y + margin, workArea.Bottom - height - margin), width, height);
    }

    private static bool IsValid(PixelRect value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) &&
        double.IsFinite(value.Width) && double.IsFinite(value.Height) && value.Width > 0 && value.Height > 0;
}

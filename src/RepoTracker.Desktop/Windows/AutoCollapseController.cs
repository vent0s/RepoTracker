using System.Windows;
using System.Windows.Threading;

namespace RepoTracker.Desktop.Windows;

public sealed class AutoCollapseController : IDisposable
{
    private readonly Window window;
    private readonly Func<bool> keepExpanded;
    private readonly Action collapse;
    private readonly DispatcherTimer timer;
    private int suspensions;
    public bool IsSuspended => suspensions > 0;

    public AutoCollapseController(Window window, Func<bool> keepExpanded, Action collapse)
    {
        this.window = window;
        this.keepExpanded = keepExpanded;
        this.collapse = collapse;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background,
            (_, _) => Evaluate(), window.Dispatcher);
        timer.Stop();
        window.Deactivated += OnDeactivated;
        window.Activated += OnActivated;
    }

    public IDisposable Suspend()
    {
        suspensions++;
        timer.Stop();
        return new Suspension(this);
    }

    public void Schedule()
    {
        timer.Stop();
        timer.Start();
    }

    private void Evaluate()
    {
        timer.Stop();
        if (!window.IsActive && window.IsVisible && window.WindowState == WindowState.Normal &&
            !keepExpanded() && !IsSuspended && !NativeWindows.ForegroundBelongsToThisProcess())
            collapse();
    }

    private void OnDeactivated(object? sender, EventArgs e) => Schedule();
    private void OnActivated(object? sender, EventArgs e) => timer.Stop();

    public void Dispose()
    {
        timer.Stop();
        window.Deactivated -= OnDeactivated;
        window.Activated -= OnActivated;
    }

    private sealed class Suspension(AutoCollapseController owner) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            owner.suspensions--;
            owner.Schedule();
        }
    }
}

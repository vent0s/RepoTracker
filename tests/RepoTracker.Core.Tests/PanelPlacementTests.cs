using RepoTracker.Core.Windows;

namespace RepoTracker.Core.Tests;

public class PanelPlacementTests
{
    [Theory]
    [InlineData(96, 1.25, 950)]
    [InlineData(144, 1.5, 1710)]
    public void UserScaleCombinesWithDpiAndClampsToWorkArea(double dpi, double scale, double expected)
    {
        var result = PanelPlacement.Place(new(0, 0, 3840, 2080), dpi, TaskbarEdge.Bottom, uiScale: scale);
        Assert.Equal(expected, result.Width);
        var small = PanelPlacement.Place(new(-800, 0, 800, 560), dpi, TaskbarEdge.Bottom, uiScale: scale);
        Assert.True(small.X >= -800 && small.Right <= 0 && small.Y >= 0 && small.Bottom <= 560);
    }
    [Theory]
    [InlineData(96, 1920, 1040, 760)]
    [InlineData(144, 2560, 1400, 1140)]
    [InlineData(192, 3840, 2080, 1520)]
    public void LogicalDesignSizeScalesExactlyOnce(double dpi, double width, double height, double expected)
    {
        var result = PanelPlacement.Place(new(0, 0, width, height), dpi, TaskbarEdge.Bottom);
        Assert.Equal(expected, result.Width);
        Assert.Equal(expected, result.Height);
        Assert.Equal(width - 12 * dpi / 96, result.Right);
        Assert.Equal(height - 12 * dpi / 96, result.Bottom);
    }

    [Theory]
    [InlineData(-1920, 0, 1920, 1040, 96)]
    [InlineData(0, -1080, 1920, 1040, 144)]
    [InlineData(0, 0, 800, 560, 192)]
    [InlineData(-100, -100, 200, 200, 192)]
    public void RemovedMonitorOrSmallWorkAreaCannotLeavePanelOffscreen(double x, double y, double width, double height, double dpi)
    {
        var work = new PixelRect(x, y, width, height);
        var result = PanelPlacement.Place(work, dpi, TaskbarEdge.Bottom, new(9999, -9999, 760, 760));
        Assert.True(result.X >= work.X && result.Y >= work.Y);
        Assert.True(result.Right <= work.Right && result.Bottom <= work.Bottom);
        Assert.True(result.Width > 0 && result.Height > 0);
    }

    [Fact]
    public void RestoringKeepsUserPositionWithinSameMonitor()
    {
        var result = PanelPlacement.Place(new(0, 0, 1920, 1040), 96, TaskbarEdge.Bottom, new(200, 100, 760, 760));
        Assert.Equal(200, result.X);
        Assert.Equal(100, result.Y);
    }

    [Theory]
    [InlineData(0, 40, 1920, 1040, TaskbarEdge.Top)]
    [InlineData(48, 0, 1872, 1080, TaskbarEdge.Left)]
    [InlineData(0, 0, 1872, 1080, TaskbarEdge.Right)]
    [InlineData(0, 0, 1920, 1040, TaskbarEdge.Bottom)]
    [InlineData(0, 0, 1920, 1080, TaskbarEdge.Bottom)]
    public void DetectsReservedEdgeAndUsesBottomWhenAutoHidden(int x, int y, int width, int height, TaskbarEdge expected)
    {
        var work = new PixelRect(x, y, width, height);
        Assert.Equal(expected, PanelPlacement.FindTaskbarEdge(new(0, 0, 1920, 1080), work));
        var placed = PanelPlacement.Place(work, 96, expected);
        if (expected == TaskbarEdge.Left) Assert.Equal(work.X + 12, placed.X);
        if (expected == TaskbarEdge.Top) Assert.Equal(work.Y + 12, placed.Y);
    }
}

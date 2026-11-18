using AIEngineer.Core.Booklet;

namespace AIEngineer.Core.Tests;

/// <summary>
/// The touch grid divides the item's touch area, as the game's touch screen
/// does. Up to 0.15.2 a cell was taken as a fixed 50, centred on the canvas:
/// right for the stock route map (32 cells on 1,600) and booklet (23 x 31 on
/// the middle 1,150 x 1,550 of 1,238 x 1,641), 25 % too far from the middle on
/// Double Track's rebuilt route map (40 on 1,600).
/// </summary>
public class TouchGridTests
{
    /// <summary>The rule before 0.15.3: cells of 50, the grid centred on the canvas.</summary>
    private static (double X, double Y) FixedFifty(double width, double height, int columns, int rows, int column, int row)
    {
        double left = (width - columns * 50) / 2, bottom = (height - rows * 50) / 2;
        return (left + (column + 0.5) * 50 - width / 2, bottom + (row + 0.5) * 50 - height / 2);
    }

    [Theory]
    [InlineData(1600, 1600, 1600, 1600, 32, 32)]   // the stock route map: the touch area is the canvas
    [InlineData(1238, 1641, 1150, 1550, 23, 31)]   // a booklet page: the touch area its middle
    public void StockGridsAreUnchanged(double canvasWidth, double canvasHeight, double width, double height, int columns, int rows)
    {
        for (var c = 0; c < columns; c++)
            for (var r = 0; r < rows; r++)
            {
                var (x, y) = TouchGrid.CellMiddle(width, height, columns, rows, c, r);
                var (ox, oy) = FixedFifty(canvasWidth, canvasHeight, columns, rows, c, r);
                Assert.Equal(ox, x, 6);
                Assert.Equal(oy, y, 6);
            }
    }

    [Fact]
    public void DoubleTracksRouteMapHasFortyUnitCells()
    {
        var first = TouchGrid.CellMiddle(1600, 1600, 40, 40, 0, 0);
        var next = TouchGrid.CellMiddle(1600, 1600, 40, 40, 1, 0);
        var last = TouchGrid.CellMiddle(1600, 1600, 40, 40, 39, 39);
        Assert.Equal((-780.0, -780.0), first);
        Assert.Equal(40.0, next.X - first.X, 6);
        Assert.Equal((780.0, 780.0), last);
        // The fixed 50 put the corner cell 25 % farther out: 975 from the middle, 195 units off,
        // far past the 10 units within which a cell names a drawn switch.
        var old = FixedFifty(1600, 1600, 40, 40, 0, 0);
        Assert.Equal(-975.0, old.X, 6);
        Assert.Equal(1.25, old.X / first.X, 6);
    }

    [Fact]
    public void AGridOfNoCellsIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TouchGrid.CellMiddle(1600, 1600, 0, 40, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TouchGrid.CellMiddle(1600, 1600, 40, 0, 0, 0));
    }
}

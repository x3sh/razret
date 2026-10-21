namespace ZapretApp;

internal readonly record struct StrategyLayout(double Width, double Height, int Columns, bool AllVisible, double TileExtent)
{
    internal const double TileHeight = 60;
    // Includes window chrome, connection card, section actions and fixed footer.
    internal static StrategyLayout Grid(double availableWidth, double availableHeight, int count, double extraHeight = 0)
    {
        var baseHeight = 338 + extraHeight;
        int maxColumns = Math.Max(1, (int)Math.Floor((availableWidth - 52) / 154));
        int columns = Math.Min(4, maxColumns);
        double height = baseHeight + Math.Ceiling(Math.Max(1, count) / (double)columns) * TileHeight;
        while (height > availableHeight && columns < maxColumns)
        {
            columns++;
            height = baseHeight + Math.Ceiling(Math.Max(1, count) / (double)columns) * TileHeight;
        }
        return new(Math.Min(availableWidth, columns * 162 + 52), Math.Min(availableHeight, height), columns, height <= availableHeight, TileHeight);
    }
    internal static int ColumnsForViewport(double viewportWidth) => Math.Clamp((int)Math.Floor(Math.Max(0, viewportWidth - 22) / 154), 1, 8);
}

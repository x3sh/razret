namespace ZapretApp;

internal readonly record struct StrategyLayout(double Width, double Height, int Columns, bool AllVisible, double TileExtent)
{
    internal const double TileHeight = 76;
    // Includes window chrome, connection card, section actions and fixed footer.
    internal static StrategyLayout Grid(double availableWidth, double availableHeight, int count, double extraHeight = 0)
    {
        var baseHeight = 400 + extraHeight;
        int maxColumns = Math.Max(1, (int)Math.Floor((availableWidth - 52) / 180));
        int columns = Math.Min(4, maxColumns);
        double height = baseHeight + Math.Ceiling(Math.Max(1, count) / (double)columns) * TileHeight;
        while (height > availableHeight && columns < maxColumns)
        {
            columns++;
            height = baseHeight + Math.Ceiling(Math.Max(1, count) / (double)columns) * TileHeight;
        }
        double extent = TileHeight;
        var rows = Math.Ceiling(Math.Max(1, count) / (double)columns);
        if (height > availableHeight && (availableHeight - baseHeight) / rows >= 64)
        {
            extent = (availableHeight - baseHeight) / rows;
            height = availableHeight;
        }
        return new(Math.Min(availableWidth, columns * 235 + 52), Math.Min(availableHeight, height), columns, height <= availableHeight, extent);
    }
}

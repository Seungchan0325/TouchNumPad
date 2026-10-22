namespace TouchNumPad.Services;

public sealed record NumpadKeyDefinition(
    string Id,
    string Label,
    string? SubLabel,
    int Row,
    int Column,
    int RowSpan,
    int ColumnSpan,
    ushort VirtualKey,
    ushort FallbackVirtualKey,
    bool IsAccentKey = false,
    bool IsOperatorKey = false,
    bool IsHomeKey = false);

public static class NumpadKeyMap
{
    public const int TotalRows = 4;
    public const int TotalColumns = 5;

    public static IReadOnlyList<NumpadKeyDefinition> Keys { get; } =
    [
        // Row 0: Backspace, =, 7, 8, 9
        new("Backspace", "\uE94F", "BS", 0, 0, 1, 1, 0x08, 0x08, IsOperatorKey: true),
        new("Equals", "=", null, 0, 1, 1, 1, 0xBB, 0xBB, IsOperatorKey: true),
        new("Num7", "7", null, 0, 2, 1, 1, 0x67, 0x37),
        new("Num8", "8", null, 0, 3, 1, 1, 0x68, 0x38),
        new("Num9", "9", null, 0, 4, 1, 1, 0x69, 0x39),

        // Row 1: ÷, ×, 4, 5 (Home Key), 6
        new("Divide", "÷", "/", 1, 0, 1, 1, 0x6F, 0x6F, IsOperatorKey: true),
        new("Multiply", "×", "*", 1, 1, 1, 1, 0x6A, 0x6A, IsOperatorKey: true),
        new("Num4", "4", null, 1, 2, 1, 1, 0x64, 0x34),
        new("Num5", "5", "•", 1, 3, 1, 1, 0x65, 0x35, IsHomeKey: true),
        new("Num6", "6", null, 1, 4, 1, 1, 0x66, 0x36),

        // Row 2: −, +, 1, 2, 3
        new("Subtract", "−", "-", 2, 0, 1, 1, 0x6D, 0x6D, IsOperatorKey: true),
        new("Add", "+", null, 2, 1, 1, 1, 0x6B, 0x6B, IsOperatorKey: true),
        new("Num1", "1", null, 2, 2, 1, 1, 0x61, 0x31),
        new("Num2", "2", null, 2, 3, 1, 1, 0x62, 0x32),
        new("Num3", "3", null, 2, 4, 1, 1, 0x63, 0x33),

        // Row 3: Enter (ColSpan 2), 0 (ColSpan 2), .
        new("Enter", "Enter", "↵", 3, 0, 1, 2, 0x0D, 0x0D, IsAccentKey: true),
        new("Num0", "0", null, 3, 2, 1, 2, 0x60, 0x30),
        new("Decimal", ".", null, 3, 4, 1, 1, 0x6E, 0xBE)
    ];

    /// <summary>
    /// Maps normalized coordinates in local numpad space (0.0..1.0, 0.0..1.0) to a NumpadKeyDefinition.
    /// Returns null if outside the 0.0..1.0 unit rectangle.
    /// </summary>
    public static NumpadKeyDefinition? HitTestNormalized(double localX, double localY)
    {
        if (localX < 0.0 || localX > 1.0 || localY < 0.0 || localY > 1.0)
            return null;

        double clampedX = Math.Clamp(localX, 0.0, 0.9999);
        double clampedY = Math.Clamp(localY, 0.0, 0.9999);

        int col = (int)(clampedX * TotalColumns);
        int row = (int)(clampedY * TotalRows);

        foreach (var key in Keys)
        {
            if (row >= key.Row && row < key.Row + key.RowSpan &&
                col >= key.Column && col < key.Column + key.ColumnSpan)
            {
                return key;
            }
        }

        return null;
    }
}

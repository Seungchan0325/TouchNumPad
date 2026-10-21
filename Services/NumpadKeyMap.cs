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
    bool IsOperatorKey = false);

public static class NumpadKeyMap
{
    public const int TotalRows = 5;
    public const int TotalColumns = 4;

    // Normalized top strip height reserved for the top swipe bar in the UI/touchpad mapping
    public const double HeaderNormalizedHeight = 0.14;

    public static IReadOnlyList<NumpadKeyDefinition> Keys { get; } =
    [
        // Row 0: Backspace, /, *, -
        new("Backspace", "\uE94F", "BS", 0, 0, 1, 1, 0x08, 0x08, IsOperatorKey: true),
        new("Divide", "÷", "/", 0, 1, 1, 1, 0x6F, 0x6F, IsOperatorKey: true),
        new("Multiply", "×", "*", 0, 2, 1, 1, 0x6A, 0x6A, IsOperatorKey: true),
        new("Subtract", "−", "-", 0, 3, 1, 1, 0x6D, 0x6D, IsOperatorKey: true),

        // Row 1: 7, 8, 9, + (RowSpan 2)
        new("Num7", "7", null, 1, 0, 1, 1, 0x67, 0x37),
        new("Num8", "8", null, 1, 1, 1, 1, 0x68, 0x38),
        new("Num9", "9", null, 1, 2, 1, 1, 0x69, 0x39),
        new("Add", "+", null, 1, 3, 2, 1, 0x6B, 0x6B, IsOperatorKey: true),

        // Row 2: 4, 5, 6
        new("Num4", "4", null, 2, 0, 1, 1, 0x64, 0x34),
        new("Num5", "5", "•", 2, 1, 1, 1, 0x65, 0x35),
        new("Num6", "6", null, 2, 2, 1, 1, 0x66, 0x36),

        // Row 3: 1, 2, 3, Enter (RowSpan 2)
        new("Num1", "1", null, 3, 0, 1, 1, 0x61, 0x31),
        new("Num2", "2", null, 3, 1, 1, 1, 0x62, 0x32),
        new("Num3", "3", null, 3, 2, 1, 1, 0x63, 0x33),
        new("Enter", "Enter", "↵", 3, 3, 2, 1, 0x0D, 0x0D, IsAccentKey: true),

        // Row 4: 0 (ColumnSpan 2), .
        new("Num0", "0", null, 4, 0, 1, 2, 0x60, 0x30),
        new("Decimal", ".", null, 4, 2, 1, 1, 0x6E, 0xBE)
    ];

    /// <summary>
    /// Maps normalized touchpad coordinates (0.0..1.0, 0.0..1.0) to a NumpadKeyDefinition.
    /// The full touchpad area maps directly to the 4x5 key grid so the entire touchpad surface
    /// feels natural and responsive as a physical numpad.
    /// </summary>
    public static NumpadKeyDefinition? HitTestNormalized(double normX, double normY)
    {
        double clampedX = Math.Clamp(normX, 0.0, 0.9999);
        double clampedY = Math.Clamp(normY, 0.0, 0.9999);

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

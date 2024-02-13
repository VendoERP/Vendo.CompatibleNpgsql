using System.ComponentModel;

namespace Npgsql;

#pragma warning disable 1591

[EditorBrowsable(EditorBrowsableState.Never)]
public static class NpgsqlGlobalCompatibilityConfig
{
    public static bool ArrayStartsFromZero { get; set; } = true;
    public static bool AllowNullInNonNullArray { get; set; } = true;
    public static bool FallbackDecimalOverflow { get; set; } = true;
    public static bool FallbackDecimalScaleOverflow { get; set; } = true;
}

namespace DocuMind.Data.Configurations;

/// <summary>Creates enum checks from application-owned names, keeping string storage and SQL constraints aligned.</summary>
internal static class ModelConstraints
{
    /// <summary>Unknown enum values are rejected by PostgreSQL even when inserts bypass EF validation.</summary>
    public static string EnumValues<TEnum>(string column, bool nullable = false) where TEnum : struct, Enum
    {
        var values = string.Join(", ", Enum.GetNames<TEnum>().Select(value => "'" + value + "'"));
        var allowed = $"\"{column}\" IN ({values})";
        return nullable ? $"\"{column}\" IS NULL OR ({allowed})" : allowed;
    }
}

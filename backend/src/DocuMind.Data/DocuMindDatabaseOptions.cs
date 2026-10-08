using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace DocuMind.Data;

/// <summary>Keeps runtime, tooling, and verification on the same PostgreSQL/vector type mapping.</summary>
public static class DocuMindDatabaseOptions
{
    /// <summary>Registers official pgvector EF/Npgsql support without changing connection or security settings.</summary>
    public static DbContextOptionsBuilder UseDocuMindPostgreSql(this DbContextOptionsBuilder options, string connectionString)
        => options.UseNpgsql(connectionString, postgresql => postgresql.UseVector());

    /// <summary>Preserves the typed options builder used by isolated tests and design-time EF tooling.</summary>
    public static DbContextOptionsBuilder<TContext> UseDocuMindPostgreSql<TContext>(
        this DbContextOptionsBuilder<TContext> options, string connectionString) where TContext : DbContext
    {
        UseDocuMindPostgreSql((DbContextOptionsBuilder)options, connectionString);
        return options;
    }
}

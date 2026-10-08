using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DocuMind.Data;

/// <summary>Builds EF tooling's context explicitly, without starting the API, resolving email delivery, or reading secret files.</summary>
public sealed class DocuMindDesignTimeDbContextFactory : IDesignTimeDbContextFactory<DocuMindDbContext>
{
    /// <summary>Reads a locally supplied environment connection; migration commands never embed credentials in source.</summary>
    public DocuMindDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__DocuMind");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("For EF tooling, set ConnectionStrings__DocuMind privately in the local environment.");
        return new DocuMindDbContext(new DbContextOptionsBuilder<DocuMindDbContext>()
            .UseDocuMindPostgreSql(connection).Options);
    }
}

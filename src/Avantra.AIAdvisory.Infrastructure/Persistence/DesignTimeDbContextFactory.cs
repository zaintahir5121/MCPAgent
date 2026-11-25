using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Avantra.AIAdvisory.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` to generate SQL Server migrations (production schema).
/// dotnet ef migrations add &lt;Name&gt; -p src/Avantra.AIAdvisory.Infrastructure -o Persistence/Migrations
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AdvisoryDbContext>
{
    public AdvisoryDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AdvisoryDbContext>()
            .UseSqlServer("Server=localhost;Database=avantra-agent;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
}

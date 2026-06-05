using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ApiForge.Api.Data;

// Used only by `dotnet ef` at design time so migrations can be generated without running the
// app or needing a live database. The connection string here is never used to connect.
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=apiforge;Username=apiforge;Password=apiforge")
            .Options;
        return new AppDbContext(options);
    }
}

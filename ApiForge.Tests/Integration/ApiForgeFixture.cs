using ApiForge.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace ApiForge.Tests.Integration;

// Starts a real PostgreSQL container once per test collection and boots the API against it via
// WebApplicationFactory. Requires Docker to be running.
public class ApiForgeFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("apiforge")
        .WithUsername("apiforge")
        .WithPassword("apiforge")
        .Build();

    public async Task InitializeAsync()
    {
        await _db.StartAsync();

        // Apply migrations against the container before any test runs.
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public new async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing" environment: skip the Development auto-migrate path; we migrate explicitly above.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _db.GetConnectionString());
        builder.UseSetting("Jwt:Key", "integration-test-signing-key-at-least-32-bytes-long!!");
        builder.UseSetting("Jwt:Issuer", "ApiForge");
        builder.UseSetting("Jwt:Audience", "ApiForgeClients");
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiForgeFixture>;

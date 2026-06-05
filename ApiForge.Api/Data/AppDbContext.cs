using ApiForge.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApiForge.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<UsageEvent> UsageEvents => Set<UsageEvent>();
    public DbSet<Item> Items => Set<Item>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Email).IsRequired();
            e.Property(u => u.PasswordHash).IsRequired();
        });

        b.Entity<ApiKey>(e =>
        {
            e.HasIndex(k => k.KeyHash).IsUnique();
            e.Property(k => k.Name).IsRequired();
            e.Property(k => k.Prefix).IsRequired();
            e.HasOne(k => k.User)
                .WithMany(u => u.ApiKeys)
                .HasForeignKey(k => k.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasOne(t => t.User)
                .WithMany(u => u.RefreshTokens)
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<UsageEvent>(e =>
        {
            e.HasIndex(u => u.ApiKeyId);
            e.HasIndex(u => u.RequestedAt);
            e.HasOne(u => u.ApiKey)
                .WithMany(k => k.UsageEvents)
                .HasForeignKey(u => u.ApiKeyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Item>(e =>
        {
            e.HasIndex(i => i.UserId);
            e.Property(i => i.Name).IsRequired();
        });
    }
}

using System.Security.Claims;
using ApiForge.Api.Data;
using ApiForge.Api.Domain;
using ApiForge.Api.Infrastructure;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ApiForge.Api.Features.Keys;

public static class KeyEndpoints
{
    public static void MapKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/keys").WithTags("Keys").RequireAuthorization();

        group.MapPost("", async (
            CreateKeyRequest req, AppDbContext db, HttpContext ctx, IValidator<CreateKeyRequest> validator) =>
        {
            if (await validator.ToProblemAsync(req) is { } problem) return problem;

            var userId = ctx.User.UserId();
            var (plaintext, hash, prefix) = ApiKeyGenerator.Generate();

            var key = new ApiKey
            {
                UserId = userId,
                Name = req.Name,
                KeyHash = hash,
                Prefix = prefix,
                Scopes = req.Scopes ?? ["read"],
                RateLimit = req.RateLimit ?? 60,
                ExpiresAt = req.ExpiresAt,
                CreatedAt = DateTime.UtcNow
            };
            db.ApiKeys.Add(key);
            await db.SaveChangesAsync();

            // Plaintext is returned ONCE and is never retrievable again.
            return Results.Created($"/keys/{key.Id}", new { key.Id, key = plaintext, key.Prefix });
        });

        group.MapGet("", async (AppDbContext db, HttpContext ctx) =>
        {
            var userId = ctx.User.UserId();
            var keys = await db.ApiKeys
                .Where(k => k.UserId == userId)
                .OrderByDescending(k => k.CreatedAt)
                .Select(k => new
                {
                    k.Id, k.Name, k.Prefix, k.Scopes, k.RateLimit,
                    k.ExpiresAt, k.IsRevoked, k.LastUsedAt, k.CreatedAt
                })
                .ToListAsync();
            return Results.Ok(keys);
        });

        group.MapDelete("/{id:guid}", async (Guid id, AppDbContext db, HttpContext ctx) =>
        {
            var userId = ctx.User.UserId();
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == userId);
            if (key is null) return Results.NotFound();

            key.IsRevoked = true;        // soft delete
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}

public static class ClaimsPrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

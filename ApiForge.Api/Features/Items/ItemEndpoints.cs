using ApiForge.Api.Data;
using ApiForge.Api.Domain;
using ApiForge.Api.Middleware;
using Microsoft.EntityFrameworkCore;

namespace ApiForge.Api.Features.Items;

// Demo resource protected by API keys (X-API-Key) rather than JWT. Exercises the API-key
// middleware, scope enforcement, rate limiting and usage tracking end-to-end.
public static class ItemEndpoints
{
    public static void MapItemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/items").WithTags("Items").RequireRateLimiting("ApiKeyPolicy");

        group.MapGet("", async (AppDbContext db, HttpContext ctx) =>
        {
            var tenantId = (Guid)ctx.Items["TenantId"]!;
            var items = await db.Items
                .Where(i => i.UserId == tenantId)
                .OrderByDescending(i => i.CreatedAt)
                .ToListAsync();
            return Results.Ok(items);
        })
        .WithMetadata(new RequireApiKeyAttribute())
        .WithMetadata(new RequireScopeAttribute("read"));

        group.MapPost("", async (CreateItemRequest req, AppDbContext db, HttpContext ctx) =>
        {
            var tenantId = (Guid)ctx.Items["TenantId"]!;
            var item = new Item
            {
                UserId = tenantId,
                Name = req.Name,
                CreatedAt = DateTime.UtcNow
            };
            db.Items.Add(item);
            await db.SaveChangesAsync();
            return Results.Created($"/items/{item.Id}", item);
        })
        .WithMetadata(new RequireApiKeyAttribute())
        .WithMetadata(new RequireScopeAttribute("write"));
    }
}

public record CreateItemRequest(string Name);

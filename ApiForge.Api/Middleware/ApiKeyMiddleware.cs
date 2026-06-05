using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ApiForge.Api.Data;
using ApiForge.Api.Domain;
using ApiForge.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace ApiForge.Api.Middleware;

// Runs on every request. For endpoints marked [RequireApiKey] it:
//   hash X-API-Key -> look up -> check revoked/expiry/scope -> stamp tenant identity
//   -> run the handler -> record a usage event (final status + real latency).
//
// Pipeline order matters: this must run BEFORE UseRateLimiter, because the rate-limiter
// partition reads ctx.Items["ApiKey"], which is set here.
public class ApiKeyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, AppDbContext db, UsageQueue usageQueue)
    {
        // Skip routes that did NOT opt in to API-key auth.
        if (ctx.GetEndpoint()?.Metadata.GetMetadata<RequireApiKeyAttribute>() is null)
        {
            await next(ctx);
            return;
        }

        if (!ctx.Request.Headers.TryGetValue("X-API-Key", out var rawKey) || string.IsNullOrWhiteSpace(rawKey))
        {
            await Write(ctx, StatusCodes.Status401Unauthorized, "Missing X-API-Key header");
            return;
        }

        var hash = Hash(rawKey!);
        var apiKey = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.KeyHash == hash);

        if (apiKey is null || apiKey.IsRevoked)
        {
            await Write(ctx, StatusCodes.Status401Unauthorized, "Invalid or revoked API key");
            return;
        }

        if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt < DateTime.UtcNow)
        {
            await Write(ctx, StatusCodes.Status401Unauthorized, "API key has expired");
            return;
        }

        var requiredScope = ctx.GetEndpoint()?.Metadata.GetMetadata<RequireScopeAttribute>()?.Scope;
        if (requiredScope is not null && !apiKey.Scopes.Contains(requiredScope))
        {
            await Write(ctx, StatusCodes.Status403Forbidden, $"Key lacks required scope: {requiredScope}");
            return;
        }

        ctx.Items["ApiKey"] = apiKey;
        ctx.Items["TenantId"] = apiKey.UserId;

        var sw = Stopwatch.StartNew();
        await next(ctx);
        sw.Stop();

        usageQueue.Enqueue(new UsageEvent
        {
            ApiKeyId = apiKey.Id,
            Endpoint = $"{ctx.Request.Method} {ctx.Request.Path}",
            Method = ctx.Request.Method,
            StatusCode = ctx.Response.StatusCode,
            LatencyMs = (int)sw.ElapsedMilliseconds,
            RequestedAt = DateTime.UtcNow
        });
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static Task Write(HttpContext ctx, int status, string error)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new { error });
    }
}

using ApiForge.Api.Data;
using ApiForge.Api.Domain;
using ApiForge.Api.Infrastructure;
using ApiForge.Api.Middleware;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ApiForge.Api.Features.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest req, AppDbContext db, IValidator<RegisterRequest> validator) =>
        {
            if (await validator.ToProblemAsync(req) is { } problem) return problem;

            var email = req.Email.Trim().ToLowerInvariant();
            if (await db.Users.AnyAsync(u => u.Email == email))
                return Results.Conflict(new { error = "Email already registered" });

            var user = new User
            {
                Email = email,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password),
                CreatedAt = DateTime.UtcNow
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            return Results.Created($"/auth/users/{user.Id}", new { userId = user.Id });
        });

        group.MapPost("/login", async (
            LoginRequest req, AppDbContext db, TokenService tokens, IValidator<LoginRequest> validator) =>
        {
            if (await validator.ToProblemAsync(req) is { } problem) return problem;

            var email = req.Email.Trim().ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Results.Json(new { error = "Invalid credentials" }, statusCode: StatusCodes.Status401Unauthorized);

            var (rtPlain, rtHash) = tokens.NewRefreshToken();
            db.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id,
                TokenHash = rtHash,
                ExpiresAt = tokens.RefreshExpiry(),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                accessToken = tokens.IssueAccessToken(user.Id, user.Email),
                refreshToken = rtPlain
            });
        });

        group.MapPost("/refresh", async (
            RefreshRequest req, AppDbContext db, TokenService tokens, IValidator<RefreshRequest> validator) =>
        {
            if (await validator.ToProblemAsync(req) is { } problem) return problem;

            var hash = ApiKeyMiddleware.Hash(req.RefreshToken);
            var token = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash);

            if (token is null || token.RevokedAt is not null || token.ExpiresAt < DateTime.UtcNow)
                return Results.Json(new { error = "Invalid or expired refresh token" }, statusCode: StatusCodes.Status401Unauthorized);

            // Rotate: issue a new refresh token, revoke and link the old one.
            var (newPlain, newHash) = tokens.NewRefreshToken();
            var replacement = new RefreshToken
            {
                UserId = token.UserId,
                TokenHash = newHash,
                ExpiresAt = tokens.RefreshExpiry(),
                CreatedAt = DateTime.UtcNow
            };
            db.RefreshTokens.Add(replacement);

            token.RevokedAt = DateTime.UtcNow;
            token.ReplacedById = replacement.Id;
            await db.SaveChangesAsync();

            return Results.Ok(new
            {
                accessToken = tokens.IssueAccessToken(token.UserId, token.User.Email),
                refreshToken = newPlain
            });
        });
    }
}

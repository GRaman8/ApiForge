using System.Text;
using System.Threading.RateLimiting;
using ApiForge.Api.Data;
using ApiForge.Api.Domain;
using ApiForge.Api.Features.Auth;
using ApiForge.Api.Features.Items;
using ApiForge.Api.Features.Keys;
using ApiForge.Api.Features.Usage;
using ApiForge.Api.Middleware;
using ApiForge.Api.Services;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// ----- Configuration: connection string & JWT --------------------------------------------
// Prefer an explicit ConnectionStrings:Default (local/docker-compose); otherwise assemble it
// from the individual DB_* fields injected by ECS from Secrets Manager.
var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    connectionString = new NpgsqlConnectionStringBuilder
    {
        Host = builder.Configuration["DB_HOST"] ?? "localhost",
        Port = int.TryParse(builder.Configuration["DB_PORT"], out var port) ? port : 5432,
        Database = builder.Configuration["DB_NAME"] ?? "apiforge",
        Username = builder.Configuration["DB_USER"] ?? "apiforge",
        Password = builder.Configuration["DB_PASSWORD"] ?? "apiforge",
        SslMode = builder.Environment.IsDevelopment() ? SslMode.Disable : SslMode.Require
    }.ConnectionString;
}

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString));

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
// Jwt:Key comes from Secrets Manager (env Jwt__Key) in AWS, or appsettings in local dev.
jwt.Key = builder.Configuration["Jwt:Key"] ?? jwt.Key;

builder.Services.AddSingleton<TokenService>();

// ----- AuthN / AuthZ ----------------------------------------------------------------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key))
        };
    });
builder.Services.AddAuthorization();

// ----- Validation, usage pipeline, rate limiting ------------------------------------------
builder.Services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>();

builder.Services.AddSingleton<UsageQueue>();
builder.Services.AddHostedService<UsageFlushService>();

builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("ApiKeyPolicy", ctx =>
    {
        var apiKey = ctx.Items["ApiKey"] as ApiKey;       // set by ApiKeyMiddleware (runs first)
        var limit = apiKey?.RateLimit ?? 60;
        var partitionKey = apiKey?.Id.ToString()
                           ?? ctx.Connection.RemoteIpAddress?.ToString()
                           ?? "anon";

        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });

    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Rate limit exceeded" }, token);
    };
});

var app = builder.Build();

// ----- Migration gate ----------------------------------------------------------------------
// Run as a one-off task (RUN_MIGRATIONS=true) inside the VPC against RDS, then exit.
// Also auto-migrate in Development for convenience.
if (builder.Configuration.GetValue<bool>("RUN_MIGRATIONS"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    return;
}
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

// ----- Middleware pipeline (order is load-bearing) -----------------------------------------
// UseRouting -> Auth -> ApiKeyMiddleware (stamps ctx.Items["ApiKey"]) -> RateLimiter -> endpoints
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ApiKeyMiddleware>();
app.UseRateLimiter();

// ----- Endpoints ---------------------------------------------------------------------------
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Health");
app.MapAuthEndpoints();
app.MapKeyEndpoints();
app.MapUsageEndpoints();
app.MapItemEndpoints();

app.Run();

// Exposed for WebApplicationFactory<Program> integration tests.
public partial class Program;

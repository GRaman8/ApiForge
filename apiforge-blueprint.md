# ApiForge — Project Blueprint

> **Type:** Backend SaaS Platform  
> **Language:** C# (.NET 8)  
> **Cloud:** AWS  
> **Difficulty:** Mid-level (between CRUD and distributed systems)  
> **Estimated build time:** 2–3 weeks

---

## Table of Contents

1. [Project Summary](#1-project-summary)
2. [Architecture Overview](#2-architecture-overview)
3. [Tech Stack](#3-tech-stack)
4. [Data Model](#4-data-model)
5. [API Reference](#5-api-reference)
6. [Core Features — Implementation Detail](#6-core-features--implementation-detail)
   - [JWT Authentication](#61-jwt-authentication)
   - [API Key Generation and Hashing](#62-api-key-generation-and-hashing)
   - [Validation Middleware](#63-validation-middleware)
   - [Rate Limiting](#64-rate-limiting)
   - [Scopes and Expiry](#65-scopes-and-expiry)
   - [Usage Tracking](#66-usage-tracking)
7. [AWS Infrastructure](#7-aws-infrastructure)
8. [CDK Stack Layout](#8-cdk-stack-layout)
9. [Project Structure](#9-project-structure)
10. [Best Practices](#10-best-practices)
11. [Week-by-Week Build Plan](#11-week-by-week-build-plan)
12. [Resume Talking Points](#12-resume-talking-points)

---

## 1. Project Summary

ApiForge is a multi-tenant API key management service. It lets developers register, issue, scope, rate-limit, and track usage of API keys for their own applications.

This is the same core feature set behind Stripe API keys, GitHub Personal Access Tokens, and any SaaS product that exposes a developer-facing API. The project is intentionally scoped to be non-trivial (real security patterns, middleware-level validation, multi-tenancy) without requiring a distributed queue or event-driven architecture.

**What the system does:**

- Users register and authenticate via JWT
- Authenticated users create API keys with optional scopes and TTL
- Every API key is hashed before storage — plaintext is shown once and never stored
- Incoming requests carry an `X-API-Key` header validated by middleware
- Usage is tracked per key and exposed via a reporting endpoint
- Rate limits are enforced at the middleware level per key

---

## 2. Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                    API Gateway (AWS)                     │
└─────────────────────────┬───────────────────────────────┘
                          │
┌─────────────────────────▼───────────────────────────────┐
│              ASP.NET Core Minimal API                    │
│                  (ECS Fargate)                           │
│                                                          │
│  ┌──────────────┐   ┌──────────────┐   ┌─────────────┐  │
│  │  /auth/*     │   │  /keys/*     │   │  /usage/*   │  │
│  │  Register    │   │  Create key  │   │  Reports    │  │
│  │  Login       │   │  List keys   │   │  Per-key    │  │
│  │  Refresh     │   │  Revoke key  │   │  stats      │  │
│  └──────────────┘   └──────────────┘   └─────────────┘  │
│                                                          │
│  ┌────────────────────────────────────────────────────┐  │
│  │          API Key Validation Middleware              │  │
│  │  Hash incoming key → lookup → check scope/expiry   │  │
│  │  → enforce rate limit → stamp tenant identity      │  │
│  └────────────────────────────────────────────────────┘  │
└──────────────────────────┬──────────────────────────────┘
                           │
          ┌────────────────┴────────────────┐
          │                                 │
┌─────────▼──────────┐           ┌──────────▼──────────┐
│  RDS PostgreSQL     │           │   Secrets Manager    │
│  (EF Core)          │           │   DB credentials     │
│                     │           │   JWT signing key    │
│  - Users            │           └─────────────────────┘
│  - ApiKeys          │
│  - UsageEvents      │
└─────────────────────┘
```

**Request flow for a protected endpoint:**

```
Client request (X-API-Key: apf_live_xxxx)
  → API Gateway
  → ECS container
  → ApiKeyMiddleware
      → SHA-256 hash the incoming key
      → SELECT * FROM ApiKeys WHERE KeyHash = ?
      → check IsRevoked, ExpiresAt, Scope
      → check rate limit (sliding window)
      → increment usage counter
      → attach TenantId to HttpContext
  → Route handler executes
  → Response returned
```

---

## 3. Tech Stack

### Language and Runtime

| Tool | Version | Role |
|---|---|---|
| C# | 12 | Primary language |
| .NET | 8 (LTS) | Runtime |
| ASP.NET Core | 8 | Web framework |

### Backend Libraries

| Library | npm/NuGet equivalent of | Role |
|---|---|---|
| `Microsoft.AspNetCore` | Express.js / Hono | HTTP routing, middleware, DI |
| `Microsoft.EntityFrameworkCore` | Prisma ORM | Database ORM, migrations |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | `pg` driver | PostgreSQL EF Core provider |
| `FluentValidation.AspNetCore` | Zod | Request validation |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | `jsonwebtoken` | JWT validation middleware |
| `System.IdentityModel.Tokens.Jwt` | `jsonwebtoken` | JWT creation |
| `Microsoft.AspNetCore.RateLimiting` | `express-rate-limit` | Built-in sliding window rate limiter |
| `BCrypt.Net-Next` | `bcrypt` | Password hashing |
| `xUnit` | Jest / Vitest | Unit and integration testing |
| `Moq` | `jest.fn()` / `vi.fn()` | Mocking in tests |

### AWS Services

| Service | Role |
|---|---|
| ECS Fargate | Runs the containerized ASP.NET Core API |
| ECR | Stores the Docker image |
| RDS (PostgreSQL) | Primary database |
| API Gateway | Public HTTPS entry point, routes to ECS |
| Secrets Manager | Stores DB credentials and JWT signing key |
| CloudWatch | Logs and metrics (request count, error rate, latency) |
| VPC | Network isolation — ECS and RDS in private subnets |

### Infrastructure as Code

| Tool | Role |
|---|---|
| AWS CDK (C#) | Provisions all AWS resources in one `cdk deploy` |
| Docker | Containerizes the .NET app for ECS |

---

## 4. Data Model

### Users table

```sql
CREATE TABLE "Users" (
    "Id"           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "Email"        TEXT NOT NULL UNIQUE,
    "PasswordHash" TEXT NOT NULL,
    "CreatedAt"    TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

### ApiKeys table

```sql
CREATE TABLE "ApiKeys" (
    "Id"          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "UserId"      UUID NOT NULL REFERENCES "Users"("Id"),
    "Name"        TEXT NOT NULL,
    "KeyHash"     TEXT NOT NULL UNIQUE,   -- SHA-256 of plaintext key
    "Prefix"      TEXT NOT NULL,          -- e.g. "apf_live_" — shown in UI
    "Scopes"      TEXT[] NOT NULL,        -- e.g. {"read", "write"}
    "RateLimit"   INT NOT NULL DEFAULT 60, -- requests per minute
    "ExpiresAt"   TIMESTAMPTZ,            -- NULL = never expires
    "IsRevoked"   BOOLEAN NOT NULL DEFAULT false,
    "CreatedAt"   TIMESTAMPTZ NOT NULL DEFAULT now(),
    "LastUsedAt"  TIMESTAMPTZ
);
```

### UsageEvents table

```sql
CREATE TABLE "UsageEvents" (
    "Id"           BIGSERIAL PRIMARY KEY,
    "ApiKeyId"     UUID NOT NULL REFERENCES "ApiKeys"("Id"),
    "Endpoint"     TEXT NOT NULL,
    "Method"       TEXT NOT NULL,
    "StatusCode"   INT NOT NULL,
    "LatencyMs"    INT NOT NULL,
    "RequestedAt"  TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

### EF Core C# models

```csharp
public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public ICollection<ApiKey> ApiKeys { get; set; } = [];
}

public class ApiKey
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = [];
    public int RateLimit { get; set; } = 60;
    public DateTime? ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public User User { get; set; } = null!;
    public ICollection<UsageEvent> UsageEvents { get; set; } = [];
}

public class UsageEvent
{
    public long Id { get; set; }
    public Guid ApiKeyId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public int LatencyMs { get; set; }
    public DateTime RequestedAt { get; set; }
    public ApiKey ApiKey { get; set; } = null!;
}
```

---

## 5. API Reference

### Auth endpoints

```
POST /auth/register
Body: { "email": "user@example.com", "password": "..." }
Returns: { "userId": "uuid" }

POST /auth/login
Body: { "email": "...", "password": "..." }
Returns: { "accessToken": "eyJ...", "refreshToken": "..." }

POST /auth/refresh
Body: { "refreshToken": "..." }
Returns: { "accessToken": "eyJ..." }
```

### Key management endpoints

```
POST /keys
Auth: Bearer JWT
Body: { "name": "Production key", "scopes": ["read","write"], "rateLimit": 100, "expiresAt": "2025-12-31" }
Returns: { "id": "uuid", "key": "apf_live_xxxxxxxxxxxxxx", "prefix": "apf_live_" }
NOTE: "key" is shown ONCE and never stored. Store it securely.

GET /keys
Auth: Bearer JWT
Returns: [ { "id", "name", "prefix", "scopes", "rateLimit", "expiresAt", "isRevoked", "lastUsedAt" } ]

DELETE /keys/{id}
Auth: Bearer JWT
Returns: 204 No Content
Effect: Sets IsRevoked = true. Does not hard-delete.
```

### Usage reporting endpoints

```
GET /keys/{id}/usage
Auth: Bearer JWT
Query params: ?from=2024-01-01&to=2024-01-31
Returns: {
  "keyId": "uuid",
  "totalRequests": 1420,
  "requestsToday": 38,
  "requestsThisMonth": 1420,
  "byEndpoint": [
    { "endpoint": "GET /items", "count": 900 },
    { "endpoint": "POST /items", "count": 520 }
  ],
  "averageLatencyMs": 42
}
```

---

## 6. Core Features — Implementation Detail

### 6.1 JWT Authentication

Use `Microsoft.AspNetCore.Authentication.JwtBearer`. Register in `Program.cs`:

```csharp
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!))
        };
    });
```

> **Best practice:** Pull `Jwt:Key` from AWS Secrets Manager at startup, not from `appsettings.json`. Never commit secrets to source control.

Issue tokens on login:

```csharp
var tokenDescriptor = new SecurityTokenDescriptor
{
    Subject = new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Email, user.Email)
    ]),
    Expires = DateTime.UtcNow.AddMinutes(15),   // short-lived access token
    Issuer = config["Jwt:Issuer"],
    Audience = config["Jwt:Audience"],
    SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
};
```

---

### 6.2 API Key Generation and Hashing

This is the most security-critical piece. Never store the plaintext key.

```csharp
public static class ApiKeyGenerator
{
    public static (string plaintext, string hash, string prefix) Generate()
    {
        // Generate 32 cryptographically random bytes
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var rawKey = Convert.ToBase64String(randomBytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "")[..40]; // trim to 40 chars

        var prefix = "apf_live_";
        var plaintext = $"{prefix}{rawKey}";

        // SHA-256 hash — this is what gets stored
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));
        var hash = Convert.ToHexString(hashBytes).ToLower();

        return (plaintext, hash, prefix);
    }
}
```

On `POST /keys`:

```csharp
app.MapPost("/keys", async (CreateKeyRequest req, AppDbContext db, HttpContext ctx) =>
{
    var userId = Guid.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    var (plaintext, hash, prefix) = ApiKeyGenerator.Generate();

    var key = new ApiKey
    {
        UserId = userId,
        Name = req.Name,
        KeyHash = hash,
        Prefix = prefix,
        Scopes = req.Scopes,
        RateLimit = req.RateLimit ?? 60,
        ExpiresAt = req.ExpiresAt,
        CreatedAt = DateTime.UtcNow
    };

    db.ApiKeys.Add(key);
    await db.SaveChangesAsync();

    // Return plaintext ONCE — it is never retrievable again
    return Results.Created($"/keys/{key.Id}", new { key.Id, Key = plaintext, key.Prefix });
}).RequireAuthorization();
```

---

### 6.3 Validation Middleware

This middleware runs on every request that carries an `X-API-Key` header. It gates access to any route that uses the `[RequireApiKey]` convention.

```csharp
public class ApiKeyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, AppDbContext db)
    {
        // Only process routes that opt in to API key auth
        if (!ctx.GetEndpoint()?.Metadata.GetMetadata<RequireApiKeyAttribute>() is not null)
        {
            await next(ctx);
            return;
        }

        if (!ctx.Request.Headers.TryGetValue("X-API-Key", out var rawKey))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "Missing X-API-Key header" });
            return;
        }

        // Hash the incoming key and look it up
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey!))).ToLower();
        var apiKey = await db.ApiKeys
            .Include(k => k.User)
            .FirstOrDefaultAsync(k => k.KeyHash == hash);

        if (apiKey is null || apiKey.IsRevoked)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid or revoked API key" });
            return;
        }

        if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt < DateTime.UtcNow)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "API key has expired" });
            return;
        }

        // Stamp the tenant identity onto the request context
        ctx.Items["ApiKey"] = apiKey;
        ctx.Items["TenantId"] = apiKey.UserId;

        // Update last used (fire-and-forget — do not await)
        _ = db.ApiKeys
            .Where(k => k.Id == apiKey.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, DateTime.UtcNow));

        await next(ctx);
    }
}
```

---

### 6.4 Rate Limiting

Use the built-in `Microsoft.AspNetCore.RateLimiting` (no third-party library needed).

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("ApiKeyPolicy", ctx =>
    {
        // Use the API key ID as the partition key
        var apiKey = ctx.Items["ApiKey"] as ApiKey;
        var limit = apiKey?.RateLimit ?? 60;
        var keyId = apiKey?.Id.ToString() ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";

        return RateLimitPartition.GetSlidingWindowLimiter(keyId, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,   // 10-second segments
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });

    options.OnRejected = async (ctx, _) =>
    {
        ctx.HttpContext.Response.StatusCode = 429;
        await ctx.HttpContext.Response.WriteAsJsonAsync(new { error = "Rate limit exceeded" });
    };
});
```

Apply to protected routes:

```csharp
app.MapGet("/items", async (AppDbContext db, HttpContext ctx) =>
{
    var tenantId = (Guid)ctx.Items["TenantId"]!;
    return await db.Items.Where(i => i.UserId == tenantId).ToListAsync();
})
.RequireRateLimiting("ApiKeyPolicy");
```

---

### 6.5 Scopes and Expiry

Define scope requirements as a metadata attribute:

```csharp
[AttributeUsage(AttributeTargets.Method)]
public class RequireScopeAttribute(string scope) : Attribute
{
    public string Scope { get; } = scope;
}
```

Check scope in middleware or a filter:

```csharp
var requiredScope = ctx.GetEndpoint()?.Metadata.GetMetadata<RequireScopeAttribute>()?.Scope;
if (requiredScope is not null && !apiKey.Scopes.Contains(requiredScope))
{
    ctx.Response.StatusCode = 403;
    await ctx.Response.WriteAsJsonAsync(new { error = $"Key lacks required scope: {requiredScope}" });
    return;
}
```

Apply to routes:

```csharp
app.MapPost("/items", CreateItem)
    .WithMetadata(new RequireScopeAttribute("write"));

app.MapGet("/items", GetItems)
    .WithMetadata(new RequireScopeAttribute("read"));
```

---

### 6.6 Usage Tracking

Write a background `IHostedService` that flushes usage events in batches rather than hitting the DB on every single request:

```csharp
// In middleware — add to an in-memory queue
var usageQueue = ctx.RequestServices.GetRequiredService<UsageQueue>();
usageQueue.Enqueue(new UsageEvent
{
    ApiKeyId = apiKey.Id,
    Endpoint = $"{ctx.Request.Method} {ctx.Request.Path}",
    Method = ctx.Request.Method,
    StatusCode = ctx.Response.StatusCode,
    LatencyMs = (int)stopwatch.ElapsedMilliseconds,
    RequestedAt = DateTime.UtcNow
});

// UsageQueue — a thread-safe in-memory buffer
public class UsageQueue
{
    private readonly ConcurrentQueue<UsageEvent> _queue = new();
    public void Enqueue(UsageEvent e) => _queue.Enqueue(e);
    public IReadOnlyList<UsageEvent> Drain()
    {
        var items = new List<UsageEvent>();
        while (_queue.TryDequeue(out var item)) items.Add(item);
        return items;
    }
}

// Background flusher — runs every 5 seconds
public class UsageFlushService(IServiceScopeFactory scopeFactory, UsageQueue queue)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var events = queue.Drain();
            if (events.Count == 0) continue;

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UsageEvents.AddRange(events);
            await db.SaveChangesAsync(ct);
        }
    }
}
```

Usage report query using LINQ:

```csharp
app.MapGet("/keys/{id}/usage", async (Guid id, AppDbContext db, HttpContext ctx,
    DateTime? from, DateTime? to) =>
{
    var tenantId = (Guid)ctx.Items["TenantId"]!;
    var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.UserId == tenantId);
    if (key is null) return Results.NotFound();

    var query = db.UsageEvents.Where(e => e.ApiKeyId == id);
    if (from.HasValue) query = query.Where(e => e.RequestedAt >= from);
    if (to.HasValue) query = query.Where(e => e.RequestedAt <= to);

    var today = DateTime.UtcNow.Date;
    var monthStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    var events = await query.ToListAsync();

    return Results.Ok(new
    {
        KeyId = id,
        TotalRequests = events.Count,
        RequestsToday = events.Count(e => e.RequestedAt >= today),
        RequestsThisMonth = events.Count(e => e.RequestedAt >= monthStart),
        ByEndpoint = events
            .GroupBy(e => e.Endpoint)
            .Select(g => new { Endpoint = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count),
        AverageLatencyMs = events.Count > 0 ? (int)events.Average(e => e.LatencyMs) : 0
    });
}).RequireAuthorization();
```

---

## 7. AWS Infrastructure

### Services and their roles

```
┌─────────────────────────────────────────────────────────────┐
│  VPC                                                         │
│                                                              │
│  ┌──────────────────────┐   ┌────────────────────────────┐  │
│  │  Public subnet        │   │  Private subnet             │  │
│  │                       │   │                             │  │
│  │  - API Gateway        │   │  - ECS Fargate (API)        │  │
│  │  - NAT Gateway        │   │  - RDS PostgreSQL           │  │
│  └──────────────────────┘   └────────────────────────────┘  │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │  Supporting services (regional, outside VPC)           │  │
│  │  - ECR (Docker image registry)                         │  │
│  │  - Secrets Manager (DB password, JWT signing key)      │  │
│  │  - CloudWatch Logs + Metrics                           │  │
│  └────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

### Environment variables injected into ECS at runtime

| Variable | Source |
|---|---|
| `ConnectionStrings__Default` | Secrets Manager |
| `Jwt__Key` | Secrets Manager |
| `Jwt__Issuer` | ECS task definition |
| `Jwt__Audience` | ECS task definition |
| `ASPNETCORE_ENVIRONMENT` | ECS task definition (`Production`) |

### Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["ApiForge.Api/ApiForge.Api.csproj", "ApiForge.Api/"]
RUN dotnet restore "ApiForge.Api/ApiForge.Api.csproj"
COPY . .
WORKDIR "/src/ApiForge.Api"
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ApiForge.Api.dll"]
```

---

## 8. CDK Stack Layout

The CDK project is written in C# in a separate solution folder. One `cdk deploy` provisions everything.

```csharp
public class ApiForgeStack : Stack
{
    public ApiForgeStack(Construct scope, string id, IStackProps? props = null)
        : base(scope, id, props)
    {
        // VPC
        var vpc = new Vpc(this, "ApiForgeVpc", new VpcProps { MaxAzs = 2 });

        // RDS PostgreSQL
        var dbSecret = new DatabaseSecret(this, "DbSecret", new DatabaseSecretProps
        {
            Username = "apiforge"
        });

        var db = new DatabaseInstance(this, "ApiForgeDb", new DatabaseInstanceProps
        {
            Engine = DatabaseInstanceEngine.Postgres(new PostgresInstanceEngineProps
            {
                Version = PostgresEngineVersion.VER_16
            }),
            Vpc = vpc,
            Credentials = Credentials.FromSecret(dbSecret),
            InstanceType = InstanceType.Of(InstanceClass.T3, InstanceSize.MICRO),
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PRIVATE_WITH_EGRESS },
            DeletionProtection = true,
            RemovalPolicy = RemovalPolicy.RETAIN
        });

        // ECR repository
        var repo = new Repository(this, "ApiForgeRepo", new RepositoryProps
        {
            RepositoryName = "apiforge-api"
        });

        // ECS Fargate
        var cluster = new Cluster(this, "ApiForgeCluster", new ClusterProps { Vpc = vpc });

        var taskDef = new FargateTaskDefinition(this, "ApiForgeTask", new FargateTaskDefinitionProps
        {
            MemoryLimitMiB = 512,
            Cpu = 256
        });

        dbSecret.GrantRead(taskDef.TaskRole);

        taskDef.AddContainer("ApiContainer", new ContainerDefinitionOptions
        {
            Image = ContainerImage.FromEcrRepository(repo, "latest"),
            PortMappings = [new PortMapping { ContainerPort = 8080 }],
            Secrets = new Dictionary<string, Amazon.CDK.AWS.ECS.Secret>
            {
                ["ConnectionStrings__Default"] = Amazon.CDK.AWS.ECS.Secret.FromSecretsManager(dbSecret),
            },
            Environment = new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["Jwt__Issuer"] = "ApiForge",
                ["Jwt__Audience"] = "ApiForgeClients"
            },
            Logging = LogDriver.AwsLogs(new AwsLogDriverProps
            {
                StreamPrefix = "apiforge",
                LogRetention = RetentionDays.ONE_MONTH
            })
        });

        var service = new ApplicationLoadBalancedFargateService(this, "ApiForgeService",
            new ApplicationLoadBalancedFargateServiceProps
            {
                Cluster = cluster,
                TaskDefinition = taskDef,
                PublicLoadBalancer = false,  // behind API Gateway
                DesiredCount = 1
            });

        db.Connections.AllowDefaultPortFrom(service.Service);
    }
}
```

---

## 9. Project Structure

```
ApiForge/
├── ApiForge.sln
│
├── ApiForge.Api/                          # Main ASP.NET Core project
│   ├── Program.cs                         # App setup, DI registration, route mapping
│   ├── appsettings.json                   # Non-secret config (Jwt:Issuer, Jwt:Audience)
│   ├── appsettings.Production.json        # Production overrides (no secrets here)
│   │
│   ├── Domain/                            # EF Core models
│   │   ├── User.cs
│   │   ├── ApiKey.cs
│   │   └── UsageEvent.cs
│   │
│   ├── Data/                              # EF Core DbContext + migrations
│   │   ├── AppDbContext.cs
│   │   └── Migrations/
│   │
│   ├── Features/                          # Organized by feature (vertical slice)
│   │   ├── Auth/
│   │   │   ├── AuthEndpoints.cs           # MapPost /auth/register, /auth/login
│   │   │   ├── LoginRequest.cs
│   │   │   ├── RegisterRequest.cs
│   │   │   └── LoginRequestValidator.cs   # FluentValidation
│   │   ├── Keys/
│   │   │   ├── KeyEndpoints.cs
│   │   │   ├── CreateKeyRequest.cs
│   │   │   ├── CreateKeyRequestValidator.cs
│   │   │   └── ApiKeyGenerator.cs
│   │   └── Usage/
│   │       └── UsageEndpoints.cs
│   │
│   ├── Middleware/
│   │   └── ApiKeyMiddleware.cs
│   │
│   ├── Services/
│   │   ├── UsageQueue.cs                  # In-memory concurrent queue
│   │   └── UsageFlushService.cs           # IHostedService background flusher
│   │
│   └── Infrastructure/
│       └── SecretsManagerConfig.cs        # Pulls secrets from AWS at startup
│
├── ApiForge.Tests/                        # xUnit test project
│   ├── ApiForge.Tests.csproj
│   ├── Unit/
│   │   ├── ApiKeyGeneratorTests.cs
│   │   └── UsageQueueTests.cs
│   └── Integration/
│       ├── AuthEndpointTests.cs
│       └── KeyEndpointTests.cs
│
└── ApiForge.Cdk/                          # CDK infrastructure project
    ├── ApiForge.Cdk.csproj
    ├── Program.cs
    └── ApiForgeStack.cs
```

---

## 10. Best Practices

### Security

- **Never store plaintext API keys.** Store only the SHA-256 hash. Show the plaintext exactly once on creation.
- **Use Secrets Manager for all credentials.** No connection strings or JWT keys in `appsettings.json` or environment variables baked into the image.
- **Use short-lived JWTs.** 15-minute access tokens + longer-lived refresh tokens. Never 24-hour JWTs.
- **Hash passwords with BCrypt.** Never SHA-256 for passwords — use an intentionally slow algorithm.
- **Validate all inputs with FluentValidation.** No raw model binding without validators.
- **Enforce HTTPS.** Add `app.UseHttpsRedirection()`. API Gateway handles TLS termination in production.

### Architecture

- **Put API key validation in middleware, not in controllers.** This enforces it globally and keeps route handlers clean.
- **Register `DbContext` as Scoped, never Singleton.** EF Core's `DbContext` is not thread-safe.
- **Use `IHostedService` for background work** (usage flush) instead of fire-and-forget tasks.
- **Use vertical slice feature folders** (`Features/Auth/`, `Features/Keys/`) instead of layer folders (`Controllers/`, `Services/`). Easier to navigate as the project grows.
- **Treat infrastructure as code from day one.** No manual console setup — everything in CDK.

### .NET / C# specifics

- **Use Minimal APIs**, not MVC controllers. It's the modern pattern for APIs in .NET 8.
- **Use `async`/`await` everywhere** that touches I/O (DB, HTTP, file). Never `.Result` or `.Wait()`.
- **Use `CancellationToken`** in all async methods so requests can be cancelled cleanly.
- **Use `record` types** for request/response DTOs — immutable by default, value equality built in.
- **Use `DateTimeKind.Utc`** on all `DateTime` values. Store UTC, convert to local in the client.
- **Run EF Core migrations as part of app startup** in dev. In production, run them as a separate pre-deploy step.

### Testing

- **Unit test pure logic** (key generation, hashing, validators) with xUnit + no dependencies.
- **Integration test endpoints** using `WebApplicationFactory<Program>` — spins up a real in-memory server.
- **Use a real test database** (PostgreSQL in a Docker container via Testcontainers) for integration tests, not SQLite.
- **Mock external dependencies** (Secrets Manager, SES) with Moq in unit tests.

### AWS

- **Put ECS and RDS in private subnets.** Only API Gateway is public-facing.
- **Use IAM task roles** to grant ECS access to Secrets Manager — no hardcoded AWS credentials.
- **Set `DeletionProtection = true`** on the RDS instance in CDK. Prevents accidental teardown.
- **Use `RetentionDays.ONE_MONTH`** on CloudWatch log groups. Unlimited retention accumulates cost.
- **Tag all CDK resources** with `project = "apiforge"` and `env = "production"` for cost visibility.

---

## 11. Week-by-Week Build Plan

### Week 1 — Core API and auth

- [ ] Create solution: `dotnet new sln`, `dotnet new webapi`, `dotnet new xunit`
- [ ] Set up EF Core with PostgreSQL, define `User`, `ApiKey`, `UsageEvent` models
- [ ] Run first migration: `dotnet ef migrations add InitialCreate`
- [ ] Implement `POST /auth/register` and `POST /auth/login` with JWT issuance
- [ ] Add FluentValidation for all request bodies
- [ ] Implement `POST /keys`, `GET /keys`, `DELETE /keys/{id}`
- [ ] Write unit tests for `ApiKeyGenerator` and validators
- [ ] Dockerize the app and verify it runs locally

### Week 2 — Middleware, rate limiting, and usage

- [ ] Write `ApiKeyMiddleware` (hash → lookup → scope check → expiry check → stamp identity)
- [ ] Integrate the built-in `RateLimiter` with per-key sliding window
- [ ] Implement `RequireScopeAttribute` and scope enforcement
- [ ] Implement `UsageQueue` and `UsageFlushService`
- [ ] Wire usage event recording into middleware
- [ ] Implement `GET /keys/{id}/usage` with LINQ aggregation
- [ ] Write integration tests for key validation and rate limiting

### Week 3 — Deploy and polish

- [ ] Write CDK stack (VPC, RDS, ECS Fargate, ECR, API Gateway, Secrets Manager)
- [ ] Push Docker image to ECR: `docker build` → `docker push`
- [ ] Run `cdk deploy` and verify the live endpoint works end-to-end
- [ ] Set up CloudWatch dashboard (request count, 4xx/5xx rate, latency p99)
- [ ] Write README with architecture diagram, curl usage examples, and one-command deploy
- [ ] Record a short demo GIF: register → create key → hit a protected route → view usage

---

## 12. Resume Talking Points

Use these when describing ApiForge in interviews or on your resume bullet points.

**One-liner for resume:**
> Built a multi-tenant API key management service in C#/.NET 8 with JWT auth, HMAC-style key hashing, per-key rate limiting, and usage analytics, deployed to AWS ECS Fargate via CDK.

**Concepts you can speak to in interviews:**

| Topic | What you built | Why it matters |
|---|---|---|
| Security | SHA-256 key hashing — plaintext never stored | Stripe-style, industry standard |
| Auth | JWT access + refresh token pattern | Every production API uses this |
| Middleware | Validation runs at middleware level, not controller | Shows architectural judgment |
| Rate limiting | Sliding window per API key | Real throttling, not IP-based |
| Multi-tenancy | Tenant ID stamped from key, not from auth header | Clean isolation pattern |
| Background services | `IHostedService` for batched DB writes | Shows async patterns beyond request/response |
| IaC | CDK in C# — one command to provision everything | Cloud-native thinking |
| Testing | `WebApplicationFactory` integration tests + Testcontainers | Tests that actually prove correctness |

**Interview question you can now answer deeply:**
- "How would you design an API key system?"
- "How do you handle rate limiting in a multi-tenant API?"
- "Walk me through how your middleware pipeline works."
- "How do you manage secrets in a cloud-deployed application?"
- "How did you structure your .NET project and why?"

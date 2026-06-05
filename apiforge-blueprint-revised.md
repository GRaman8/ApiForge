# ApiForge — Project Blueprint (Revised)

> **Type:** Backend SaaS Platform
> **Language:** C# (.NET 8)
> **Cloud:** AWS
> **Difficulty:** Mid-level (between CRUD and distributed systems)
> **Estimated build time:** 2–3 weeks
>
> **This is a revised copy of `apiforge-blueprint.md`.** All code bugs from the original are fixed
> inline, an **AWS IAM & Access** section and a **Cost & Teardown** section have been added, and the
> network design is simplified for a learning/portfolio deployment. Changes are marked with
> **`▶ REVISED:`** notes. See `apiforge-blueprint-review.md` for the rationale behind each change.

---

## Table of Contents

1. [Project Summary](#1-project-summary)
2. [Architecture Overview](#2-architecture-overview)
3. [Tech Stack](#3-tech-stack)
4. [Data Model](#4-data-model)
5. [API Reference](#5-api-reference)
6. [Core Features — Implementation Detail](#6-core-features--implementation-detail)
7. [AWS Infrastructure](#7-aws-infrastructure)
8. [CDK Stack Layout](#8-cdk-stack-layout)
9. [AWS IAM & Access (Least Privilege)](#9-aws-iam--access-least-privilege)
10. [Cost & Teardown](#10-cost--teardown)
11. [Project Structure](#11-project-structure)
12. [Best Practices](#12-best-practices)
13. [Week-by-Week Build Plan](#13-week-by-week-build-plan)
14. [Resume Talking Points](#14-resume-talking-points)

---

## 1. Project Summary

ApiForge is a multi-tenant API key management service. It lets developers register, issue, scope,
rate-limit, and track usage of API keys for their own applications.

This is the same core feature set behind Stripe API keys, GitHub Personal Access Tokens, and any
SaaS product that exposes a developer-facing API. The project is intentionally scoped to be
non-trivial (real security patterns, middleware-level validation, multi-tenancy) without requiring
a distributed queue or event-driven architecture.

**What the system does:**

- Users register and authenticate via JWT (access token + a stored, revocable refresh token)
- Authenticated users create API keys with optional scopes and TTL
- Every API key is hashed before storage — plaintext is shown once and never stored
- Incoming requests carry an `X-API-Key` header validated by middleware
- Usage is tracked per key and exposed via a reporting endpoint
- Rate limits are enforced at the middleware level per key

---

## 2. Architecture Overview

**▶ REVISED:** For a learning/portfolio deployment, v1 uses a **public ALB with an ACM
certificate** (TLS terminates at the ALB) and **drops API Gateway**. API Gateway in front of a
private ALB requires a **VPC Link** (it's a regional managed service, not something that lives in a
subnet) and adds cost and moving parts with little benefit here. Add API Gateway + VPC Link later
as a deliberate "stretch."

```
┌─────────────────────────────────────────────────────────┐
│         Public Application Load Balancer (ACM TLS)       │
│         health check: GET /health                        │
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
│  - RefreshTokens    │
│  - UsageEvents      │
└─────────────────────┘
```

**Request flow for a protected endpoint:**

```
Client request (X-API-Key: apf_live_xxxx)
  → Public ALB (TLS)
  → ECS container
  → ApiKeyMiddleware
      → SHA-256 hash the incoming key
      → SELECT * FROM ApiKeys WHERE KeyHash = ?
      → check IsRevoked, ExpiresAt, Scope → stamp TenantId on HttpContext
  → RateLimiter (partitioned by API key id)
  → Route handler executes
  → Stopwatch stops, usage event enqueued (final status + latency)
  → Response returned
```

**▶ REVISED — Middleware pipeline order (load-bearing):**

```
UseRouting → ApiKeyMiddleware (auth + scope + stamp identity) → UseRateLimiter → endpoints
```

The rate limiter partitions on `ctx.Items["ApiKey"]`, which `ApiKeyMiddleware` sets — so the
limiter must run *after* it, or every request partitions as "anon".

---

## 3. Tech Stack

### Language and Runtime

| Tool | Version | Role |
|---|---|---|
| C# | 12 | Primary language |
| .NET | 8 (LTS) | Runtime |
| ASP.NET Core | 8 | Web framework |

### Backend Libraries

| Library | Role |
|---|---|
| `Microsoft.AspNetCore` | HTTP routing, middleware, DI |
| `Microsoft.EntityFrameworkCore` | Database ORM, migrations |
| `Npgsql.EntityFrameworkCore.PostgreSQL` | PostgreSQL EF Core provider |
| `FluentValidation.AspNetCore` | Request validation |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | JWT validation middleware |
| `System.IdentityModel.Tokens.Jwt` | JWT creation |
| `Microsoft.AspNetCore.RateLimiting` | Built-in sliding-window rate limiter (per instance) |
| `BCrypt.Net-Next` | Password hashing |
| `xUnit` | Unit and integration testing |
| `Moq` | Mocking in tests |
| `Testcontainers.PostgreSql` | Real Postgres for integration tests |

### AWS Services

**▶ REVISED:** API Gateway removed from v1; public ALB added.

| Service | Role |
|---|---|
| ECS Fargate | Runs the containerized ASP.NET Core API |
| ECR | Stores the Docker image |
| RDS (PostgreSQL) | Primary database |
| Application Load Balancer | Public HTTPS entry point (ACM cert), routes to ECS |
| Secrets Manager | Stores DB credentials and JWT signing key |
| CloudWatch | Logs and metrics (request count, error rate, latency) |
| VPC | Network isolation — ECS and RDS in private subnets |
| AWS Budgets | Cost alarm (learning-account guardrail) |

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

### RefreshTokens table — **▶ REVISED (new)**

`/auth/refresh` was undefined in the original (no storage, no revocation). A refresh token must be
storable and revocable, hashed at rest, and rotated on use.

```sql
CREATE TABLE "RefreshTokens" (
    "Id"           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "UserId"       UUID NOT NULL REFERENCES "Users"("Id"),
    "TokenHash"    TEXT NOT NULL UNIQUE,   -- SHA-256 of the token, never plaintext
    "ExpiresAt"    TIMESTAMPTZ NOT NULL,
    "RevokedAt"    TIMESTAMPTZ,
    "ReplacedById" UUID,                    -- rotation chain (points to the new token)
    "CreatedAt"    TIMESTAMPTZ NOT NULL DEFAULT now()
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

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
    public DateTime CreatedAt { get; set; }
    public User User { get; set; } = null!;
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
Returns: { "accessToken": "eyJ...", "refreshToken": "..." }   # rotates: old token revoked, new one issued
```

> **▶ REVISED:** `/auth/refresh` now has a defined, revocable, rotating implementation backed by the
> `RefreshTokens` table. The refresh token is hashed (SHA-256) before storage; on use, the matched
> row is checked for `RevokedAt`/`ExpiresAt`, marked revoked, and replaced (`ReplacedById`) by a
> freshly issued token. This makes logout / token theft recoverable.

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

### Usage reporting endpoint

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

### Health endpoint — **▶ REVISED (new)**

```
GET /health
Returns: 200 OK
Purpose: ALB target-group health check. Without it, the default "/" check 404s and the ECS
         service never stabilizes on deploy.
```

---

## 6. Core Features — Implementation Detail

### 6.1 JWT Authentication

Register in `Program.cs`:

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

> **Best practice:** `Jwt:Key` is injected from AWS Secrets Manager into the ECS task as an
> environment variable (`Jwt__Key`). Never commit secrets to source control.

Issue a short-lived access token on login (and a stored refresh token — see 6.7):

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

The most security-critical piece. Never store the plaintext key.

```csharp
public static class ApiKeyGenerator
{
    public static (string plaintext, string hash, string prefix) Generate()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(32);
        var rawKey = Convert.ToBase64String(randomBytes)
            .Replace("+", "")
            .Replace("/", "")
            .Replace("=", "")[..40]; // trim to 40 chars

        var prefix = "apf_live_";
        var plaintext = $"{prefix}{rawKey}";

        // SHA-256 hash — this is what gets stored. (SHA-256 is correct here because the key is
        // already high-entropy/random; BCrypt is only for low-entropy passwords.)
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

### 6.3 Validation Middleware — **▶ REVISED (two bugs fixed)**

Fixes vs. original: (a) the opt-in check is now valid C# with correct logic; (b) the `LastUsedAt`
update is no longer a fire-and-forget on the scoped `DbContext` — it's batched in
`UsageFlushService` (6.6).

```csharp
public class ApiKeyMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, AppDbContext db, UsageQueue usageQueue)
    {
        // ▶ FIXED: skip routes that did NOT opt in to API-key auth
        if (ctx.GetEndpoint()?.Metadata.GetMetadata<RequireApiKeyAttribute>() is null)
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

        // Scope enforcement (see 6.5)
        var requiredScope = ctx.GetEndpoint()?.Metadata.GetMetadata<RequireScopeAttribute>()?.Scope;
        if (requiredScope is not null && !apiKey.Scopes.Contains(requiredScope))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = $"Key lacks required scope: {requiredScope}" });
            return;
        }

        // Stamp the tenant identity onto the request context
        ctx.Items["ApiKey"] = apiKey;
        ctx.Items["TenantId"] = apiKey.UserId;

        // ▶ FIXED: capture latency/status AFTER the handler, then enqueue (no DB write here)
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
        // LastUsedAt is updated in the batched flush (6.6), NOT fire-and-forget here.
    }
}
```

---

### 6.4 Rate Limiting

Use the built-in `Microsoft.AspNetCore.RateLimiting`.

> **▶ REVISED note:** this limiter keeps its sliding window **in memory, per container**. It is
> correct at `DesiredCount = 1` (as deployed here). If you ever scale to 2+ tasks, each instance
> has its own window and the effective limit becomes N× looser — true multi-instance throttling
> needs a shared store (ElastiCache/Redis). Left simple on purpose for this scope.

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("ApiKeyPolicy", ctx =>
    {
        var apiKey = ctx.Items["ApiKey"] as ApiKey;     // set by ApiKeyMiddleware (must run first)
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
.WithMetadata(new RequireApiKeyAttribute())
.RequireRateLimiting("ApiKeyPolicy");
```

---

### 6.5 Scopes and Expiry

```csharp
[AttributeUsage(AttributeTargets.Method)]
public class RequireScopeAttribute(string scope) : Attribute
{
    public string Scope { get; } = scope;
}
```

Scope is enforced inside `ApiKeyMiddleware` (see 6.3) — it has the resolved `apiKey` in hand, so
it's the natural place. Apply to routes:

```csharp
app.MapPost("/items", CreateItem)
    .WithMetadata(new RequireApiKeyAttribute())
    .WithMetadata(new RequireScopeAttribute("write"));

app.MapGet("/items", GetItems)
    .WithMetadata(new RequireApiKeyAttribute())
    .WithMetadata(new RequireScopeAttribute("read"));
```

---

### 6.6 Usage Tracking — **▶ REVISED (LastUsedAt folded into the batch)**

A background `IHostedService` flushes usage events in batches rather than writing on every request.
The flusher *also* updates `LastUsedAt` per key, replacing the buggy fire-and-forget from the
original.

> **Tradeoff (state it honestly):** the in-memory queue is **at-most-once** — up to ~5s of events
> can be lost on task restart/deploy/crash. Fine for analytics; not for billing.

```csharp
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

            // ▶ FIXED: batch LastUsedAt here on a FRESH scoped context (no disposed-context race)
            foreach (var grp in events.GroupBy(e => e.ApiKeyId))
            {
                var last = grp.Max(e => e.RequestedAt);
                await db.ApiKeys.Where(k => k.Id == grp.Key)
                    .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, last), ct);
            }

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
    var tenantId = Guid.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
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

### 6.7 Refresh Tokens — **▶ REVISED (new)**

On **login**, issue an access token *and* a refresh token; store only the hash:

```csharp
static (string plaintext, string hash) NewRefreshToken()
{
    var plaintext = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLower();
    return (plaintext, hash);
}

// inside /auth/login, after password check:
var (rtPlain, rtHash) = NewRefreshToken();
db.RefreshTokens.Add(new RefreshToken
{
    UserId = user.Id,
    TokenHash = rtHash,
    ExpiresAt = DateTime.UtcNow.AddDays(30),
    CreatedAt = DateTime.UtcNow
});
await db.SaveChangesAsync();
return Results.Ok(new { accessToken, refreshToken = rtPlain });
```

On **`/auth/refresh`**, validate + rotate (revoke old, issue new):

```csharp
app.MapPost("/auth/refresh", async (RefreshRequest req, AppDbContext db) =>
{
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.RefreshToken))).ToLower();
    var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash);

    if (token is null || token.RevokedAt is not null || token.ExpiresAt < DateTime.UtcNow)
        return Results.Unauthorized();

    var (newPlain, newHash) = NewRefreshToken();
    var replacement = new RefreshToken
    {
        UserId = token.UserId,
        TokenHash = newHash,
        ExpiresAt = DateTime.UtcNow.AddDays(30),
        CreatedAt = DateTime.UtcNow
    };
    db.RefreshTokens.Add(replacement);

    token.RevokedAt = DateTime.UtcNow;
    token.ReplacedById = replacement.Id;
    await db.SaveChangesAsync();

    var accessToken = IssueAccessToken(token.UserId);   // your JWT helper from 6.1
    return Results.Ok(new { accessToken, refreshToken = newPlain });
});
```

Reuse of an already-revoked token is a theft signal — you can extend this to revoke the whole
chain. For a portfolio v1, rotation + revocation is plenty.

---

## 7. AWS Infrastructure

### Services and their roles — **▶ REVISED (public ALB, no API Gateway/NAT for the cheap path)**

```
┌─────────────────────────────────────────────────────────────┐
│  VPC                                                         │
│                                                              │
│  ┌──────────────────────┐   ┌────────────────────────────┐  │
│  │  Public subnet        │   │  Private (isolated) subnet  │  │
│  │                       │   │                             │  │
│  │  - Public ALB (ACM)   │   │  - ECS Fargate (API)        │  │
│  │                       │   │  - RDS PostgreSQL           │  │
│  └──────────────────────┘   └────────────────────────────┘  │
│                                                              │
│  VPC interface endpoints (so tasks need NO NAT):             │
│    - ECR (api + dkr), S3 gateway endpoint (ECR layers)       │
│    - Secrets Manager                                         │
│    - CloudWatch Logs                                         │
│                                                              │
│  Regional services:                                          │
│    - ECR (image registry)                                    │
│    - Secrets Manager (DB password, JWT signing key)          │
│    - CloudWatch Logs + Metrics                               │
│    - AWS Budgets (cost alarm)                                │
└─────────────────────────────────────────────────────────────┘
```

> **▶ REVISED:** Using `PRIVATE_ISOLATED` subnets + VPC interface endpoints lets ECS pull images
> and read secrets **without a NAT Gateway** — removing the single biggest idle cost (~$32/mo).
> The ALB sits in the public subnet and is the only public ingress.

### Environment variables injected into ECS at runtime — **▶ REVISED**

The DB secret is injected as **individual fields**, not one blob (the original injected the whole
JSON as `ConnectionStrings__Default`, which is not a valid Npgsql connection string).

| Variable | Source |
|---|---|
| `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_USER`, `DB_PASSWORD` | Secrets Manager (individual fields) |
| `Jwt__Key` | Secrets Manager |
| `Jwt__Issuer` | ECS task definition |
| `Jwt__Audience` | ECS task definition |
| `ASPNETCORE_ENVIRONMENT` | ECS task definition (`Production`) |

The app assembles `ConnectionStrings:Default` from the `DB_*` vars at startup:

```csharp
var cs = new Npgsql.NpgsqlConnectionStringBuilder
{
    Host = builder.Configuration["DB_HOST"],
    Port = int.Parse(builder.Configuration["DB_PORT"] ?? "5432"),
    Database = builder.Configuration["DB_NAME"],
    Username = builder.Configuration["DB_USER"],
    Password = builder.Configuration["DB_PASSWORD"],
    SslMode = Npgsql.SslMode.Require
}.ConnectionString;

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(cs));
```

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

### Migration strategy — **▶ REVISED (new)**

RDS is in a private subnet (not reachable from your laptop), so migrations run **inside the VPC** as
a one-off ECS task using the same image with a different entrypoint:

```csharp
// Program.cs — only when RUN_MIGRATIONS=true (used by the migration task, not the web service)
if (builder.Configuration.GetValue<bool>("RUN_MIGRATIONS"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    return; // exit after migrating
}
```

Run it once after each deploy that changes the schema: `aws ecs run-task … --overrides
RUN_MIGRATIONS=true`. Keep migrations out of the normal web container's startup so a crash-loop in
migration can't take the API down.

### First-deploy ordering — **▶ REVISED (avoids the ECR chicken-and-egg)**

The stack creates the ECR repo *and* references `:latest`. On the first deploy the repo is empty,
so split it:

1. `cdk bootstrap` (once per account/region).
2. Deploy the network + ECR (`cdk deploy --context env=dev` of a stack/stage that excludes the
   Fargate service, **or** a stack that tolerates a missing image).
3. `docker build` → `docker push` the image to ECR.
4. Deploy the Fargate service.
5. Run the migration task.
6. Smoke-test `/health`, then the full flow.

---

## 8. CDK Stack Layout — **▶ REVISED**

Environment-aware (cheap/destroyable `dev` vs. hardened `prod`), public ALB, no NAT, budget alarm.

```csharp
public class ApiForgeStack : Stack
{
    public ApiForgeStack(Construct scope, string id, bool isProd, IStackProps? props = null)
        : base(scope, id, props)
    {
        // ▶ No NAT — isolated private subnets + VPC endpoints keep tasks off the internet & cut cost
        var vpc = new Vpc(this, "ApiForgeVpc", new VpcProps
        {
            MaxAzs = isProd ? 2 : 1,
            NatGateways = 0,
            SubnetConfiguration = new[]
            {
                new SubnetConfiguration { Name = "public", SubnetType = SubnetType.PUBLIC, CidrMask = 24 },
                new SubnetConfiguration { Name = "private", SubnetType = SubnetType.PRIVATE_ISOLATED, CidrMask = 24 }
            }
        });
        vpc.AddInterfaceEndpoint("EcrApi", new InterfaceVpcEndpointOptions { Service = InterfaceVpcEndpointAwsService.ECR });
        vpc.AddInterfaceEndpoint("EcrDkr", new InterfaceVpcEndpointOptions { Service = InterfaceVpcEndpointAwsService.ECR_DOCKER });
        vpc.AddInterfaceEndpoint("Secrets", new InterfaceVpcEndpointOptions { Service = InterfaceVpcEndpointAwsService.SECRETS_MANAGER });
        vpc.AddInterfaceEndpoint("Logs", new InterfaceVpcEndpointOptions { Service = InterfaceVpcEndpointAwsService.CLOUDWATCH_LOGS });
        vpc.AddGatewayEndpoint("S3", new GatewayVpcEndpointOptions { Service = GatewayVpcEndpointAwsService.S3 }); // ECR layers

        var dbSecret = new DatabaseSecret(this, "DbSecret", new DatabaseSecretProps { Username = "apiforge" });

        // ▶ Environment-driven lifecycle: dev is destroyable, prod is protected
        var db = new DatabaseInstance(this, "ApiForgeDb", new DatabaseInstanceProps
        {
            Engine = DatabaseInstanceEngine.Postgres(new PostgresInstanceEngineProps { Version = PostgresEngineVersion.VER_16 }),
            Vpc = vpc,
            Credentials = Credentials.FromSecret(dbSecret),
            InstanceType = InstanceType.Of(InstanceClass.T3, InstanceSize.MICRO),
            VpcSubnets = new SubnetSelection { SubnetType = SubnetType.PRIVATE_ISOLATED },
            DeletionProtection = isProd,
            DeleteAutomatedBackups = !isProd,
            RemovalPolicy = isProd ? RemovalPolicy.RETAIN : RemovalPolicy.DESTROY
        });

        var repo = new Repository(this, "ApiForgeRepo", new RepositoryProps { RepositoryName = "apiforge-api" });

        var cluster = new Cluster(this, "ApiForgeCluster", new ClusterProps { Vpc = vpc });

        var taskDef = new FargateTaskDefinition(this, "ApiForgeTask", new FargateTaskDefinitionProps
        {
            MemoryLimitMiB = 512,
            Cpu = 256
        });

        // ▶ Least privilege: task role reads ONLY the specific secrets it needs (see §9)
        dbSecret.GrantRead(taskDef.TaskRole);

        taskDef.AddContainer("ApiContainer", new ContainerDefinitionOptions
        {
            Image = ContainerImage.FromEcrRepository(repo, "latest"),
            PortMappings = new[] { new PortMapping { ContainerPort = 8080 } },
            // ▶ FIXED: inject individual DB fields, not the whole JSON blob
            Secrets = new Dictionary<string, Amazon.CDK.AWS.ECS.Secret>
            {
                ["DB_HOST"]     = Amazon.CDK.AWS.ECS.Secret.FromSecretsManager(dbSecret, "host"),
                ["DB_PORT"]     = Amazon.CDK.AWS.ECS.Secret.FromSecretsManager(dbSecret, "port"),
                ["DB_NAME"]     = Amazon.CDK.AWS.ECS.Secret.FromSecretsManager(dbSecret, "dbname"),
                ["DB_USER"]     = Amazon.CDK.AWS.ECS.Secret.FromSecretsManager(dbSecret, "username"),
                ["DB_PASSWORD"] = Amazon.CDK.AWS.ECS.Secret.FromSecretsManager(dbSecret, "password"),
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

        // ▶ Public ALB with ACM TLS; health check at /health
        var service = new ApplicationLoadBalancedFargateService(this, "ApiForgeService",
            new ApplicationLoadBalancedFargateServiceProps
            {
                Cluster = cluster,
                TaskDefinition = taskDef,
                PublicLoadBalancer = true,
                DesiredCount = 1,
                AssignPublicIp = false,
                TaskSubnets = new SubnetSelection { SubnetType = SubnetType.PRIVATE_ISOLATED }
                // Certificate = <ACM cert>, RedirectHTTP = true   // add for HTTPS
            });
        service.TargetGroup.ConfigureHealthCheck(new HealthCheck { Path = "/health" });

        db.Connections.AllowDefaultPortFrom(service.Service);

        // ▶ Cost guardrail
        new CfnBudget(this, "MonthlyBudget", new CfnBudgetProps
        {
            Budget = new CfnBudget.BudgetDataProperty
            {
                BudgetType = "COST",
                TimeUnit = "MONTHLY",
                BudgetLimit = new CfnBudget.SpendProperty { Amount = 10, Unit = "USD" }
            }
        });

        Tags.Of(this).Add("project", "apiforge");
        Tags.Of(this).Add("env", isProd ? "production" : "dev");
    }
}
```

---

## 9. AWS IAM & Access (Least Privilege)

**▶ REVISED (new section — the requested least-privilege model).**

You asked to drive all AWS interaction through dedicated IAM identities with least privilege. The
important nuance: **the naive version — one IAM user with a giant inline policy granting RDS + ECS +
VPC + IAM — is both insecure and brittle for CDK.** CDK provisions through CloudFormation, which
needs broad permissions *and creates IAM roles itself*; a hand-written mega-policy will be
over-broad and still break. The correct model is **role-based and splits deploy-time from runtime.**

### 9.0 Account hygiene first

- **Never use the root account** for work. Enable MFA on it, store the credentials offline.
- Create a human identity in **IAM Identity Center (SSO)** if available — it issues **short-lived**
  credentials, which beats long-lived access keys.
- If you must use an IAM user with access keys: enforce **MFA**, store keys only in a named CLI
  profile (`~/.aws/credentials`, `[apiforge-deploy]`), **never** in the repo, and rotate them.

### 9.1 Deploy-time identity (whoever runs `cdk deploy`)

`cdk bootstrap` creates four scoped roles that CloudFormation assumes:
`cdk-<qualifier>-deploy-role`, `-cfn-exec-role`, `-file-publishing-role`, `-lookup-role`.
**True least privilege = your deployer identity may only assume those roles** (plus read
CloudFormation) — it does **not** hold RDS/ECS/VPC permissions directly.

Deployer user/role policy (minimal):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "AssumeCdkBootstrapRoles",
      "Effect": "Allow",
      "Action": "sts:AssumeRole",
      "Resource": "arn:aws:iam::<ACCOUNT_ID>:role/cdk-hnb659fds-*-<ACCOUNT_ID>-<REGION>"
    },
    {
      "Sid": "ReadCloudFormation",
      "Effect": "Allow",
      "Action": [
        "cloudformation:DescribeStacks",
        "cloudformation:GetTemplate",
        "cloudformation:ListStacks"
      ],
      "Resource": "*"
    }
  ]
}
```

Lock down what the deploy can *create* by bootstrapping with a scoped execution policy and/or a
**permissions boundary**, so even a compromised deploy can't exceed scope:

```bash
# Bootstrap once, capping the CloudFormation execution role.
cdk bootstrap aws://<ACCOUNT_ID>/<REGION> \
  --qualifier apiforge \
  --cloudformation-execution-policies "arn:aws:iam::aws:policy/PowerUserAccess" \
  --custom-permissions-boundary apiforge-deploy-boundary
```

> For a personal learning account, `PowerUserAccess` on the exec role + a permissions boundary is a
> pragmatic middle ground. For a shared/work account, replace it with a tightly scoped customer-
> managed policy listing only the services this stack touches (ec2, ecs, ecr, rds, secretsmanager,
> elasticloadbalancing, logs, iam:PassRole on the task/exec roles, budgets).

### 9.2 Runtime identity (the ECS task role) — the tightest scope

Two distinct roles, do not conflate them:

- **Execution role** — used by the ECS agent to *pull the image and write logs* (ECR + Logs).
  CDK's `FargateTaskDefinition` creates this for you; the VPC endpoints make it work without NAT.
- **Task role** — used by *your app code* at runtime. It should read **only** the specific secrets,
  nothing else. The blueprint's `dbSecret.GrantRead(taskDef.TaskRole)` is exactly right; extend it
  to the JWT secret and stop there.

Hand-written equivalent of the task-role policy (no wildcards on resources):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "ReadOnlyTheseTwoSecrets",
      "Effect": "Allow",
      "Action": "secretsmanager:GetSecretValue",
      "Resource": [
        "arn:aws:secretsmanager:<REGION>:<ACCOUNT_ID>:secret:ApiForge/DbSecret-*",
        "arn:aws:secretsmanager:<REGION>:<ACCOUNT_ID>:secret:ApiForge/JwtKey-*"
      ]
    }
  ]
}
```

(The app needs no S3, no DynamoDB, no broad CloudWatch — leave them out. CloudWatch Logs write is
on the *execution* role, not the task role.)

### 9.3 Least-privilege checklist

- [ ] Root account: MFA on, credentials offline, never used for daily work.
- [ ] Deployer identity can only assume the CDK bootstrap roles + read CloudFormation.
- [ ] CDK bootstrapped with a scoped execution policy **and** a permissions boundary.
- [ ] Task role reads only the two specific secret ARNs; no `Resource: "*"`.
- [ ] Execution role limited to ECR pull + Logs write (CDK default + VPC endpoints).
- [ ] Prefer SSO/short-lived creds; if access keys, MFA + named profile + rotate, never in git.
- [ ] All resources tagged `project=apiforge` for cost/audit visibility.

---

## 10. Cost & Teardown

**▶ REVISED (new section — this is a learning project, so cost discipline matters).**

| Cost item | Default trap | Cheap/learning setting |
|---|---|---|
| **NAT Gateway** | ~$32/mo + data, runs 24/7 | `NatGateways = 0` + VPC interface endpoints (§8) |
| **RDS lifecycle** | `RETAIN` + `DeletionProtection` survive `cdk destroy` → silent billing | dev: `DESTROY`, `DeletionProtection=false`, `DeleteAutomatedBackups=true` |
| **RDS instance** | larger classes cost more | `t3.micro`, single-AZ in dev |
| **Idle stack** | left running for weeks | `cdk destroy` between sessions |
| **CloudWatch logs** | unlimited retention accrues | `RetentionDays.ONE_MONTH` |
| **Surprise bill** | no alarm | AWS Budgets alarm at ~$10/mo (§8) |

**Teardown:** in `dev`, `cdk destroy` removes everything including the database (because
`RemovalPolicy.DESTROY`). Confirm in the console that the RDS instance and any leftover ENIs/EIPs
are gone afterward. Treat "deploy → test → destroy" as the normal cycle, not "deploy once and
forget."

---

## 11. Project Structure

```
ApiForge/
├── ApiForge.sln
│
├── ApiForge.Api/                          # Main ASP.NET Core project
│   ├── Program.cs                         # App setup, DI, route mapping, migration gate
│   ├── appsettings.json                   # Non-secret config (Jwt:Issuer, Jwt:Audience)
│   ├── appsettings.Production.json        # Production overrides (no secrets here)
│   │
│   ├── Domain/                            # EF Core models
│   │   ├── User.cs
│   │   ├── ApiKey.cs
│   │   ├── RefreshToken.cs                # ▶ new
│   │   └── UsageEvent.cs
│   │
│   ├── Data/                              # EF Core DbContext + migrations
│   │   ├── AppDbContext.cs
│   │   └── Migrations/
│   │
│   ├── Features/                          # Vertical slice
│   │   ├── Auth/
│   │   │   ├── AuthEndpoints.cs           # register / login / refresh
│   │   │   ├── LoginRequest.cs
│   │   │   ├── RegisterRequest.cs
│   │   │   ├── RefreshRequest.cs          # ▶ new
│   │   │   └── LoginRequestValidator.cs
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
│   │   ├── UsageQueue.cs
│   │   └── UsageFlushService.cs
│   │
│   └── Health/
│       └── HealthEndpoint.cs              # ▶ new — GET /health
│
├── ApiForge.Tests/                        # xUnit test project
│   ├── Unit/
│   │   ├── ApiKeyGeneratorTests.cs
│   │   └── UsageQueueTests.cs
│   └── Integration/
│       ├── AuthEndpointTests.cs
│       ├── RefreshTokenTests.cs           # ▶ new
│       └── KeyEndpointTests.cs
│
└── ApiForge.Cdk/                          # CDK infrastructure project
    ├── ApiForge.Cdk.csproj
    ├── Program.cs                         # reads --context env=dev|prod
    └── ApiForgeStack.cs
```

---

## 12. Best Practices

### Security

- **Never store plaintext API keys.** Store only the SHA-256 hash. Show plaintext exactly once.
- **SHA-256 for keys, BCrypt for passwords.** Keys are high-entropy random (fast hash is fine);
  passwords are low-entropy (need an intentionally slow hash). Don't swap these.
- **Use Secrets Manager for all credentials.** No secrets in `appsettings.json` or baked into the image.
- **Short-lived JWTs** (15 min) + **stored, revocable, rotating refresh tokens** (§6.7).
- **Validate all inputs with FluentValidation.** No raw model binding without validators.
- **Enforce HTTPS** at the ALB (ACM cert); add `app.UseHttpsRedirection()` for local.

### Architecture

- **API key validation in middleware**, not controllers — enforced globally.
- **DbContext is Scoped**, never Singleton. No fire-and-forget on a scoped context (see the fixed
  `LastUsedAt` batching in §6.6).
- **Explicit middleware order:** `UseRouting → ApiKeyMiddleware → UseRateLimiter → endpoints`.
- **`IHostedService` for background work** (usage flush), not detached tasks.
- **Vertical slice feature folders.**
- **Infrastructure as code from day one**, environment-aware (dev vs prod).

### .NET / C# specifics

- **Minimal APIs**, not MVC controllers.
- **`async`/`await` for all I/O.** Never `.Result`/`.Wait()`.
- **`CancellationToken`** in async methods (e.g. the flush loop).
- **`record` types** for request/response DTOs.
- **`DateTimeKind.Utc`** everywhere; store UTC.
- **Migrations:** auto-migrate in local dev; in AWS, run as a **separate one-off ECS task** gated by
  `RUN_MIGRATIONS` (§7), never on the web container's startup path.

### Testing

- **Unit test pure logic** (key generation, validators) with xUnit, no dependencies.
- **Integration test endpoints** with `WebApplicationFactory<Program>`.
- **Real Postgres via Testcontainers**, not SQLite — catches array/UUID/timestamptz behavior.
- **Mock external dependencies** (Secrets Manager) with Moq in unit tests.
- **Cover the refresh-token rotation + revocation path** explicitly.

### AWS

- **ECS + RDS in private (isolated) subnets**; only the ALB is public.
- **VPC interface endpoints** for ECR/Secrets/Logs so tasks need no NAT.
- **IAM task role reads only the specific secret ARNs** (§9). No hardcoded AWS credentials.
- **`DeletionProtection`/`RETAIN` only in prod**; dev is destroyable.
- **`RetentionDays.ONE_MONTH`** on log groups.
- **Budget alarm** + tags (`project=apiforge`, `env=…`) for cost visibility.

---

## 13. Week-by-Week Build Plan

### Week 1 — Core API and auth (local-first)

- [ ] `docker-compose` with the app + Postgres so everything runs locally before any AWS work.
- [ ] Create solution: `dotnet new sln`, `dotnet new webapi`, `dotnet new xunit`.
- [ ] EF Core + Postgres; define `User`, `ApiKey`, `RefreshToken`, `UsageEvent`.
- [ ] First migration: `dotnet ef migrations add InitialCreate`.
- [ ] `POST /auth/register`, `POST /auth/login` (JWT + stored refresh token), `POST /auth/refresh` (rotation).
- [ ] FluentValidation for all request bodies.
- [ ] `POST /keys`, `GET /keys`, `DELETE /keys/{id}`; add `GET /health`.
- [ ] Unit tests for `ApiKeyGenerator`, validators; integration test for refresh rotation.
- [ ] Dockerize and verify it runs locally end-to-end.

### Week 2 — Middleware, rate limiting, usage

- [ ] `ApiKeyMiddleware` (opt-in check → hash → lookup → scope → expiry → stamp identity).
- [ ] Built-in `RateLimiter`, per-key sliding window; confirm middleware order.
- [ ] `RequireScopeAttribute` + enforcement in middleware.
- [ ] `UsageQueue` + `UsageFlushService` (events **and** batched `LastUsedAt`).
- [ ] Usage recorded **after** the handler (final status + latency).
- [ ] `GET /keys/{id}/usage` with LINQ aggregation.
- [ ] Integration tests for key validation, scope, and rate limiting (429).

### Week 3 — Deploy (cheap) and polish

- [ ] `cdk bootstrap` with qualifier + permissions boundary (§9).
- [ ] Write the environment-aware CDK stack (VPC no-NAT + endpoints, RDS, ECS, ECR, public ALB, budget).
- [ ] Deploy network + ECR first; `docker build` → `docker push`; then deploy the service.
- [ ] Run the **migration task** (`RUN_MIGRATIONS=true`).
- [ ] Smoke-test `/health`, then register → create key → hit protected route → view usage.
- [ ] CloudWatch dashboard (request count, 4xx/5xx, latency p99).
- [ ] README: architecture diagram, curl examples, one-command deploy, **and teardown** (`cdk destroy`).
- [ ] `cdk destroy` to confirm a clean, billing-free teardown.

---

## 14. Resume Talking Points

**One-liner for resume:**
> Built a multi-tenant API key management service in C#/.NET 8 with JWT auth (rotating refresh
> tokens), SHA-256 key hashing, per-key rate limiting, and batched usage analytics; deployed to AWS
> ECS Fargate via CDK with least-privilege IAM, a NAT-free VPC-endpoint network, and cost guardrails.

**Concepts you can speak to in interviews:**

| Topic | What you built | Why it matters |
|---|---|---|
| Security | SHA-256 key hashing; plaintext never stored | Stripe-style, industry standard |
| Auth | JWT access + **stored, rotating, revocable** refresh tokens | Real session management, not just a token |
| Middleware | Validation + scope at middleware level, explicit pipeline order | Architectural judgment |
| Rate limiting | Sliding window per API key (and you can explain the single-instance limit) | Honest about tradeoffs |
| Multi-tenancy | Tenant ID stamped from the key | Clean isolation |
| Background services | `IHostedService` for batched DB writes (events + LastUsedAt) | Async patterns beyond request/response |
| IaC | Environment-aware CDK; one command per env | Cloud-native thinking |
| Least-privilege IAM | Bootstrap-role deploy model + scoped task role + permissions boundary | Security maturity, not a mega-policy |
| Cost discipline | NAT-free VPC endpoints, destroyable dev, budget alarm | Engineers who don't burn money |
| Testing | `WebApplicationFactory` + Testcontainers; refresh-rotation covered | Tests that prove correctness |

**Interview questions you can now answer deeply:**
- "How would you design an API key system?"
- "How do you handle rate limiting in a multi-tenant API — and where does it break?"
- "Walk me through your middleware pipeline and its ordering constraints."
- "How do you manage secrets and IAM least privilege in a CDK-deployed app?"
- "How do you keep a learning AWS account from running up a bill?"
```

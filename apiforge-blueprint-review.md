# ApiForge Blueprint — Review & Evaluation

> Reviewing `apiforge-blueprint.md` (the AI-generated build plan).
> Context: this is a **learning / portfolio** project (it ends in "Resume Talking Points"),
> so my recommendations optimize for *learning value, low AWS cost, and easy teardown* —
> not for a production SLA.

---

## TL;DR — is the approach good?

**Yes, the *shape* of the plan is good. No, you can't follow it literally.**

The blueprint shows genuinely sound instincts: a well-scoped problem (API-key management is
non-trivial but not distributed-systems-hard), correct security fundamentals (hash-don't-store
keys, BCrypt for passwords, short-lived JWTs, Secrets Manager), infrastructure-as-code from day
one, and a clean vertical-slice project layout. As a *study guide* it's above average.

But if you hand it to an AI agent (or yourself) and build it line-by-line, **it will not deploy
cleanly on the first try.** There are ~4 real bugs in the code snippets, several
deployment chicken-and-egg problems, an auth feature (`/auth/refresh`) that has no backing data
model, and a couple of AWS settings that are actively wrong for a throwaway learning stack.

Treat it as **a strong outline to argue with, not a spec to obey.**

**My one-line verdict:** *Great bones, buggy in the details, and missing the "make the first
deploy actually work" glue. Fix the four code bugs, add the missing refresh-token + migration +
health-check pieces, simplify the network for v1, and put cost guardrails in — then it's an
excellent portfolio project.*

---

## What's genuinely good (keep all of this)

- **Hash-not-store for API keys, plaintext shown once.** This is exactly how Stripe/GitHub do it.
  Using **SHA-256** for the *API key* (high-entropy random secret) and **BCrypt** for *passwords*
  (low-entropy human input) is the correct distinction — many people get this backwards.
- **Secrets in AWS Secrets Manager**, not `appsettings.json`. Correct.
- **Short-lived (15 min) access tokens.** Correct.
- **Validation in middleware, not controllers.** Good architectural judgment — it's enforced globally.
- **Vertical-slice feature folders** (`Features/Auth`, `Features/Keys`). Scales better than
  layer-folders for a project this size.
- **IaC from day one with CDK in C#.** No console click-ops. Right call.
- **Testcontainers (real Postgres) over SQLite for integration tests.** SQLite would hide
  Postgres-specific behavior (arrays, `gen_random_uuid`, `TIMESTAMPTZ`). Good.
- **Batched usage writes via `IHostedService`** instead of a DB write per request. Right pattern.

If the rest of the document were as good as these decisions, there'd be little to say.

---

## Must-fix: real bugs in the code snippets

These aren't style nits — they won't compile or won't behave as written.

### 1. The opt-in middleware check won't compile (§6.3)

```csharp
// As written — broken:
if (!ctx.GetEndpoint()?.Metadata.GetMetadata<RequireApiKeyAttribute>() is not null)
```

`!` is being applied to a nullable *attribute* (not a `bool`), and the `!… is not null` logic is
also inverted. Correct version:

```csharp
// Skip routes that did NOT opt in to API-key auth:
if (ctx.GetEndpoint()?.Metadata.GetMetadata<RequireApiKeyAttribute>() is null)
{
    await next(ctx);
    return;
}
```

### 2. Fire-and-forget `LastUsedAt` update reuses a disposed `DbContext` (§6.3)

```csharp
// As written — race / ObjectDisposedException:
_ = db.ApiKeys
    .Where(k => k.Id == apiKey.Id)
    .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, DateTime.UtcNow));
```

`db` is the **scoped** `AppDbContext`. The request finishes, the DI scope disposes the context,
and this un-awaited background task then runs against a disposed `DbContext`. This directly
contradicts the blueprint's *own* best-practices ("DbContext is Scoped / not thread-safe" and "no
fire-and-forget"). **Fix:** fold `LastUsedAt` into the same `UsageFlushService` batch (take the max
`RequestedAt` per key when you flush), so it's one batched write on a fresh scoped context.

### 3. The DB secret can't be used as a connection string (§7/§8)

```csharp
// As written — injects the whole JSON blob, not a connection string:
["ConnectionStrings__Default"] = Secret.FromSecretsManager(dbSecret)
```

`DatabaseSecret` stores JSON like `{"username":"…","password":"…","host":"…","port":5432,…}`.
Injecting that blob as `ConnectionStrings__Default` gives EF Core garbage — it can't connect.
**Fix:** inject the fields individually and assemble the string at startup:

```csharp
// CDK — inject individual fields:
Secrets = new()
{
    ["DB_HOST"]     = Secret.FromSecretsManager(dbSecret, "host"),
    ["DB_PORT"]     = Secret.FromSecretsManager(dbSecret, "port"),
    ["DB_NAME"]     = Secret.FromSecretsManager(dbSecret, "dbname"),
    ["DB_USER"]     = Secret.FromSecretsManager(dbSecret, "username"),
    ["DB_PASSWORD"] = Secret.FromSecretsManager(dbSecret, "password"),
}
```

```csharp
// Program.cs — build the connection string from env:
var cs = new Npgsql.NpgsqlConnectionStringBuilder
{
    Host     = builder.Configuration["DB_HOST"],
    Port     = int.Parse(builder.Configuration["DB_PORT"] ?? "5432"),
    Database = builder.Configuration["DB_NAME"],
    Username = builder.Configuration["DB_USER"],
    Password = builder.Configuration["DB_PASSWORD"],
    SslMode  = Npgsql.SslMode.Require
}.ConnectionString;
```

### 4. Usage events record status/latency *before* the handler runs (§6.6)

The enqueue snippet reads `ctx.Response.StatusCode` and a stopwatch, but doesn't show *where* it
runs. To capture the real status code and latency it must run **after** `await next(ctx)`:

```csharp
var sw = Stopwatch.StartNew();
await next(ctx);
sw.Stop();

usageQueue.Enqueue(new UsageEvent
{
    ApiKeyId   = apiKey.Id,
    Endpoint   = $"{ctx.Request.Method} {ctx.Request.Path}",
    Method     = ctx.Request.Method,
    StatusCode = ctx.Response.StatusCode,        // now the FINAL status
    LatencyMs  = (int)sw.ElapsedMilliseconds,    // now the REAL latency
    RequestedAt = DateTime.UtcNow
});
```

As written, every event would log `200` and ~`0ms`.

---

## Should-fix: design & completeness gaps

### 5. `/auth/refresh` has no backing data model

The API exposes `POST /auth/refresh`, but there is **no refresh-token storage** anywhere in the
data model, and no rotation/revocation story. As written it's undefined: if refresh tokens are
just stateless JWTs you can't revoke them (bad); if they're stored, you need a table that doesn't
exist. **Fix:** add a `RefreshTokens` table and rotate on use —

```sql
CREATE TABLE "RefreshTokens" (
    "Id"           UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    "UserId"       UUID NOT NULL REFERENCES "Users"("Id"),
    "TokenHash"    TEXT NOT NULL UNIQUE,       -- SHA-256 of the token, never plaintext
    "ExpiresAt"    TIMESTAMPTZ NOT NULL,
    "RevokedAt"    TIMESTAMPTZ,
    "ReplacedById" UUID,                        -- rotation chain
    "CreatedAt"    TIMESTAMPTZ NOT NULL DEFAULT now()
);
```

— or honestly just drop `/auth/refresh` from v1 and say so. Don't ship a half-defined auth flow.

### 6. API Gateway can't reach the private ALB (no VPC Link)

The architecture diagram draws "API Gateway in a public subnet" (API Gateway is a *regional
managed service* — it isn't in any subnet) and sets the ALB to `PublicLoadBalancer = false`, but
never adds a **VPC Link**. Without one, API Gateway physically cannot route to the private ALB.
And honestly, API Gateway *in front of* an ALB+Fargate is largely redundant for this project.

**For a learning project, simplify v1:** drop API Gateway, use a **public ALB with an ACM
certificate** (TLS terminates at the ALB). Fewer moving parts, lower cost, and it actually works.
Add API Gateway + VPC Link later as a deliberate "stretch" if you want the talking point.

### 7. No `/health` endpoint → service never goes healthy

`ApplicationLoadBalancedFargateService` health-checks `/` by default. A minimal API returns 404
there, so the target group never reports healthy and the ECS service fails to stabilize on first
deploy. **Fix:** `app.MapGet("/health", () => Results.Ok());` and point the target group's
health-check path at `/health`.

### 8. No migration strategy for the deployed DB

Best-practices say "run migrations as a separate pre-deploy step," but Week 3 has no such step —
and RDS lives in a private subnet you can't reach from your laptop. **Fix:** run migrations as a
one-off ECS task inside the VPC (`context.Database.MigrateAsync()` behind a startup flag, or a
dedicated migration entrypoint). Decide this *before* the first deploy, not during the outage.

### 9. ECR chicken-and-egg on first deploy

The stack *creates* the ECR repo and *references* `ContainerImage.FromEcrRepository(repo,
"latest")` in the same deploy. On the very first `cdk deploy` the repo is empty, so Fargate can't
pull `latest` and the service hangs. **Fix:** split the deploy — provision ECR (+ bootstrap)
first, `docker push` the image, *then* deploy the service. Document the ordering.

### 10. The rate limiter is per-instance (fine now, name it)

`Microsoft.AspNetCore.RateLimiting` keeps its window **in memory per container**. At
`DesiredCount = 1` (as specified) it's correct. But the resume claim of "real throttling" should
note that the moment you scale to 2+ tasks, each has its own window and limits become N× looser —
a shared store (ElastiCache/Redis) is needed for true multi-instance throttling. One honest
sentence in the doc; keep the simple version for the portfolio.

### 11. Middleware order isn't stated — and it matters

The rate-limiter partition reads `ctx.Items["ApiKey"]`, which `ApiKeyMiddleware` *sets*. So the
pipeline order is load-bearing:

```
UseRouting → ApiKeyMiddleware (auth + scope + stamp identity) → UseRateLimiter → endpoints
```

If `UseRateLimiter` runs before `ApiKeyMiddleware`, every request partitions as "anon". Add a
short "middleware pipeline order" note so this isn't discovered by accident.

### 12. & 13. Smaller notes

- **In-memory usage queue loses ~5s of events** on task restart/deploy/crash. Fine for analytics —
  just state the at-most-once tradeoff rather than implying it's lossless.
- **Two redundant secret-loading paths.** The blueprint both injects secrets via the ECS task
  definition *and* has a `SecretsManagerConfig.cs` that pulls them via SDK at startup. Pick one —
  for ECS, task-definition injection is cleaner. Drop the SDK path (or keep it only for local dev).

---

## Cost & teardown (this is a learning project — these matter)

### 14. RDS is set to survive `cdk destroy` — wrong for learning

```csharp
DeletionProtection = true,
RemovalPolicy = RemovalPolicy.RETAIN
```

With these, `cdk destroy` leaves the RDS instance **running and billing** indefinitely. For a
throwaway learning stack that's a money trap. Make it environment-driven: dev gets
`RemovalPolicy.DESTROY`, `DeletionProtection = false`, `deleteAutomatedBackups: true`; keep
RETAIN only for a real prod environment.

### 15. The NAT Gateway is the silent cost leader

A NAT Gateway is ~$32/month *plus* data processing — usually the biggest line item in a stack
like this, and it runs 24/7 whether you use it or not. **Options for a cheap learning stack:**
use `PRIVATE_ISOLATED` subnets + **VPC interface endpoints** for ECR / Secrets Manager /
CloudWatch (so the task needs no NAT at all), or accept a single NAT but **`cdk destroy` between
sessions**. Either way, don't leave it running idle for a month.

### 16. Ship a budget alarm

Add an **AWS Budgets** alarm (e.g. $10/month) to the CDK stack so a forgotten NAT or RDS instance
emails you instead of surprising you on the bill. Cheap insurance for a learning account.

---

## AWS IAM & least-privilege access (your explicit ask)

You asked to drive *all* AWS interaction through dedicated IAM identities with least privilege.
Good instinct — but the naive version ("one IAM user with a big inline policy granting RDS + ECS +
VPC + …") is both **insecure and brittle** for CDK. The right model is role-based and splits
deploy-time from runtime. Full details and ready-to-paste policy JSON are in the **revised
blueprint** (new "AWS IAM & Access" section); the short version:

- **Don't use the root account.** MFA it, lock it away, never use it for daily work.
- **Deploy-time identity** (whoever runs `cdk deploy`): with **CDK bootstrap**, CloudFormation
  assumes scoped bootstrap roles (`…-deploy-role`, `…-cfn-exec-role`, `…-file-publishing-role`,
  `…-lookup-role`). True least privilege = your human/CI identity may only `sts:AssumeRole` onto
  those roles (+ `cloudformation:*` on the stack) — **not** a sprawling inline policy. Put a
  **permissions boundary** on the `cfn-exec-role` so even a deploy can't exceed scope.
- **Runtime identity** (the ECS **task role**): grant only `secretsmanager:GetSecretValue` on the
  **specific secret ARNs** (the blueprint's `dbSecret.GrantRead` is the right pattern — extend it
  to the JWT secret) and `logs:` on the task's log group. ECR pull belongs to the *execution* role,
  not the task role. No `Resource: "*"`.
- **Prefer short-lived credentials** (IAM Identity Center / `aws sts`) over long-lived access keys.
  If you must use access keys: MFA the user, store keys only in a named CLI profile, never in git,
  and rotate.

The key reframe: *least privilege for CDK is achieved through the bootstrap-role model + a
permissions boundary, not by hand-writing a monolithic user policy.*

---

## How I'd actually sequence the build

The blueprint's week-by-week plan is reasonable, but I'd reorder around **"make it work locally
and provably, then make it cheap to deploy, then make it fancy."**

1. **Local-first.** `docker-compose` with the app + Postgres. Get the *entire* feature set working
   and green under `WebApplicationFactory` + Testcontainers **before touching AWS.** Bugs #1–#4
   above all surface here for free, at zero cost.
2. **Simplest infra that works.** Public ALB + ACM, single AZ, no API Gateway / VPC Link. Get a
   real end-to-end request working against RDS. *Then* layer extras on as deliberate commits —
   each (private subnets, VPC endpoints, API Gateway, refresh tokens) becomes its own clean resume
   talking point instead of one undifferentiated "deployed to AWS."
3. **Environment-aware CDK from the start.** A `dev` context (destroyable, single-AZ, no deletion
   protection, budget alarm) and a `prod` context (retain, multi-AZ). Toggle with
   `--context env=dev|prod`.
4. **A scripted, repeatable first deploy:** `cdk bootstrap` → deploy ECR + network → build & push
   image → deploy service → run migration task → smoke-test `/health` and a full
   `register → create key → hit a protected route → view usage` flow.

---

## Priority order (if you fix nothing else, fix these)

| # | Item | Why it's this priority |
|---|------|------------------------|
| 1 | Bugs #1–#4 (compile/runtime) | The app literally won't build/run/connect otherwise |
| 2 | `/health` endpoint (#7) | First deploy hangs without it |
| 3 | Connection-string assembly (#3) | App can't reach the DB otherwise |
| 4 | Migration strategy (#8) + ECR ordering (#9) | First deploy fails without a plan |
| 5 | Cost guardrails (#14–#16) | Avoid a surprise bill on a learning account |
| 6 | Refresh-token table or drop it (#5) | Don't ship half-defined auth |
| 7 | IAM least-privilege model (§ above) | Your explicit goal; do it right (roles, not a mega-user) |
| 8 | Simplify network for v1 (#6) | Less to get wrong; cheaper |

Everything else (rate-limiter caveat, middleware-order note, queue-loss note, redundant secret
path) is a one-or-two-line clarification — worth doing, not blocking.

---

*See `apiforge-blueprint-revised.md` for the full blueprint with every fix above applied inline,
plus the new "AWS IAM & Access" and "Cost & Teardown" sections.*

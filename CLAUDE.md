# CLAUDE.md — ApiForge

Guidance for AI agents working in this repo. Read this before making changes.

## What this is

A learning/portfolio **multi-tenant API key management service**. C# minimal API + EF Core +
PostgreSQL, deployed to AWS ECS Fargate via CDK. Built from `apiforge-blueprint-revised.md` (the
reviewed/patched spec). `apiforge-blueprint.md` is the original AI-generated draft;
`apiforge-blueprint-review.md` explains what was wrong with it and why the revised version differs.

## Key facts & deviations (don't "fix" these without reason)

- **Targets .NET 10**, not .NET 8 as the blueprint says — the installed SDK is 10. Dockerfile uses
  `mcr.microsoft.com/dotnet/{sdk,aspnet}:10.0`. The code shape is identical to the blueprint.
- **Solution file is `ApiForge.slnx`** (new XML format from the .NET 10 SDK), not `.sln`.
- **EF Core is pinned to 10.0.8** in `ApiForge.Api.csproj` (explicit `Microsoft.EntityFrameworkCore`
  + `.Relational`). This resolves a version conflict where the Npgsql provider pulls 10.0.4. Don't
  drop these pins or the test project fails to compile (CS1705).
- **CDK uses `ContainerImage.FromAsset("..")`** — CDK builds & publishes the image itself in one
  `cdk deploy`. This intentionally replaces the blueprint's manual "create ECR repo + docker push"
  flow to avoid the first-deploy chicken-and-egg. Requires Docker running at deploy time.
- `ApiForge.Cdk.csproj` sets `DefaultItemExcludes` to ignore `cdk.out/**` — `cdk synth` stages the
  Docker build context there, and without the exclude those copied `.cs` files break the build.

## Layout

```
ApiForge.Api/    Domain/  Data/  Features/{Auth,Keys,Usage,Items}/  Middleware/  Services/  Infrastructure/
ApiForge.Tests/  Unit/  Integration/ (Testcontainers + WebApplicationFactory)
ApiForge.Cdk/    Program.cs (env=dev|prod context)  ApiForgeStack.cs  cdk.json
Dockerfile, docker-compose.yml at repo root
```

## Conventions

- **Minimal APIs**, endpoints grouped per feature via `Map*Endpoints()` extension methods, mapped in
  `Program.cs`.
- **Middleware order is load-bearing:** `UseRouting → UseAuthentication → UseAuthorization →
  ApiKeyMiddleware → UseRateLimiter → endpoints`. The rate-limiter partition reads
  `ctx.Items["ApiKey"]`, which `ApiKeyMiddleware` stamps. Don't reorder.
- **Two auth schemes:** JWT bearer for `/auth/*`, `/keys/*`, `/keys/{id}/usage`; **X-API-Key** for
  `/items/*` (gated by `[RequireApiKey]` + `[RequireScope]` metadata, enforced in `ApiKeyMiddleware`).
- **Hashing:** SHA-256 for API keys *and* refresh tokens (high-entropy → fast hash is fine), BCrypt
  for passwords. `ApiKeyMiddleware.Hash()` is the shared SHA-256 helper. Never store plaintext.
- **Usage tracking:** `ApiKeyMiddleware` enqueues a `UsageEvent` *after* `await next` (final status +
  latency). `UsageFlushService` (BackgroundService) batches inserts every 5s on a **fresh scoped
  DbContext** and updates `LastUsedAt` in the same batch. Never write to the request-scoped DbContext
  fire-and-forget.
- **Validation:** FluentValidation; call `validator.ToProblemAsync(req)` (custom extension in
  `Infrastructure/ValidationExtensions.cs`) — note the name avoids clashing with FluentValidation's
  own `ValidateAsync`.
- **Config:** connection string from `ConnectionStrings:Default` (local) or assembled from `DB_*`
  env vars (ECS/Secrets Manager). `Jwt:Key` from `Jwt__Key` (Secrets Manager) or appsettings (dev).
- **Migrations:** auto-applied in Development on startup; in AWS run a one-off task with
  `RUN_MIGRATIONS=true` (the app migrates and exits). `DesignTimeDbContextFactory` exists so
  `dotnet ef` doesn't execute `Program.cs`.

## Commands

```bash
dotnet build                                   # builds ApiForge.slnx
dotnet test --filter "FullyQualifiedName~Unit" # no Docker needed
dotnet test                                    # full suite — needs Docker daemon running
docker compose up --build                      # local API + Postgres on :8080

export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef migrations add <Name> --project ApiForge.Api --output-dir Data/Migrations

cd ApiForge.Cdk && npx cdk synth  --context env=dev      # validate template (no creds/Docker)
cd ApiForge.Cdk && npx cdk deploy --context env=dev      # needs AWS creds + Docker
```

## Gotchas

- **Docker daemon:** Testcontainers integration tests and compose need a running daemon. On this WSL2
  box it is **not auto-started** — `sudo service docker start` (needs an interactive password, so the
  user must run it; suggest `! sudo service docker start`). Unit tests do not need Docker.
- **AWS CLI is not installed** by default here — see README §0 for the install snippet.
- **Node 25** prints an "untested version" banner from the CDK CLI; harmless. Silence with
  `JSII_SILENCE_WARNING_UNTESTED_NODE_VERSION=1`.
- `npx cdk` downloads the CDK CLI on first run.
- Integration tests share one Postgres container per collection (`[Collection("api")]`); use **unique
  emails per test** (the helpers already do) to avoid cross-test collisions.

## Verified state (as of last work)

- `dotnet build` (all 3 projects): clean, 0 warnings/errors.
- Unit tests: 6/6 pass.
- Integration tests: compile & are correct; **not executed here** because the Docker daemon was not
  running. Run `dotnet test` after starting Docker to confirm.
- `cdk synth --context env=dev`: succeeds (valid template).
- `cdk deploy`: **not run** (no AWS account configured in this environment).

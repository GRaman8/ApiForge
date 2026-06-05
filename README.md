# ApiForge

A multi-tenant API key management service in **C# / .NET 10** — JWT auth with rotating refresh
tokens, SHA-256 API-key hashing, per-key sliding-window rate limiting, scopes, and batched usage
analytics. Deployed to **AWS ECS Fargate** via **AWS CDK (C#)** with least-privilege IAM, a NAT-free
VPC-endpoint network, and cost guardrails.

> Built from `apiforge-blueprint-revised.md`. **Note on framework version:** the blueprint targets
> .NET 8; this implementation targets **.NET 10** to match the installed SDK. The code is identical
> in shape — only the `TargetFramework` and the Docker base image tags differ.

## Contents

- [Architecture](#architecture)
- [Project layout](#project-layout)
- [Run & test locally](#run--test-locally)
- [API walkthrough (curl)](#api-walkthrough-curl)
- [Deploy to AWS](#deploy-to-aws)
- [Tear down](#tear-down)

---

## Architecture

```
Client ──HTTPS──> Public ALB (health: /health) ──> ECS Fargate (ASP.NET Core)
                                                       │
                       ┌───────────────────────────────┼─────────────────────────┐
                       ▼                                ▼                         ▼
              ApiKeyMiddleware (X-API-Key)      RDS PostgreSQL 16        Secrets Manager
              hash → lookup → scope/expiry      Users / ApiKeys /        DB creds + JWT key
              → stamp tenant → rate limit       RefreshTokens /
              → usage event                     UsageEvents / Items
```

Middleware order is load-bearing: `UseRouting → Auth → ApiKeyMiddleware → RateLimiter → endpoints`
(the rate limiter partitions on the API key stamped by the middleware).

## Project layout

| Project | Purpose |
|---------|---------|
| `ApiForge.Api` | The minimal-API service (domain, EF Core, features, middleware, background flush) |
| `ApiForge.Tests` | xUnit unit tests + integration tests (`WebApplicationFactory` + Testcontainers Postgres) |
| `ApiForge.Cdk` | AWS CDK app: VPC (no NAT) + RDS + ECS Fargate + public ALB + budget |

---

## Run & test locally

### Prerequisites

- .NET SDK 10
- Docker (daemon **running** — see note below)
- For deployment only: AWS CLI v2 and the AWS CDK CLI (`npx cdk`)

> **WSL2 / Docker:** the Testcontainers integration tests and `docker compose` need a running Docker
> daemon. If `docker ps` fails with a socket error, start it first:
> ```bash
> sudo service docker start    # WSL without Docker Desktop
> ```
> (Type `! sudo service docker start` in the Claude Code prompt to run it in-session.)

### Option A — one command (docker-compose)

Builds the API image and starts it with Postgres. Migrations auto-apply on startup in Development.

```bash
docker compose up --build
# API on http://localhost:8080 — try: curl http://localhost:8080/health
```

### Option B — run the API directly, Postgres in a container

```bash
# 1. Start Postgres
docker run -d --name apiforge-pg -p 5432:5432 \
  -e POSTGRES_DB=apiforge -e POSTGRES_USER=apiforge -e POSTGRES_PASSWORD=apiforge \
  postgres:16-alpine

# 2. Run the API (auto-migrates in Development)
dotnet run --project ApiForge.Api
# Listens on the Kestrel default (e.g. http://localhost:5xxx — see console output)
```

### Tests

```bash
# Unit tests only — no Docker needed
dotnet test --filter "FullyQualifiedName~Unit"

# Integration tests — require a running Docker daemon (spins up postgres:16-alpine)
dotnet test --filter "FullyQualifiedName~Integration"

# Everything
dotnet test
```

Unit tests cover the key generator and the usage queue. Integration tests cover register/login,
refresh-token **rotation + revocation**, JWT-gated key management, and the full API-key flow on
`/items` including **scope enforcement** and **revocation**.

### Database migrations

```bash
export PATH="$PATH:$HOME/.dotnet/tools"          # if dotnet-ef isn't on PATH
dotnet ef migrations add <Name> --project ApiForge.Api --output-dir Data/Migrations
```

---

## API walkthrough (curl)

Assuming the API is at `http://localhost:8080`:

```bash
# 1. Register
curl -s -X POST localhost:8080/auth/register \
  -H 'Content-Type: application/json' \
  -d '{"email":"dev@example.com","password":"password123"}'

# 2. Login → grab accessToken + refreshToken
curl -s -X POST localhost:8080/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"dev@example.com","password":"password123"}'
TOKEN=<accessToken from above>

# 3. Create an API key (JWT-protected) → the plaintext "key" is shown ONCE
curl -s -X POST localhost:8080/keys \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"name":"My key","scopes":["read","write"],"rateLimit":100}'
APIKEY=<key from above>   # e.g. apf_live_xxxxxxxx

# 4. Use the API key against the protected demo resource
curl -s -X POST localhost:8080/items \
  -H "X-API-Key: $APIKEY" -H 'Content-Type: application/json' \
  -d '{"name":"Widget"}'
curl -s localhost:8080/items -H "X-API-Key: $APIKEY"

# 5. View usage for a key (JWT-protected). Usage flushes every ~5s.
KEYID=<the key id returned in step 3>
curl -s "localhost:8080/keys/$KEYID/usage" -H "Authorization: Bearer $TOKEN"

# 6. Rotate the refresh token (old one is revoked)
curl -s -X POST localhost:8080/auth/refresh \
  -H 'Content-Type: application/json' \
  -d '{"refreshToken":"<refreshToken>"}'
```

---

## Deploy to AWS

This is a **learning/portfolio** deployment: cheap, destroyable, single small RDS, no NAT Gateway.

### 0. Install the tools (if missing)

```bash
# AWS CLI v2
curl "https://awscli.amazonaws.com/awscli-exe-linux-x86_64.zip" -o awscliv2.zip
unzip awscliv2.zip && sudo ./aws/install
aws --version
```

The CDK CLI is run via `npx cdk` (no global install needed). Docker must be running (the image is
built locally and published by CDK).

### 1. Configure least-privilege AWS access

Follow `apiforge-blueprint-revised.md` §9. In short:

- **Don't use root.** Create an admin/SSO identity, MFA it.
- Create a **deployer** identity whose only job is to assume the CDK bootstrap roles. Configure it as
  a named CLI profile:
  ```bash
  aws configure --profile apiforge-deploy   # access key, secret, region
  export AWS_PROFILE=apiforge-deploy
  export CDK_DEFAULT_ACCOUNT=$(aws sts get-caller-identity --query Account --output text)
  export CDK_DEFAULT_REGION=$(aws configure get region)
  ```
- The **runtime task role** is created by the stack and already scoped to read only the two secret
  ARNs (`dbSecret.GrantRead` + `jwtSecret.GrantRead`) — no wildcards.

### 2. Bootstrap CDK (once per account/region)

```bash
cd ApiForge.Cdk
npx cdk bootstrap "aws://$CDK_DEFAULT_ACCOUNT/$CDK_DEFAULT_REGION" \
  --custom-permissions-boundary apiforge-deploy-boundary   # optional but recommended (§9)
```

### 3. Deploy

```bash
# from ApiForge.Cdk/
npx cdk deploy --context env=dev \
  --context budgetEmail="you@example.com"     # optional: enables the $10/mo budget alarm
```

CDK builds the Docker image from the repo `Dockerfile`, publishes it, and provisions the VPC, RDS,
ECS service and public ALB in one go. The circuit breaker rolls a bad deploy back in minutes. When
it finishes, it prints `ApiUrl` (the ALB DNS name).

### 4. Run the database migration (one-off task)

RDS starts empty. Run migrations as a one-off task using the same image (`RUN_MIGRATIONS=true` makes
the app migrate and exit):

```bash
CLUSTER=$(aws ecs list-clusters --query 'clusterArns[0]' --output text)
TASKDEF=$(aws ecs list-task-definitions --query 'taskDefinitionArns[-1]' --output text)
# Use the SAME private subnets + security group the service uses (find them in the console or via
# `aws ecs describe-services`). Then:
aws ecs run-task --cluster "$CLUSTER" --task-definition "$TASKDEF" --launch-type FARGATE \
  --network-configuration "awsvpcConfiguration={subnets=[subnet-AAA,subnet-BBB],securityGroups=[sg-XXX],assignPublicIp=DISABLED}" \
  --overrides '{"containerOverrides":[{"name":"web","environment":[{"name":"RUN_MIGRATIONS","value":"true"}]}]}'
```

> Tip: the container name in the generated task definition is `web`. Confirm with
> `aws ecs describe-task-definition --task-definition "$TASKDEF" --query 'taskDefinition.containerDefinitions[].name'`.

### 5. Smoke test

```bash
API=<ApiUrl from cdk output>
curl -s "http://$API/health"
# then run the curl walkthrough above against http://$API
```

---

## Tear down

```bash
cd ApiForge.Cdk
npx cdk destroy --context env=dev
```

In `dev` the RDS instance has `RemovalPolicy.DESTROY` and no deletion protection, so it is removed
too. Afterwards, confirm in the console that the RDS instance and any leftover ENIs are gone. Treat
**deploy → test → destroy** as the normal cycle to keep costs near zero.

> Cost note: there is **no NAT Gateway** (the biggest idle cost) — isolated subnets reach ECR /
> Secrets Manager / CloudWatch via VPC endpoints. The main idle costs are the small RDS instance and
> the ALB, both removed by `cdk destroy`.

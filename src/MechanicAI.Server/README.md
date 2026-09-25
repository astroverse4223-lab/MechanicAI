# Mechanic AI shop server

`MechanicAI.Server` is the **optional** multi-workstation server. Workstations stay offline-first on
their local SQLite database; a shop that wants to share customers, vehicles, diagnostic sessions,
estimates/inspections, and its knowledge base across workstations runs this server on PostgreSQL
(with pgvector for semantic document search).

The server is a thin HTTP layer: every endpoint calls the same application services the desktop
uses (`VehicleService`, `DiagnosticSessionService`, `ShopService`, `KnowledgeBaseService`, ...).
Business rules live in `MechanicAI.Application`, not here.

## Quick start (Docker)

```bash
cd src/MechanicAI.Server
cp .env.example .env            # fill in POSTGRES_PASSWORD, JWT_SIGNING_KEY, BOOTSTRAP_ADMIN_*
docker compose up -d --build    # server on http://localhost:8080, PostgreSQL + pgvector
docker compose --profile redis up -d --build   # optional Redis (set REDIS_CONNECTION=redis:6379)
curl http://localhost:8080/health/ready
```

After the first start, remove `BOOTSTRAP_ADMIN_PASSWORD` from `.env` (see [First administrator](#first-administrator)).
Put the server behind a TLS-terminating reverse proxy (Caddy, nginx, Traefik) and enable
`ForwardedHeaders` for that proxy, or configure Kestrel certificates directly.

## Running locally

```bash
dotnet user-secrets --project src/MechanicAI.Server set "ConnectionStrings:MechanicAI" "Host=localhost;Database=mechanicai;Username=mechanicai;Password=<pw>"
dotnet user-secrets --project src/MechanicAI.Server set "Jwt:SigningKey" "$(openssl rand -base64 48)"
dotnet run --project src/MechanicAI.Server --launch-profile http
```

In Development the OpenAPI document is served at `/openapi/v1.json` and the Scalar API reference at
`/scalar`. If `Jwt:SigningKey` is missing in Development an ephemeral key is generated (tokens do
not survive restarts); in every other environment the server refuses to start.

To try the server without PostgreSQL, set `Database:Provider=Sqlite` (optionally with a
`ConnectionStrings:MechanicAI` like `Data Source=/path/server.db`). SQLite is meant for evaluation
and the integration tests, not for a shop with several workstations.

## Configuration

Settings come from `appsettings.json`, `appsettings.{Environment}.json`, user-secrets
(Development), environment variables (`Section__Key`, e.g. `Jwt__SigningKey`), and command-line
arguments, in that order of precedence (last wins). **`appsettings.json` contains no secrets**; the
values marked *secret* below must come from environment variables, user-secrets, or your secret manager.

| Key | Default | Notes |
| --- | --- | --- |
| `ConnectionStrings:MechanicAI` | — | *Secret.* PostgreSQL connection string (required for `Postgres`). |
| `ConnectionStrings:Redis` | empty | Optional. When set, Redis backs the distributed cache (signed-out access tokens are shared across server instances). Otherwise an in-memory cache is used. |
| `Database:Provider` | `Postgres` | `Postgres` or `Sqlite`. |
| `Database:ApplyMigrationsOnStartup` | `true` | Apply EF Core migrations and seed reference content (DTCs, training) at startup. |
| `Storage:DataRoot` | `%LOCALAPPDATA%/MechanicAI.Server` | Uploaded documents. `/data` in the container (mount a volume). |
| `Jwt:SigningKey` | — | *Secret.* HMAC-SHA256 key, **at least 32 bytes**. Generate with `openssl rand -base64 48`. |
| `Jwt:Issuer` / `Jwt:Audience` | `MechanicAI.Server` / `MechanicAI.Workstation` | Token issuer and audience. |
| `Jwt:AccessTokenMinutes` | `15` | Access token lifetime (1–1440). |
| `Jwt:RefreshTokenDays` | `14` | Refresh token lifetime (1–365). |
| `Jwt:ClockSkewSeconds` | `30` | Allowed clock difference. |
| `Auth:MaxFailedAttempts` | `5` | Failed sign-ins before a lockout. |
| `Auth:LockoutMinutes` | `15` | Lockout duration. |
| `Auth:MinPasswordLength` | `12` | Minimum password length (letters plus digits/symbols are also required). |
| `Bootstrap:AdminEmail` / `Bootstrap:AdminPassword` | empty | *Secret (password).* Creates the first Owner when no Owner/Admin exists. |
| `Bootstrap:AdminDisplayName` | `Shop owner` | |
| `RateLimiting:Auth:PermitLimit` / `WindowSeconds` | `10` / `60` | Per client IP, on `/api/auth/login`, `refresh`, `logout`, `change-password`. |
| `RateLimiting:Api:PermitLimit` / `WindowSeconds` | `600` / `60` | Per user (or IP), everything else. Health checks are never limited. |
| `Uploads:MaxDocumentBytes` | `104857600` (100 MB) | Knowledge-base upload limit (capped at the application's 300 MB). |
| `Kestrel:Limits:MaxRequestBodySize` | `10485760` (10 MB) | Limit for every other request; the upload endpoint raises it for itself. |
| `Cors:AllowedOrigins` | `[]` | CORS is **off** unless origins are listed. Workstations are not browsers and do not need it. |
| `ForwardedHeaders:Enabled` | `false` | Honor `X-Forwarded-For/Proto/Host`. |
| `ForwardedHeaders:KnownProxies` / `KnownNetworks` | `[]` | Proxy IPs / CIDR ranges to trust (loopback is trusted by default). |
| `Https:Redirect` / `Https:Hsts` | `false` / `false` | Enable when Kestrel terminates TLS itself. |
| `App:*` | see `appsettings.json` | Application settings (`AppSettings` shape): `App:Ai:Mode` (`Disabled` by default on the server; `Local` uses Ollama at `App:Ai:OllamaBaseUrl`), `App:Search:Provider` (`None`, `Brave`, `Tavily`, `SearXng`), `App:VehicleData:*`, `App:Privacy:*`, `App:Diagnostics:ShopName`. |
| `Secrets:AnthropicApiKey`, `Secrets:OpenAiApiKey`, `Secrets:BraveApiKey`, `Secrets:TavilyApiKey`, `Secrets:SearxngApiKey` | empty | *Secret.* API keys for optional server-side AI and web search. |
| `Serilog:*` | console | Standard Serilog configuration. |

Example (environment variables):

```bash
export ConnectionStrings__MechanicAI="Host=db;Database=mechanicai;Username=mechanicai;Password=..."
export Jwt__SigningKey="$(openssl rand -base64 48)"
export App__Search__Provider=Brave Secrets__BraveApiKey=...
```

## First administrator

There is **no default account and no default password**. Create the first Owner in one of two ways:

1. **Configuration (containers):** set `Bootstrap__AdminEmail` and `Bootstrap__AdminPassword` for the
   first start. The account is created only when no Owner/Admin exists; afterwards the settings are
   ignored and a warning reminds you to remove the password from the environment.
2. **One-time CLI command:**
   ```bash
   MECHANICAI_ADMIN_PASSWORD='...' dotnet MechanicAI.Server.dll create-admin --email owner@shop.example --name "Pat Owner"
   # or omit the variable to be prompted (input hidden)
   docker compose run --rm server create-admin --email owner@shop.example
   ```
   The command refuses to run once an Owner/Admin exists, and never accepts the password as an argument.

Then sign in and create accounts for everyone else with `POST /api/users`.

## Database migrations

Migrations live in `src/MechanicAI.Infrastructure/Persistence/Migrations/{Postgres,Sqlite}` and are
applied at startup when `Database:ApplyMigrationsOnStartup` is `true` (default). The startup step also
creates the PostgreSQL full-text GIN indexes used by keyword search (`IX_DocumentChunks_Fts`,
`IX_DTCs_Fts`). To manage migrations yourself set it to `false` and use the `dotnet-ef` tool from the
repository's tool manifest:

```bash
dotnet tool restore
# add a migration for each provider after changing the model
dotnet ef migrations add <Name> --project src/MechanicAI.Infrastructure --context PostgresAppDbContext --output-dir Persistence/Migrations/Postgres
dotnet ef migrations add <Name> --project src/MechanicAI.Infrastructure --context SqliteAppDbContext --output-dir Persistence/Migrations/Sqlite
# apply to a server database (the design-time factory reads MECHANICAI_DESIGN_PG)
MECHANICAI_DESIGN_PG="Host=...;Database=mechanicai;Username=...;Password=..." \
  dotnet ef database update --project src/MechanicAI.Infrastructure --context PostgresAppDbContext
# or produce an idempotent script for a DBA
dotnet ef migrations script --idempotent --project src/MechanicAI.Infrastructure --context PostgresAppDbContext -o migrate.sql
```

The PostgreSQL role needs permission to `CREATE EXTENSION vector` on first migration (the
`pgvector/pgvector` image's default user has it).

## Authentication

* `POST /api/auth/login` with `{ "email", "password" }` returns `accessToken` (JWT, 15 min) and
  `refreshToken` (opaque, 14 days). Send `Authorization: Bearer <accessToken>`.
* Passwords are PBKDF2-SHA256 hashes (600,000 iterations); older hashes are upgraded on sign-in.
  Repeated failures lock the account (`429`). Unknown accounts take the same time as wrong passwords.
* `POST /api/auth/refresh` rotates the refresh token: each one works **once**. Presenting an already
  rotated token (replay or theft) revokes every token descended from it, and that workstation must sign in again.
  Only SHA-256 hashes of refresh tokens are stored.
* `POST /api/auth/logout` revokes the refresh token and deny-lists the current access token until it expires.
* Changing or resetting a password, changing a role, or deactivating a user rotates the account's
  security stamp and revokes all its refresh tokens; its outstanding access tokens stop working
  within 30 seconds (per-instance cache).

### Roles

| Policy | Roles | Grants |
| --- | --- | --- |
| *(any signed-in user)* | all | Reading vehicles, customers, sessions, DTCs, documents, training, notes |
| `Diagnostics` | Owner, Admin, Technician, Apprentice | Starting/updating diagnostic sessions, training attempts, web research |
| `Records` | Owner, Admin, Technician, ServiceAdvisor | Creating/updating customers, vehicles, repairs, estimates, inspections |
| `Knowledge` | Owner, Admin, Technician | Uploading/editing/deleting documents, technician DTC definitions |
| `Admin` | Owner, Admin | Users, deleting vehicles/customers/sessions |

Every endpoint requires authentication unless listed as anonymous below (fallback policy).
Every request runs with the caller's identity in `AuditLogger.Ambient`, so audit entries record
the user id, name, and client IP.

## Endpoints

Anonymous: `GET /health/live`, `GET /health/ready` (database check), `POST /api/auth/login`,
`POST /api/auth/refresh`, `POST /api/auth/logout`.

| Area | Endpoints |
| --- | --- |
| Auth | `GET /api/auth/me`, `POST /api/auth/change-password` |
| Users (Admin) | `GET/POST /api/users`, `PUT /api/users/{id}`, `POST /api/users/{id}/reset-password` |
| Vehicles | `GET /api/vehicles?search=&take=`, `GET/PUT/DELETE /api/vehicles/{id}`, `POST /api/vehicles`, `PUT /api/vehicles/{id}/favorite`, `GET /api/vehicles/vin/{vin}?modelYear=`, `POST /api/vehicles/from-vin`, `POST /api/vehicles/{id}/redecode`, `GET /api/vehicles/{id}/recalls`, `POST /api/vehicles/{id}/recalls/refresh`, `PUT /api/recalls/{id}/status`, `GET /api/vehicles/{id}/complaints` |
| History | `GET /api/vehicles/{id}/history?search=`, `GET /api/history/diagnoses?q=` |
| Customers | `GET /api/customers?search=`, `GET/PUT/DELETE /api/customers/{id}`, `POST /api/customers` |
| Diagnostic sessions | `GET /api/sessions?vehicleId=&includeClosed=&take=`, `POST /api/sessions`, `POST /api/sessions/from-text`, `GET /api/sessions/{id}` (tree, ranking, steps), `GET /api/sessions/{id}/report` (Markdown), `POST/DELETE /api/sessions/{id}/tests/{testId}/result`, `POST /api/sessions/{id}/tests/{testId}/skip`, `POST /api/sessions/{id}/back`, `POST /api/sessions/{id}/observations`, `POST /api/sessions/{id}/dtcs`, `DELETE /api/sessions/{id}/dtcs/{code}`, `PUT /api/sessions/{id}/causes/{nodeId}/status`, `POST /api/sessions/{id}/confirm`, `/repair`, `/verification`, `/reopen`, `/abandon`, `DELETE /api/sessions/{id}` |
| DTC reference | `GET /api/dtcs?q=&take=`, `GET /api/dtcs/{code}?make=`, `POST /api/dtcs/{code}/definitions` |
| Knowledge base | `GET /api/knowledge/documents?kind=&vehicleId=&search=`, `POST /api/knowledge/documents` (multipart), `GET/PUT/DELETE /api/knowledge/documents/{id}`, `GET /api/knowledge/documents/{id}/file`, `POST /api/knowledge/documents/{id}/reindex`, `GET /api/knowledge/search?q=&limit=&kind=&documentId=&year=&make=&model=`, `POST /api/knowledge/ask`, `GET /api/knowledge/stats` |
| Shop | `GET/POST /api/repairs`, `GET/POST /api/notes`, `PUT/DELETE /api/notes/{id}`, `GET/POST /api/estimates`, `GET/PUT/DELETE /api/estimates/{id}`, `GET/POST /api/inspections`, `PUT /api/inspections/items/{itemId}`, `POST /api/inspections/{id}/complete` |
| Training | `GET /api/training/courses`, `GET /api/training/courses/{id}` (quizzes without answer keys), `GET /api/training/lessons/{id}`, `POST /api/training/lessons/{id}/complete`, `POST /api/training/quizzes/{id}/submit`, `GET /api/training/flashcards/due`, `POST /api/training/flashcards/{id}/review`, `GET /api/training/attempts` |
| Research | `GET /api/research/search?q=&vehicle=&maxResults=` (needs `App:Search:Provider` and its key) |

Diagnostic-session mutations return the updated session, so a workstation can re-render without a
second request. The technician recorded on steps, repairs, notes, and quiz attempts is always the
signed-in user, never a client-supplied name.

### Uploads

`POST /api/knowledge/documents` takes `multipart/form-data` with a `file` field plus optional
`title`, `kind` (`ServiceManual`, `WiringDiagram`, ...), `vehicleId`, `make`, `model`, `yearFrom`,
`yearTo`, `tags` (comma-separated), and `description`. PDF, text/Markdown/CSV, and images are
accepted; content is checked by file signature, not only by extension. Larger files than
`Uploads:MaxDocumentBytes` get `413`. The response is `202 Accepted`; indexing (text extraction,
chunking, keyword index, embeddings when an embedding model is configured) runs in the background,
so poll `GET /api/knowledge/documents/{id}` until `status` is `Ready` or `ReadyKeywordOnly`.
Images need OCR, which the server does not provide; they are stored with status `NeedsOcr`.

### Errors

Errors are RFC 9457 problem details (`application/problem+json`) with an `errorKind` extension.
Application error kinds map to: `Validation` 400, `Unauthorized` 401, `NotFound` 404, `Conflict` 409,
`RateLimited` 429, `InvalidResponse` 502, `NotConfigured`/`Unavailable`/`Offline` 503, `Timeout` 504,
`Cancelled` 499, `Unexpected` 500. Unexpected errors return a generic message; details go to the log only.

## What is not exposed

* The AI assistant chat and the AI tool-calling agent: they depend on workstation-only features
  (OBD-II live data, local image analysis) and stream long responses; they stay on the desktop.
  `POST /api/knowledge/ask` is available and answers from the shop's documents when `App:Ai:Mode`
  is configured (otherwise it returns the most relevant passages).
* Training progress (lesson completion, flashcard boxes) is shared shop-wide on the server, as it is
  per-workstation on the desktop; quiz attempts record the signed-in user as the trainee.

## Tests

```bash
dotnet test tests/MechanicAI.Server.Tests
```

The integration tests host the real pipeline with `WebApplicationFactory`, using
`Database:Provider=Sqlite` on a temporary file and a bootstrapped owner account.

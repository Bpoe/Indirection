# Indirection

A small, self-hosted URL alias service: .NET 10, ASP.NET Core controllers, EF Core SQLite, and local HybridCache. Clients choose case-insensitive codes, including nested paths. The authoritative architecture and requirements are in [docs/design.md](docs/design.md).

Requires the .NET 10 SDK. Commands below run from the repository root; shell examples use Bash (and OpenSSL to generate a key).

```bash
dotnet restore Indirection.sln
dotnet tool restore
dotnet build Indirection.sln -c Release --no-restore
```

To create a local database and run:

```bash
export Authentication__ApiKey="$(openssl rand -base64 32)"
export Database__Provider=Sqlite
export ConnectionStrings__Urls="Data Source=$PWD/urls.db"
dotnet ef database update --project src/prod/Indirection --configuration Release --no-build
dotnet run --project src/prod/Indirection -c Release --no-build --no-launch-profile -- --urls http://127.0.0.1:5080
```

Keep the generated key in the shell for the examples below, or supply a secret through your deployment's secret store. No key is included in application configuration. Startup rejects a missing/blank key or an unsupported provider. Use at least 32 cryptographically random bytes for production keys. Serve deployed management endpoints over HTTPS, normally through your hosting platform/reverse proxy; the HTTP binding above is for local use.

SQLite is the only supported provider. `appsettings.json` defaults to `Data Source=urls.db`; relative SQLite paths resolve against the process working directory. An absolute `ConnectionStrings__Urls` avoids ambiguity between the EF CLI and host. Put the database and its sidecar files on persistent, writable storage and back them up. Apply migrations explicitly before starting/upgrading; the application does not migrate at startup. `dotnet ef migrations has-pending-model-changes --project src/prod/Indirection` checks model drift.

The production application is one project under `src/prod/Indirection`. Controllers access EF Core and HybridCache directly. Caches are lazy, local and disposable, use HybridCache's default expiration settings, and never retain missing lookups. Successful writes save first and then invalidate the normalized code. Operate one application instance; out-of-band database writes do not invalidate its cache. `/health` is an anonymous liveness check returning `200` with `Healthy`; it does not probe database readiness.

| Request | Result |
| --- | --- |
| `PUT /api/v1/links/{code}` | Create: `201` with mapping and management `Location`; replace: `204` |
| `GET /api/v1/links/{code}` | Mapping as JSON, or `404` |
| `GET /api/v1/links` | JSON array of all mappings (no ordering guarantee) |
| `DELETE /api/v1/links/{code}` | Existing: `204`; missing: `404` |
| `GET /{code}` | Anonymous `302` with destination in `Location`, or `404` |

All management requests require `X-Api-Key`. Missing/incorrect keys return `401`. There is no POST. Codes are normalized with invariant lowercase and slashes are preserved. Explicit management and health routes take precedence over redirects. Empty public paths return `404`. PUT requires a JSON `destination` string; ordinary ASP.NET Core required-field validation rejects missing, null, empty, or whitespace-only values with `400`. There is no URL/scheme validation: relative destinations and non-HTTP schemes are accepted. HTTP header serialization still imposes the framework's usual restrictions. `CreatedUtc` records initial creation, remains unchanged on replacement, and is serialized in UTC. Repeated identical PUTs are idempotent; repeated DELETE leaves the resource absent even though its second response is `404`.

In another terminal, set `Authentication__ApiKey` to the same key, then:

```bash
curl -i -X PUT http://127.0.0.1:5080/api/v1/links/Docs/Getting-Started \
  -H "X-Api-Key: $Authentication__ApiKey" -H 'Content-Type: application/json' \
  -d '{"destination":"https://example.com/guide"}'
curl -i http://127.0.0.1:5080/api/v1/links/docs/getting-started -H "X-Api-Key: $Authentication__ApiKey"
curl -i http://127.0.0.1:5080/DOCS/GETTING-STARTED
curl -i -X DELETE http://127.0.0.1:5080/api/v1/links/docs/getting-started -H "X-Api-Key: $Authentication__ApiKey"
```

Local tests use MSTest on Microsoft.Testing.Platform, WebApplicationFactory, isolated real SQLite files created from migrations, and the real HybridCache. They require no external service or key:

```bash
dotnet test --project src/test/Indirection.Tests -c Release
dotnet test --project src/test/Indirection.Tests -c Release --no-build \
  --coverlet --coverlet-threshold 96 --coverlet-threshold-type line,branch \
  --results-directory "$PWD/artifacts/coverage"
```

Coverage includes all handwritten production code, including startup and authentication; only generated EF migration files are excluded. The coverage command and CI enforce at least 96% for both lines and branches. JSON and Cobertura reports are written under `artifacts/coverage`. The separate deployment suite does not contribute to coverage. The explicit SQLite native bundle reference upgrades EF's legacy transitive bundle without suppressing vulnerability warnings.

Deployment verification builds independently and uses only HTTP, with automatic redirects disabled. With the local application still running (or an explicit HTTPS deployment target):

```bash
dotnet build src/test/Indirection.DeploymentTests -c Release
export INDIRECTION_BASE_URL=http://127.0.0.1:5080
export INDIRECTION_API_KEY="$Authentication__ApiKey"
dotnet test --project src/test/Indirection.DeploymentTests -c Release --no-build
```

The suite checks health and the complete create/read/redirect/update/redirect/delete/404 lifecycle, including mixed casing, nested codes, and warmed-cache updates. Each run uses a unique `synthetic/{guid}/nested` code and cleans up in `finally`, so concurrent runs are isolated. Run this same command after deployment or on a scheduler for synthetics. When neither environment variable is supplied, deployment tests are skipped; supplying only one fails configuration. CI runs only the local suite and never targets a live deployment implicitly.

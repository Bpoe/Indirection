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

### File-based secrets

Indirection optionally loads configuration from `/run/secrets` using ASP.NET Core's Key-Per-File provider. Each file's contents supply its value; `__` in the filename maps to `:` for any configuration key. For example, mount a file containing your API key at `/run/secrets/Authentication__ApiKey` to set `Authentication:ApiKey`. No directory or configuration changes are required when running without mounted secrets.

Files take precedence over all standard sources, including command-line arguments. Otherwise, standard precedence is preserved: command-line arguments, environment variables, `appsettings.{Environment}.json`, then `appsettings.json` (highest to lowest). Restart the application after rotating secret files.

### Application behavior

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

GitHub Actions runs on pull requests targeting `main` and pushes to `main`. It installs .NET 10, caches NuGet packages using the project/configuration files as the cache key, restores, builds the complete solution in Release, and runs only `Indirection.Tests` with the existing coverage configuration. Both line and branch coverage must reach 96%, exceeding the design's >95% requirement; coverage reports are retained as workflow artifacts.

Pushing a version tag matching `v*` runs the same validation before publishing a multi-platform image to GHCR and then a GitHub Release. Release creation requires successful tests and Docker publishing. After these workflow changes are committed and pushed, create the first release from the intended commit:

```bash
git switch main
git pull --ff-only origin main
git tag v0.1.0
git push origin v0.1.0
```

The release attaches `indirection-v0.1.0.zip` (named for the tag). It contains the published application, default configuration, and dependencies, including native SQLite assets for multiple platforms; it contains no database or credentials. Publishing uses GitHub's built-in token: validation has `contents: read`, the Docker job alone has `packages: write`, and only the release-creation job has `contents: write`. No personal access token is needed.

The archive is runtime-neutral and framework-dependent. It requires the **ASP.NET Core Runtime 10.0** (including the .NET runtime), not the SDK. One archive serves platforms supported by both .NET 10 and the packaged SQLite libraries, including Windows, Linux, and macOS on x64/Arm64. Keep the `runtimes` directory intact. Extract into an application directory and run from there; the database directory must already exist on persistent storage and be writable by the application account. This example uses Bash; on Windows, set the same environment variables with PowerShell and use an absolute Windows database path:

```bash
mkdir -p indirection
unzip indirection-v0.1.0.zip -d indirection
cd indirection
export DOTNET_ENVIRONMENT=Production
export Authentication__ApiKey="$(openssl rand -base64 32)" # Or supply your existing deployment secret.
export Database__Provider=Sqlite
export ConnectionStrings__Urls='Data Source=/var/lib/indirection/urls.db'
dotnet Indirection.dll --migrate
dotnet Indirection.dll --urls http://127.0.0.1:5080
```

The `--migrate` command initializes or upgrades the database without a source checkout, SDK, or platform-specific migration executable. It uses the same configuration as the server, applies only pending migrations, and exits without starting HTTP; failures return a nonzero exit code. Migration mode does not require an API key. Back up the database and stop the application before upgrading, run the new release's migration command, then restart. Keep state outside the extracted release directory and expose deployed management requests through HTTPS as described above.

Docker images support Linux amd64 and arm64 and use the .NET 10 Azure Linux distroless ASP.NET Core runtime. CI packages the same published binaries from the release ZIP into both images without restoring, compiling, or publishing inside Docker. Pull the published image or publish once locally and package it from the repository root:

```bash
docker pull ghcr.io/bpoe/indirection:latest
dotnet publish src/prod/Indirection -c Release --self-contained false \
  -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false -o artifacts/portable
docker build -t indirection:local .
# Optional multi-platform build (requires a Buildx builder):
docker buildx build --platform linux/amd64,linux/arm64 -t indirection:local --output type=oci,dest=indirection.oci.tar .
```

A stable `v0.1.0` tag publishes `ghcr.io/bpoe/indirection` tags `0.1.0`, `0.1`, `0`, and `latest`. Prereleases publish their full version (for example `0.2.0-rc.1`) without updating stable aliases or `latest`. Pin a full version for controlled upgrades. Publishing uses Docker's [metadata-action](https://github.com/docker/metadata-action) for version tags and [Buildx](https://github.com/docker/build-push-action) for multi-platform builds and caching.

The included `compose.yaml` exposes HTTP on port 8080 and keeps SQLite in a named volume mounted at `/data`. Supply an API key at runtime, apply migrations explicitly, then start:

```bash
export Authentication__ApiKey="$(openssl rand -base64 32)" # Or your existing deployment secret.
docker compose pull
docker compose run --rm --no-deps indirection --migrate
docker compose up -d
curl --fail http://localhost:8080/health
```

Keep the key in your deployment's secret store and supply it again when recreating the container. Migrations do not require a key. Containers run as the runtime image's built-in non-root user, with `/app` as the working directory and a writable `/data`. The runtime contains no shell or package manager. Startup never applies migrations automatically.

The container includes `docker/appsettings.json` as `/app/appsettings.json`, defaulting to SQLite with `Data Source=/data/urls.db`. The portable ZIP retains its original `Data Source=urls.db` default. To override the complete container configuration, copy `docker/appsettings.json` to a local `appsettings.json`, edit it, and uncomment the read-only file mount in `compose.yaml`. For a direct Docker run, use `--mount type=bind,src="$PWD/appsettings.json",dst=/app/appsettings.json,readonly` on both migration and server containers. Standard ASP.NET Core configuration precedence applies, including environment variables and command-line arguments; neither the image nor Compose sets database-provider or connection-string environment variables. A configured alternative database path is used as supplied; mount writable persistent storage at that location, accessible to UID 1654.

Without Compose, use the same named volume for migrations and serving:

```bash
docker volume create indirection-data
docker run --rm -v indirection-data:/data ghcr.io/bpoe/indirection:latest --migrate
docker run -d --name indirection --restart unless-stopped -p 8080:8080 \
  -e Authentication__ApiKey -v indirection-data:/data ghcr.io/bpoe/indirection:latest
```

Mount the entire data directory so SQLite journal, WAL, and shared-memory files persist with the database. A fresh named volume inherits the image directory's non-root ownership; provision bind mounts or existing volumes with equivalent write permissions. Back up SQLite consistently while the service is stopped, including its sidecar files. `docker compose down` retains the data volume; `docker compose down -v` deletes it.

To upgrade, stop the service, back up the database, pull the new image, explicitly migrate using that image, and recreate the container while retaining the volume and API key:

```bash
docker compose stop
# Back up the data volume before continuing.
docker compose pull
docker compose run --rm --no-deps indirection --migrate
docker compose up -d
```

If using a pinned image tag, update it in `compose.yaml` before pulling. Check `/health` and the deployment verification suite after upgrading. Route deployed HTTP through HTTPS as described above.

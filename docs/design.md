# Indirection Design
## Overview
Indirection is a small, self-hostable URL-shortening and redirect service.
The design intentionally favors simplicity and YAGNI. The initial deployment uses SQLite and local in-process caching, while keeping the data model and application code straightforward enough to move to PostgreSQL, SQL Server, or distributed caching later if those needs actually arise.
This document records the intended architecture and the decisions behind it. It is the authoritative design reference for the initial implementation.
## Goals
Indirection should:
- provide durable, client-defined URL aliases;
- resolve aliases quickly;
- be simple to deploy and operate;
- have a small, predictable REST management surface;
- use standard ASP.NET Core facilities rather than custom infrastructure where practical;
- maintain strong automated test coverage;
- provide black-box tests that can also be used for deployment verification and synthetic monitoring; and
- keep future database and cache changes possible without implementing them prematurely.
## Non-goals
The initial implementation does not need:
- a service layer;
- repository abstractions around Entity Framework Core;
- a cache abstraction around `HybridCache`;
- Clean Architecture or Onion Architecture project separation;
- CQRS or mediator infrastructure;
- Redis;
- multiple application instances;
- JWT authentication;
- users, roles, login, refresh tokens, or ASP.NET Identity;
- server-generated short codes;
- negative caching;
- destination URL validation;
- click analytics;
- tags, campaigns, ownership metadata, or other speculative link metadata; or
- simultaneous support for multiple database providers.
These can be introduced later if concrete requirements justify them.
## Technology
The initial implementation uses:
- .NET 10;
- ASP.NET Core controllers;
- Entity Framework Core;
- SQLite;
- `HybridCache`;
- ASP.NET Core authentication and authorization;
- a custom API-key authentication scheme built on the standard ASP.NET Core authentication handler APIs; and
- Microsoft Testing Platform-compatible test tooling.
## Repository layout
Production code lives under `/src/prod`, tests under `/src/test`, and design documentation under `/docs`.
```text
/
├── Indirection.sln
├── README.md
├── Directory.Build.props
├── .editorconfig
│
├── docs/
│   └── design.md
│
└── src/
    ├── prod/
    │   └── Indirection/
    │       ├── Authentication/
    │       │   ├── ApiKeyAuthenticationHandler.cs
    │       │   ├── ApiKeyAuthenticationOptions.cs
    │       │   └── ApiKeyDefaults.cs
    │       ├── Controllers/
    │       │   ├── LinksController.cs
    │       │   └── RedirectController.cs
    │       ├── Data/
    │       │   ├── IndirectionDbContext.cs
    │       │   └── Migrations/
    │       ├── Models/
    │       │   └── ShortUrl.cs
    │       ├── Program.cs
    │       ├── Startup.cs
    │       ├── appsettings.json
    │       └── Indirection.csproj
    │
    └── test/
        ├── Indirection.Tests/
        │   └── Indirection.Tests.csproj
        │
        └── Indirection.DeploymentTests/
            └── Indirection.DeploymentTests.csproj
```
The exact test-file organization may evolve as the implementation is written, but the production application remains a single `.csproj`.
## Application architecture
Indirection intentionally does not use a separate domain, application, service, repository, or infrastructure layer.
Controllers use `IndirectionDbContext` and `HybridCache` directly.
```text
HTTP
 │
 ▼
Controllers
 ├────────► HybridCache
 │
 └────────► IndirectionDbContext
                    │
                    ▼
                  SQLite
```
Entity Framework Core already supplies the persistence abstraction needed by this service. Wrapping `DbContext` in another repository layer would add indirection without providing meaningful value at the current scope.
Likewise, `HybridCache` is used directly rather than wrapped in an application-specific cache abstraction.
If the application later develops substantial business behavior or multiple persistence implementations that must coexist, additional abstractions can be introduced at that time.
## Application startup
Dependency registration is centralized in a static:
```csharp
Startup.ConfigureServices(
    IServiceCollection services,
    IConfiguration configuration)
```
`Program.cs` should remain focused on host construction and the HTTP pipeline.
Its shape should remain approximately:
```csharp
var builder = WebApplication.CreateBuilder(args);
Startup.ConfigureServices(
    builder.Services,
    builder.Configuration);
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();
```
Do not recreate the older `Startup.Configure()` hosting model. Only dependency registration is moved into `Startup.ConfigureServices`.
For portable releases, `dotnet Indirection.dll --migrate` is an explicit maintenance mode in the same application project. It applies the existing EF Core migrations using the configured SQLite connection and exits without starting HTTP. Repeated execution applies only pending migrations; failures produce a nonzero exit code. This command does not require the API key, because it does not start the web host. Normal application startup does not run migrations.
## Data model
The initial model should contain only fields needed by the current API.
At minimum:
```csharp
public sealed class ShortUrl
{
    public required string Code { get; set; }
    public required string Destination { get; set; }
    public DateTime CreatedUtc { get; set; }
}
```
`Code` is the database primary key.
There is no separate numeric or GUID identifier.
Additional fields should not be introduced speculatively. If a future requirement needs expiration, enabled/disabled state, analytics, tags, ownership, or similar metadata, those can be added deliberately.
## Short-code semantics
Short codes are generated by clients.
They are:
- case-insensitive;
- allowed to contain slash characters; and
- immutable as resource identifiers.
The application normalizes codes to invariant lowercase before persistence, lookup, deletion, cache access, and cache invalidation.
For example:
```text
Foo/Bar
foo/bar
FOO/BAR
```
all refer to the same logical resource.
Case-insensitivity must be implemented by the application rather than delegated to database collation behavior. SQLite, PostgreSQL, and SQL Server have different collation defaults, and database-provider choice should not change API semantics.
The normalized code is what is stored as the primary key.
The leading slash in a request path is routing syntax and is not part of the stored code.
### Route namespaces
The management and health routes inherently take precedence over the public catch-all redirect route:
```text
/api/...
/health
```
No separate reserved-name subsystem is required. A database record whose code overlaps one of these route namespaces may exist, but it is not reachable as a public redirect because the explicit route wins.
## Persistence
Entity Framework Core is used directly for persistence.
### Initial provider
SQLite is the only persistence provider that must be supported initially.
Configuration should allow the provider choice to be expressed explicitly, for example:
```json
{
  "Database": {
    "Provider": "Sqlite"
  },
  "ConnectionStrings": {
    "Urls": "Data Source=urls.db"
  }
}
```
The initial implementation should include SQLite migrations.
### Future database portability
The entity model and ordinary LINQ queries should remain reasonably portable to:
- PostgreSQL; and
- Microsoft SQL Server.
Future provider registration could use `UseNpgsql(...)` or `UseSqlServer(...)` without changing controller behavior.
This is a portability constraint, not a requirement to implement or test those providers now.
Do not add PostgreSQL or SQL Server packages, migration sets, CI jobs, or compatibility shims until there is an actual need to deploy on those databases.
SQLite schema migrations may differ from future provider migrations. If another database is adopted, provider-specific migrations can be created at that point.
### SQLite durability
The SQLite database file is authoritative persistent state. Production deployment must place it on persistent storage rather than ephemeral container storage.
### Portable release packaging
Publish one framework-dependent, runtime-neutral archive with `UseAppHost=false` and no runtime identifier. Users invoke both the server and migration command through `dotnet Indirection.dll`, with the ASP.NET Core Runtime 10 installed. Retain the dependency-provided native SQLite assets for supported operating systems and architectures; the host selects its matching assets. Do not ship a platform-specific EF migration bundle, SDK, runtime, database, or secrets. Portability is limited to platforms supported by both .NET 10 and the packaged SQLite native libraries.
## Caching
Indirection uses `HybridCache` directly.
Initially, HybridCache uses local process memory only.
The database is authoritative; cached data is disposable.
### Lazy population
Do not prime all URLs into the cache during startup.
The redirect path uses lazy population:
```text
GET /some/code
      │
      ▼
 HybridCache
      │
      ├── hit ─────────────► 302
      │
      └── miss
             │
             ▼
       EF Core lookup
             │
             ▼
       cache successful
       lookup result
             │
             ▼
            302
```
### No negative caching
Missing URLs are not negatively cached initially.
A request for a nonexistent code should query the backing store again on a later request.
Negative caching can be introduced later if abusive or high-volume random lookup traffic makes it useful.
### Invalidation
A successful management write changes the authoritative store first, then invalidates the cache entry.
For PUT:
```text
SaveChangesAsync
      │
      ▼
HybridCache.RemoveAsync
```
For DELETE:
```text
SaveChangesAsync
      │
      ▼
HybridCache.RemoveAsync
```
The next redirect then reloads from the database.
This ordering and behavior must be covered by automated tests.
### Future distributed caching
If multiple application instances eventually require shared cache state, a distributed secondary cache such as Redis may be added behind HybridCache.
No Redis dependency is needed initially.
## HTTP API
### Public redirect
```http
GET /{**code}
```
A catch-all route is required because codes may contain slashes.
The route is anonymous.
The code is normalized before lookup.
When the code exists, return:
```http
302 Found
Location: <destination>
```
Indirection always uses `302 Found` rather than a permanent redirect so that a mapping can be changed without browsers or intermediaries retaining an old permanent destination.
When the code does not exist, return:
```http
404 Not Found
```
The service does not validate or constrain destination URLs beyond what is necessary to store and return the configured value.
## Management API
Management endpoints are rooted at:
```text
/api/v1/links
```
The supported operations are:
```text
GET     /api/v1/links
GET     /api/v1/links/{**code}
PUT     /api/v1/links/{**code}
DELETE  /api/v1/links/{**code}
```
There is intentionally no POST endpoint.
### PUT
PUT provides intentional create-or-replace semantics.
The client chooses the short code.
Example:
```http
PUT /api/v1/links/docs/getting-started
X-Api-Key: <secret>
Content-Type: application/json
{
  "destination": "https://example.com/a/very/long/url"
}
```
If the code does not exist, the service creates it.
If it already exists, the service updates/replaces it.
This is not considered a collision. PUT is intentionally idempotent and is the only create/update operation.
A newly created resource should return `201 Created`. The implementation may choose a consistent normal success response for an update (`200 OK` or `204 No Content`) and document that choice.
After persistence succeeds, the corresponding HybridCache entry is invalidated.
### GET one
```http
GET /api/v1/links/{**code}
```
Returns the stored mapping.
Lookup is case-insensitive through application normalization.
### GET collection
```http
GET /api/v1/links
```
The initial implementation may return all mappings.
Do not add pagination, search, filtering, or cursor behavior until the size or usage of the collection requires it.
### DELETE
```http
DELETE /api/v1/links/{**code}
```
Deletes the mapping if present and invalidates its HybridCache entry after the database operation succeeds.
The implementation should choose a simple, consistent response for a nonexistent resource and document it. DELETE should retain normal idempotent HTTP semantics.
## Authentication and authorization
The management API uses an API key implemented through the standard ASP.NET Core authentication and authorization system.
Do not implement authentication as ad-hoc request middleware.
### Authentication scheme
Create a custom authentication scheme based on:
```csharp
AuthenticationHandler<ApiKeyAuthenticationOptions>
```
and register it using the standard APIs:
```csharp
services
    .AddAuthentication(ApiKeyDefaults.AuthenticationScheme)
    .AddScheme<ApiKeyAuthenticationOptions,
               ApiKeyAuthenticationHandler>(...);
services.AddAuthorization();
```
Management controllers use `[Authorize]`.
The public redirect endpoint remains anonymous.
### API-key transport
Clients send the key in:
```http
X-Api-Key: <secret>
```
The expected key comes from configuration/secrets, for example:
```text
Authentication__ApiKey=<secret>
```
A real production key must never be committed to source control.
Production keys should contain at least 256 bits of cryptographically secure random entropy. One suitable generation approach is:
```csharp
Convert.ToBase64String(
    RandomNumberGenerator.GetBytes(32));
```
The authentication handler should compare the supplied and configured keys using a fixed-time comparison.
An API key is a bearer credential, so deployed management endpoints must be served over HTTPS.
### Authentication features intentionally omitted
Do not add:
- JWTs;
- token issuance;
- login endpoints;
- refresh tokens;
- users;
- roles;
- ASP.NET Identity;
- an OAuth authorization server; or
- Entra ID integration.
The custom scheme still uses the normal ASP.NET Core `ClaimsPrincipal`, `[Authorize]`, authentication middleware, and authorization middleware, so the authentication mechanism can be replaced later without changing controller authorization semantics.
## Health endpoint
Expose:
```text
GET /health
```
using ASP.NET Core health-check infrastructure.
Keep health checking minimal initially.
The explicit health route takes precedence over the public redirect catch-all.
The endpoint is intended for:
- deployment verification;
- hosting/orchestration health checks; and
- monitoring.
## Testing strategy
Testing has two separate purposes:
1. local/CI correctness and code coverage; and
2. black-box verification of an actually deployed service.
These are implemented as separate test projects.
## Local and CI tests
`Indirection.Tests` contains the normal automated test suite.
Production code must maintain:
- greater than 95% line coverage; and
- greater than 95% branch coverage.
The practical target should be at least 96% for each so the project is not sitting directly on the threshold.
Generated EF migrations and genuinely generated code may be excluded from coverage. Ordinary handwritten production code must not be excluded merely to satisfy the metric.
Deployment tests do not count toward this coverage threshold.
### Test philosophy
Prefer observable behavior and lightweight integration-style tests over excessive mocking.
Use real SQLite behavior for persistence tests, preferably SQLite in-memory where useful for isolation.
Do not use the EF Core in-memory provider as a substitute for relational testing.
Avoid mocking `DbContext`, EF Core internals, or HybridCache when exercising the actual in-process application provides more confidence for little additional cost.
### Required local coverage
At minimum, tests should cover:
#### Authentication
- missing API key is rejected;
- incorrect API key is rejected;
- correct API key authenticates;
- authenticated management requests succeed; and
- anonymous management requests fail.
#### PUT
- creates a new mapping;
- returns the documented create result;
- replaces an existing mapping;
- returns the documented update result;
- repeated identical PUT is idempotent;
- stored codes are normalized;
- case-insensitive codes address the same resource; and
- slash-containing and nested slash-containing codes work.
#### Management GET
- retrieves an existing mapping;
- handles a missing mapping; and
- lists mappings.
#### DELETE
- deletes an existing mapping;
- follows the documented nonexistent-resource behavior;
- works case-insensitively; and
- supports slash-containing codes.
#### Redirect
- an existing code returns `302`;
- the `Location` header is the stored destination;
- a missing code returns `404`;
- lookup is case-insensitive; and
- slash-containing codes resolve correctly.
#### Cache behavior
- an initial successful redirect can populate HybridCache;
- a subsequent lookup can use the cached value;
- PUT invalidates an existing cached mapping;
- a redirect after PUT returns the updated destination;
- DELETE invalidates an existing cached mapping;
- a redirect after DELETE returns `404`; and
- missing links are not negatively cached.
#### Routing
Verify that explicit system routes are not consumed by the public redirect catch-all:
- `/api/...` reaches the management API; and
- `/health` reaches the health endpoint.
#### Persistence
Cover relevant EF Core entity mapping and CRUD behavior, including the primary-key and normalized-code behavior.
## Deployment verification tests
`Indirection.DeploymentTests` is a black-box HTTP test project.
It must not reference the production `Indirection.csproj`.
It must know nothing about:
- EF Core;
- SQLite;
- HybridCache;
- internal controller classes; or
- implementation-specific services.
It communicates exclusively through the public HTTP contract using `HttpClient`.
### Configuration
The deployment suite reads its target and credentials from environment/configuration:
```text
INDIRECTION_BASE_URL
INDIRECTION_API_KEY
```
Live credentials must not be committed.
When testing redirect responses, the HTTP client must disable automatic redirect following so the suite can inspect the `302` and `Location` header directly.
### Health verification
The deployment suite verifies that:
```http
GET /health
```
returns the expected healthy result.
### Full lifecycle verification
Each run generates a unique nested synthetic code, for example:
```text
synthetic/{guid}
```
The lifecycle test performs:
1. PUT the unique code with destination A.
2. Verify creation succeeds.
3. GET the management resource using different casing.
4. Verify case-insensitive lookup.
5. GET the public short URL.
6. Verify `302`.
7. Verify `Location` is destination A.
8. PUT the same code with destination B.
9. GET the public short URL again.
10. Verify `302`.
11. Verify `Location` is destination B.
12. DELETE the mapping.
13. GET the public short URL again.
14. Verify `404`.
The second redirect after PUT is especially important: it proves cache invalidation works in the deployed service, not merely that the database row changed.
The test should use `try/finally` or equivalent cleanup so a failed run makes a best effort to remove its synthetic mapping.
Unique generated codes make parallel/concurrent runs safe.
## Deployment tests as synthetics
The same `Indirection.DeploymentTests` suite should be reusable without code changes for:
- immediate post-deployment verification;
- manual live-environment verification; and
- scheduled synthetic monitoring.
Do not create a second synthetic-monitor implementation unless a hosting platform later requires one.
## Build and quality configuration
Repository-wide compiler and build settings belong in `Directory.Build.props` where appropriate.
Enable:
- .NET 10 conventions;
- nullable reference types;
- implicit usings; and
- warnings as errors where practical.
Do not broadly suppress warnings to achieve a clean build.
CI should:
1. restore;
2. build;
3. run `Indirection.Tests`;
4. collect line and branch coverage;
5. fail if either metric is not greater than 95%; and
6. publish coverage output as an artifact when useful.
Deployment tests should not automatically run against production during ordinary pull-request builds unless an explicit target and credentials are supplied.
## Documentation
The root `README.md` should remain concise and explain:
- what Indirection is;
- prerequisites;
- how to build;
- how to run locally;
- SQLite configuration;
- API-key configuration;
- basic API examples;
- how to run local tests and coverage;
- how to run deployment tests; and
- that detailed design decisions live in `docs/design.md`.
This document should be updated when architecture or externally visible behavior changes.
## Evolution strategy
The initial implementation optimizes for a very small service with minimal operational dependencies.
Future changes should be introduced only in response to actual requirements.
Expected evolutionary paths include:
```text
SQLite
  ↓
PostgreSQL or SQL Server
```
```text
HybridCache local memory
  ↓
HybridCache local memory + distributed cache
```
```text
single application instance
  ↓
multiple instances
```
```text
API-key authentication
  ↓
external authentication provider
```
The current code should avoid gratuitous coupling that makes these changes difficult, but it should not implement their infrastructure in advance.
## Summary of architectural decisions
1. Target .NET 10.
2. Use ASP.NET Core controllers.
3. Keep production in one `.csproj`.
4. Put production code under `/src/prod`.
5. Put all tests under `/src/test`.
6. Put design documentation under `/docs`.
7. Register dependencies in static `Startup.ConfigureServices`.
8. Keep `Program.cs` small.
9. Use EF Core directly from controllers.
10. Use SQLite as the initial and only supported database provider.
11. Keep the EF model and normal queries reasonably portable to PostgreSQL and SQL Server.
12. Use HybridCache directly from controllers.
13. Populate cache lazily.
14. Do not negatively cache missing codes.
15. Client generates all short codes.
16. Use PUT for both create and update.
17. Do not expose POST.
18. Treat codes as case-insensitive.
19. Allow slashes in codes.
20. Use normalized code as the database primary key.
21. Use `302 Found` for redirects.
22. Do not validate destination URLs.
23. Protect the management API with a standard ASP.NET Core custom API-key authentication scheme.
24. Do not build JWT, user, role, or login infrastructure.
25. Expose a minimal `/health` endpoint.
26. Maintain greater than 95% line and branch coverage.
27. Maintain a separate black-box live endpoint test suite.
28. Reuse the deployment suite for deployment verification and synthetics.
29. Prefer removing unnecessary architecture over anticipating hypothetical future complexity.

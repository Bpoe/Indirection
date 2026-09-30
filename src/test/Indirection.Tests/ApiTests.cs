using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Indirection.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Indirection.Tests;

[TestClass]
public sealed class ApiTests
{
    private AppFactory app = null!;
    private HttpClient client = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        app = new AppFactory();
        client = app.CreateApiClient();
        await app.WithDb(db => db.Database.MigrateAsync());
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        client.Dispose();
        await app.DisposeAsync();
    }

    [TestMethod]
    [DataRow("GET", "/api/v1/links")]
    [DataRow("GET", "/api/v1/links/private")]
    [DataRow("PUT", "/api/v1/links/private")]
    [DataRow("DELETE", "/api/v1/links/private")]
    public async Task ManagementRequiresAuthentication(string method, string path)
    {
        using var anonymous = app.CreateApiClient(authenticated: false);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await anonymous.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("wrong")]
    public async Task IncorrectKeysAreRejected(string key)
    {
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", key);
        using var response = await client.GetAsync("/api/v1/links");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task MultipleKeysAreRejected()
    {
        client.DefaultRequestHeaders.Add("X-Api-Key", "second-key");
        using var response = await client.GetAsync("/api/v1/links");
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    [DataRow("MiXeD")]
    [DataRow("Docs/Getting-Started")]
    [DataRow("Docs/Guides/Part/One")]
    public async Task CompleteLifecycleIsCaseInsensitiveAndIdempotent(string code)
    {
        var path = "/api/v1/links/" + code;
        var lower = code.ToLowerInvariant();
        using var created = await client.PutAsJsonAsync(path, new { destination = "https://example.com/a" });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var original = await created.Content.ReadFromJsonAsync<ShortUrl>();
        Assert.IsNotNull(original);
        Assert.AreEqual(lower, original.Code);
        Assert.AreEqual("https://example.com/a", original.Destination);
        Assert.AreEqual(DateTimeKind.Utc, original.CreatedUtc.Kind);
        Assert.IsTrue(DateTime.UtcNow - original.CreatedUtc < TimeSpan.FromMinutes(1));
        Assert.IsNotNull(created.Headers.Location);
        var fromLocation = await client.GetFromJsonAsync<ShortUrl>(created.Headers.Location);
        Assert.IsNotNull(fromLocation);
        Assert.AreEqual(lower, fromLocation.Code);

        var retrieved = await client.GetFromJsonAsync<ShortUrl>("/api/v1/links/" + code.ToUpperInvariant());
        Assert.IsNotNull(retrieved);
        Assert.AreEqual(original.CreatedUtc, retrieved.CreatedUtc);
        Assert.AreEqual(DateTimeKind.Utc, retrieved.CreatedUtc.Kind);
        await AssertRedirect(code.ToUpperInvariant(), "https://example.com/a");

        for (var i = 0; i < 2; i++)
        {
            using var replaced = await client.PutAsJsonAsync("/api/v1/links/" + lower,
                new { destination = "https://example.com/b" });
            Assert.AreEqual(HttpStatusCode.NoContent, replaced.StatusCode);
            await AssertRedirect(code, "https://example.com/b");
        }

        var listed = await client.GetFromJsonAsync<List<ShortUrl>>("/api/v1/links");
        Assert.IsNotNull(listed);
        Assert.HasCount(1, listed);
        Assert.AreEqual(lower, listed[0].Code);
        Assert.AreEqual(original.CreatedUtc, listed[0].CreatedUtc);
        Assert.AreEqual("https://example.com/b", listed[0].Destination);
        await app.WithDb(async db =>
        {
            var stored = await db.ShortUrls.SingleAsync();
            Assert.AreEqual(lower, stored.Code);
            Assert.AreEqual("https://example.com/b", stored.Destination);
        });

        using var deleted = await client.DeleteAsync("/api/v1/links/" + code.ToUpperInvariant());
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missingRedirect = await client.GetAsync("/" + lower);
        Assert.AreEqual(HttpStatusCode.NotFound, missingRedirect.StatusCode);
        using var missingGet = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.NotFound, missingGet.StatusCode);
        using var missingDelete = await client.DeleteAsync(path);
        Assert.AreEqual(HttpStatusCode.NotFound, missingDelete.StatusCode);
        await app.WithDb(async db => Assert.AreEqual(0, await db.ShortUrls.CountAsync()));
    }

    [TestMethod]
    public async Task EmptyCollectionAndUnknownCodes()
    {
        var links = await client.GetFromJsonAsync<List<ShortUrl>>("/api/v1/links");
        Assert.IsNotNull(links);
        Assert.IsEmpty(links);
        foreach (var path in new[] { "/", "/missing", "/api/v1/links/missing" })
        {
            using var response = await client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [TestMethod]
    public async Task NoPostEndpoint()
    {
        foreach (var path in new[] { "/api/v1/links", "/api/v1/links/code" })
        {
            using var response = await client.PostAsJsonAsync(path, new { destination = "https://example.com" });
            Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"destination\":null}")]
    [DataRow("{\"destination\":\"\"}")]
    [DataRow("{\"destination\":\"  \"}")]
    [DataRow("{broken")]
    public async Task InvalidRequestBodiesReturnBadRequest(string json)
    {
        using var body = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PutAsync("/api/v1/links/code", body);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        await app.WithDb(async db => Assert.AreEqual(0, await db.ShortUrls.CountAsync()));
    }

    [TestMethod]
    [DataRow("/relative/path?x=1#part")]
    [DataRow("mailto:someone@example.com")]
    [DataRow("not-a-url")]
    public async Task DestinationIsNotUrlValidated(string destination)
    {
        using var response = await client.PutAsJsonAsync("/api/v1/links/code", new { destination });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        await AssertRedirect("code", destination);
    }

    [TestMethod]
    public async Task ExplicitRoutesTakePrecedenceAndPublicEndpointsAreAnonymous()
    {
        foreach (var code in new[] { "health", "api/v1/links", "api/v1/links/health" })
        {
            using var response = await client.PutAsJsonAsync("/api/v1/links/" + code,
                new { destination = "https://example.com/shadow" });
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        }

        using var anonymous = app.CreateApiClient(authenticated: false);
        using var health = await anonymous.GetAsync("/health");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
        Assert.AreEqual("Healthy", await health.Content.ReadAsStringAsync());
        using var management = await anonymous.GetAsync("/api/v1/links");
        Assert.AreEqual(HttpStatusCode.Unauthorized, management.StatusCode);
        var stored = await client.GetFromJsonAsync<ShortUrl>("/api/v1/links/health");
        Assert.IsNotNull(stored);
        Assert.AreEqual("health", stored.Code);

        anonymous.DefaultRequestHeaders.Add("X-Api-Key", "invalid");
        using var stillHealthy = await anonymous.GetAsync("/health");
        Assert.AreEqual(HttpStatusCode.OK, stillHealthy.StatusCode);
    }

    [TestMethod]
    public async Task CachePopulatesLazilyAndHitsAvoidDatabase()
    {
        using var response = await client.PutAsJsonAsync("/api/v1/links/Cached/Code",
            new { destination = "https://example.com/original" });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        // An out-of-band database change reveals whether PUT prematurely filled the cache.
        await app.WithDb(db => db.ShortUrls.ExecuteUpdateAsync(
            setters => setters.SetProperty(url => url.Destination, "https://example.com/lazy")));
        await AssertRedirect("CACHED/CODE", "https://example.com/lazy");
        // Removing the row without invalidation proves a subsequent redirect is a cache hit.
        await app.WithDb(db => db.ShortUrls.ExecuteDeleteAsync());
        await AssertRedirect("cached/code", "https://example.com/lazy");
    }

    [TestMethod]
    public async Task MissesAreNotCached()
    {
        for (var i = 0; i < 2; i++)
        {
            using var missing = await client.GetAsync("/Missing/Nested");
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        }

        // Insert directly: management PUT would invalidate even an incorrectly cached miss.
        await app.WithDb(async db =>
        {
            db.ShortUrls.Add(new ShortUrl
            {
                Code = "missing/nested", Destination = "https://example.com/found", CreatedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        });
        await AssertRedirect("MISSING/NESTED", "https://example.com/found");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WritesCommitBeforeCacheInvalidation(bool delete)
    {
        using var created = await client.PutAsJsonAsync("/api/v1/links/order",
            new { destination = "https://example.com/old" });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        await AssertRedirect("order", "https://example.com/old");
        var observed = false;
        app.Observer.AfterSave = async () =>
        {
            observed = true;
            await app.WithDb(async db =>
            {
                var stored = await db.ShortUrls.SingleOrDefaultAsync();
                if (delete)
                {
                    Assert.IsNull(stored);
                }
                else
                {
                    Assert.IsNotNull(stored);
                    Assert.AreEqual("https://example.com/new", stored.Destination);
                }
            });
            var cache = app.Services.GetRequiredService<HybridCache>();
            var cached = await cache.GetOrCreateAsync("order", _ => ValueTask.FromResult("unexpected cache miss"));
            Assert.AreEqual("https://example.com/old", cached, "Cache must still exist immediately after commit.");
        };

        using var response = delete
            ? await client.DeleteAsync("/api/v1/links/ORDER")
            : await client.PutAsJsonAsync("/api/v1/links/ORDER", new { destination = "https://example.com/new" });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.IsTrue(observed);
        if (delete)
        {
            using var missing = await client.GetAsync("/order");
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        }
        else
        {
            await AssertRedirect("order", "https://example.com/new");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FailedWritesLeaveStoredMappingAndCacheIntact(bool delete)
    {
        using var created = await client.PutAsJsonAsync("/api/v1/links/failure",
            new { destination = "https://example.com/old" });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        await AssertRedirect("failure", "https://example.com/old");
        await app.WithDb(async db =>
        {
            await db.Database.ExecuteSqlRawAsync(delete
                ? "CREATE TRIGGER reject_write BEFORE DELETE ON ShortUrls BEGIN SELECT RAISE(ABORT, 'test failure'); END;"
                : "CREATE TRIGGER reject_write BEFORE UPDATE ON ShortUrls BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        });
        using var response = delete
            ? await client.DeleteAsync("/api/v1/links/failure")
            : await client.PutAsJsonAsync("/api/v1/links/failure", new { destination = "https://example.com/new" });
        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        await app.WithDb(async db => Assert.AreEqual("https://example.com/old", (await db.ShortUrls.SingleAsync()).Destination));
        var cached = await app.Services.GetRequiredService<HybridCache>()
            .GetOrCreateAsync("failure", _ => ValueTask.FromResult("unexpected cache miss"));
        Assert.AreEqual("https://example.com/old", cached);
    }

    [TestMethod]
    public async Task NormalizationIsInvariantUnderTurkishCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            using var created = await client.PutAsJsonAsync("/api/v1/links/INDIGO/ITEM",
                new { destination = "https://example.com/invariant" });
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
            var mapping = await client.GetFromJsonAsync<ShortUrl>("/api/v1/links/indigo/item");
            Assert.IsNotNull(mapping);
            Assert.AreEqual("indigo/item", mapping.Code);
            await AssertRedirect("INDIGO/ITEM", "https://example.com/invariant");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [TestMethod]
    public async Task MigrationCreatesPrimaryKeyAndSurvivesReopeningConnections()
    {
        await app.WithDb(async db =>
        {
            Assert.IsFalse(db.Database.HasPendingModelChanges());
            Assert.HasCount(1, (await db.Database.GetAppliedMigrationsAsync()).ToList());
            Assert.IsEmpty((await db.Database.GetPendingMigrationsAsync()).ToList());
            var entity = db.Model.FindEntityType(typeof(ShortUrl));
            Assert.IsNotNull(entity);
            var key = entity.FindPrimaryKey();
            Assert.IsNotNull(key);
            Assert.HasCount(1, key.Properties);
            Assert.AreEqual("Code", key.Properties[0].Name);
            db.ShortUrls.Add(new ShortUrl { Code = "durable", Destination = "target", CreatedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
        await app.WithDb(async db =>
        {
            Assert.AreEqual("target", (await db.ShortUrls.SingleAsync()).Destination);
            // Clear the read entity so this exercises SQLite's PK constraint, not EF's identity map.
            db.ChangeTracker.Clear();
            db.ShortUrls.Add(new ShortUrl { Code = "durable", Destination = "duplicate", CreatedUtc = DateTime.UtcNow });
            await Assert.ThrowsExactlyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
    }

    private async Task AssertRedirect(string code, string destination)
    {
        using var anonymous = app.CreateApiClient(authenticated: false);
        using var response = await anonymous.GetAsync("/" + code);
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.IsNotNull(response.Headers.Location);
        Assert.AreEqual(destination, response.Headers.Location.OriginalString);
    }
}

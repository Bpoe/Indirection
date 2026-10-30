using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;

namespace Indirection.Tests;

[TestClass]
public sealed class AdminUiTests
{
    [TestMethod]
    public async Task AdminUiIsServedWithoutExposingTheManagementApi()
    {
        await using var app = new AppFactory();
        using var client = app.CreateApiClient(authenticated: false);

        using var redirect = await client.GetAsync("/admin");
        Assert.AreEqual(HttpStatusCode.MovedPermanently, redirect.StatusCode);
        Assert.AreEqual("/admin/", redirect.Headers.Location?.AbsolutePath);

        using var index = await client.GetAsync(redirect.Headers.Location);
        Assert.AreEqual(HttpStatusCode.OK, index.StatusCode);
        Assert.AreEqual("text/html", index.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(await index.Content.ReadAsStringAsync(), "Indirection · Links");

        foreach (var path in new[] { "/admin/styles.css", "/admin/app.js" })
        {
            using var asset = await client.GetAsync(path);
            Assert.AreEqual(HttpStatusCode.OK, asset.StatusCode, path);
        }

        using var management = await client.GetAsync("/api/v1/links");
        Assert.AreEqual(HttpStatusCode.Unauthorized, management.StatusCode);
    }

    [TestMethod]
    public async Task AdminPathsAreReservedEvenWhenRedirectsExist()
    {
        await using var app = new AppFactory();
        using var client = app.CreateApiClient();
        await app.WithDb(db => db.Database.MigrateAsync());

        foreach (var code in new[] { "admin", "admin/index.html", "admin/app.js", "admin/missing" })
        {
            using var created = await client.PutAsJsonAsync("/api/v1/links/" + code,
                new { destination = "https://example.com/reserved" });
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        }

        using var anonymous = app.CreateApiClient(authenticated: false);
        using var index = await anonymous.GetAsync("/admin/index.html");
        Assert.AreEqual(HttpStatusCode.OK, index.StatusCode);
        using var script = await anonymous.GetAsync("/admin/app.js");
        Assert.AreEqual(HttpStatusCode.OK, script.StatusCode);
        using var missing = await anonymous.GetAsync("/admin/missing");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        using var root = await anonymous.GetAsync("/admin");
        Assert.AreEqual(HttpStatusCode.MovedPermanently, root.StatusCode);
    }
}

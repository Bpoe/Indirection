using System.Net;

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
        Assert.AreEqual(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.AreEqual("/admin/", redirect.Headers.Location?.OriginalString);

        using var index = await client.GetAsync("/admin/");
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
}

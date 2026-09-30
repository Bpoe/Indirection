using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Indirection.DeploymentTests;

[TestClass]
public sealed class DeploymentTests
{
    private HttpClient client = null!;
    private string apiKey = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        var baseUrl = Environment.GetEnvironmentVariable("INDIRECTION_BASE_URL");
        var key = Environment.GetEnvironmentVariable("INDIRECTION_API_KEY");
        if (string.IsNullOrWhiteSpace(baseUrl) && string.IsNullOrWhiteSpace(key))
        {
            Assert.Inconclusive("Set INDIRECTION_BASE_URL and INDIRECTION_API_KEY to run deployment verification.");
        }

        Assert.IsFalse(string.IsNullOrWhiteSpace(baseUrl), "INDIRECTION_BASE_URL is required.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(key), "INDIRECTION_API_KEY is required.");
        Assert.IsTrue(Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
            "INDIRECTION_BASE_URL must be an absolute HTTP(S) URL.");
        apiKey = key;
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = uri,
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    [TestCleanup]
    public void Cleanup() => client?.Dispose();

    [TestMethod]
    public async Task HealthIsHealthy()
    {
        using var response = await client.GetAsync("health");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task FullLifecycle()
    {
        var code = $"synthetic/{Guid.NewGuid():N}/nested";
        var path = "api/v1/links/" + code;
        const string destinationA = "https://example.com/indirection-synthetic/a";
        const string destinationB = "https://example.com/indirection-synthetic/b";
        var completed = false;
        try
        {
            using var created = await Management(HttpMethod.Put, path, destinationA);
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
            using var get = await Management(HttpMethod.Get, "api/v1/links/" + code.ToUpperInvariant());
            Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
            using var mapping = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
            Assert.AreEqual(code, mapping.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(destinationA, mapping.RootElement.GetProperty("destination").GetString());
            await AssertRedirect(code.ToUpperInvariant(), destinationA);
            await AssertRedirect(code, destinationA);

            using var updated = await Management(HttpMethod.Put, "api/v1/links/" + code.ToUpperInvariant(), destinationB);
            Assert.AreEqual(HttpStatusCode.NoContent, updated.StatusCode);
            await AssertRedirect(code, destinationB);

            using var deleted = await Management(HttpMethod.Delete, "api/v1/links/" + code.ToUpperInvariant());
            Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
            using var missing = await client.GetAsync(code);
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            using var missingManagement = await Management(HttpMethod.Get, path);
            Assert.AreEqual(HttpStatusCode.NotFound, missingManagement.StatusCode);
            completed = true;
        }
        finally
        {
            try
            {
                using var cleanup = await Management(HttpMethod.Delete, path);
                Assert.IsTrue(cleanup.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound,
                    $"Synthetic cleanup failed for {code}: {cleanup.StatusCode}");
            }
            catch (Exception exception) when (!completed)
            {
                // Preserve the original failure while reporting that best-effort cleanup also failed.
                TestContext.WriteLine($"Cleanup failed for {code}: {exception.Message}");
            }
        }
    }

    private async Task<HttpResponseMessage> Management(HttpMethod method, string path, string? destination = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Api-Key", apiKey);
        if (destination is not null)
        {
            request.Content = JsonContent.Create(new { destination });
        }

        return await client.SendAsync(request);
    }

    private async Task AssertRedirect(string code, string destination)
    {
        using var response = await client.GetAsync(code);
        Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
        Assert.IsNotNull(response.Headers.Location);
        Assert.AreEqual(destination, response.Headers.Location.OriginalString);
    }
}

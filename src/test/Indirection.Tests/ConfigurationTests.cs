using Indirection.Authentication;
using Indirection.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Indirection.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("PostgreSQL")]
    public void UnsupportedOrMissingProviderFails(string? provider)
    {
        var configuration = Configuration(provider, "Data Source=:memory:", "test-only");
        Assert.ThrowsExactly<InvalidOperationException>(() => Startup.ConfigureServices(new ServiceCollection(), configuration));
    }

    [TestMethod]
    public void MissingConnectionStringFails()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => Startup.ConfigureServices(
            new ServiceCollection(), Configuration("Sqlite", null, "test-only")));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public void MissingOrBlankKeyFailsValidation(string? key)
    {
        var services = new ServiceCollection();
        Startup.ConfigureServices(services, Configuration("Sqlite", "Data Source=:memory:", key));
        using var provider = services.BuildServiceProvider();
        Assert.ThrowsExactly<OptionsValidationException>(() => provider
            .GetRequiredService<IOptionsMonitor<ApiKeyAuthenticationOptions>>().Get(ApiKeyDefaults.AuthenticationScheme));
    }

    [TestMethod]
    public void SqliteProviderNameIsCaseInsensitive()
    {
        var services = new ServiceCollection();
        Startup.ConfigureServices(services, Configuration("sQLiTe", "Data Source=:memory:", "test-only"));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsTrue(scope.ServiceProvider.GetRequiredService<IndirectionDbContext>().Database.IsSqlite());
    }

    private static IConfiguration Configuration(string? databaseProvider, string? connectionString, string? key)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = databaseProvider,
            ["ConnectionStrings:Urls"] = connectionString,
            ["Authentication:ApiKey"] = key
        }).Build();
}

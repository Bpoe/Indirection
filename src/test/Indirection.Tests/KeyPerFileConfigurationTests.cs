using Indirection.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Indirection.Tests;

[TestClass]
[DoNotParallelize]
public sealed class KeyPerFileConfigurationTests
{
    private DirectoryInfo directory = null!;
    private string? originalApiKey;

    [TestInitialize]
    public void Initialize()
    {
        directory = Directory.CreateTempSubdirectory("indirection-configuration-");
        originalApiKey = Environment.GetEnvironmentVariable("Authentication__ApiKey");
        Environment.SetEnvironmentVariable("Authentication__ApiKey", null);
        File.WriteAllText(Path.Combine(directory.FullName, "appsettings.json"), """
            {
              "Database": { "Provider": "Sqlite" },
              "ConnectionStrings": { "Urls": "Data Source=:memory:" },
              "Authentication": { "ApiKey": "json-test-key" },
              "ConfigurationTest": { "BaseOnly": "base-json", "Layered": "base-json" }
            }
            """);
        File.WriteAllText(Path.Combine(directory.FullName, "appsettings.Testing.json"), """
            { "ConfigurationTest": { "Layered": "environment-json" } }
            """);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("Authentication__ApiKey", originalApiKey);
        directory.Delete(recursive: true);
    }

    [TestMethod]
    public void FilesMapHierarchicalKeysAndBindAuthenticationOptions()
    {
        var secrets = Directory.CreateDirectory(Path.Combine(directory.FullName, "secrets"));
        File.WriteAllText(Path.Combine(secrets.FullName, "Authentication__ApiKey"), "file-test-key");
        File.WriteAllText(Path.Combine(secrets.FullName, "ConfigurationTest__Nested__Value"), "arbitrary-value");
        using var configuration = CreateConfiguration(secrets.FullName);

        Assert.AreEqual("file-test-key", configuration["Authentication:ApiKey"]);
        Assert.AreEqual("arbitrary-value", configuration["ConfigurationTest:Nested:Value"]);
        using var services = CreateServices(configuration);
        Assert.AreEqual("file-test-key", services.GetRequiredService<IOptionsMonitor<ApiKeyAuthenticationOptions>>()
            .Get(ApiKeyDefaults.AuthenticationScheme).ApiKey);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FilesOverrideJsonEnvironmentAndCommandLine(bool commandLine)
    {
        Environment.SetEnvironmentVariable("Authentication__ApiKey", "environment-test-key");
        var secrets = Directory.CreateDirectory(Path.Combine(directory.FullName, "secrets"));
        File.WriteAllText(Path.Combine(secrets.FullName, "Authentication__ApiKey"), "file-test-key");
        using var configuration = CreateConfiguration(secrets.FullName,
            commandLine ? ["--Authentication:ApiKey=command-line-test-key"] : []);

        Assert.AreEqual("file-test-key", configuration["Authentication:ApiKey"]);
    }

    [TestMethod]
    [DataRow(false, false, "json-test-key")]
    [DataRow(true, false, "environment-test-key")]
    [DataRow(true, true, "command-line-test-key")]
    public void MissingDirectoryPreservesStandardConfiguration(bool environment, bool commandLine, string expected)
    {
        if (environment)
        {
            Environment.SetEnvironmentVariable("Authentication__ApiKey", "environment-test-key");
        }

        var missing = Path.Combine(directory.FullName, "missing");
        using var configuration = CreateConfiguration(missing,
            commandLine ? ["--Authentication:ApiKey=command-line-test-key"] : []);

        Assert.IsFalse(Directory.Exists(missing));
        Assert.AreEqual(expected, configuration["Authentication:ApiKey"]);
        Assert.AreEqual("base-json", configuration["ConfigurationTest:BaseOnly"]);
        Assert.AreEqual("environment-json", configuration["ConfigurationTest:Layered"]);
        using var services = CreateServices(configuration);
        Assert.AreEqual(expected, services.GetRequiredService<IOptionsMonitor<ApiKeyAuthenticationOptions>>()
            .Get(ApiKeyDefaults.AuthenticationScheme).ApiKey);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public void BlankSecretStillFailsApiKeyValidation(string key)
    {
        var secrets = Directory.CreateDirectory(Path.Combine(directory.FullName, "secrets"));
        File.WriteAllText(Path.Combine(secrets.FullName, "Authentication__ApiKey"), key);
        using var configuration = CreateConfiguration(secrets.FullName);
        using var services = CreateServices(configuration);

        Assert.ThrowsExactly<OptionsValidationException>(() => services
            .GetRequiredService<IOptionsMonitor<ApiKeyAuthenticationOptions>>().Get(ApiKeyDefaults.AuthenticationScheme));
    }

    private ConfigurationManager CreateConfiguration(string secretsDirectory, string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = directory.FullName,
            EnvironmentName = "Testing",
            Args = args ?? []
        });
        Startup.ConfigureConfiguration(builder.Configuration, secretsDirectory);
        return builder.Configuration;
    }

    private static ServiceProvider CreateServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        Startup.ConfigureServices(services, configuration);
        return services.BuildServiceProvider();
    }
}

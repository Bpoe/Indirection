using System.Security.Cryptography;
using Indirection.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Indirection.Tests;

internal sealed class AppFactory : WebApplicationFactory<Program>
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "indirection-tests", Guid.NewGuid().ToString("N"));

    public string ApiKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public SaveObserver Observer { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(directory);
        builder.UseEnvironment("Development");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:Urls", $"Data Source={Path.Combine(directory, "test.db")};Pooling=False");
        builder.UseSetting("Authentication:ApiKey", ApiKey);
        builder.UseSetting("Logging:LogLevel:Default", "None");
        builder.ConfigureServices(services => services.AddDbContext<IndirectionDbContext>(
            options => options.AddInterceptors(Observer)));
    }

    public HttpClient CreateApiClient(bool authenticated = true)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (authenticated)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        }

        return client;
    }

    public async Task WithDb(Func<IndirectionDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<IndirectionDbContext>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }
}

internal sealed class SaveObserver : SaveChangesInterceptor
{
    public Func<Task>? AfterSave { get; set; }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (AfterSave is not null)
        {
            await AfterSave();
        }

        return result;
    }
}

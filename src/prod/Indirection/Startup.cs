using Indirection.Authentication;
using Indirection.Data;
using Microsoft.EntityFrameworkCore;

namespace Indirection;

public static class Startup
{
    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        if (!string.Equals(configuration["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Database:Provider must be Sqlite.");
        }

        var connectionString = configuration.GetConnectionString("Urls")
            ?? throw new InvalidOperationException("ConnectionStrings:Urls is required.");
        services.AddDbContext<IndirectionDbContext>(options => options.UseSqlite(connectionString));
        services.AddHybridCache();
        services.AddControllers();
        services.AddHealthChecks();
        services.AddAuthentication(ApiKeyDefaults.AuthenticationScheme)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                ApiKeyDefaults.AuthenticationScheme,
                options => configuration.GetSection("Authentication").Bind(options));
        services.AddOptions<ApiKeyAuthenticationOptions>(ApiKeyDefaults.AuthenticationScheme)
            .Validate(options => !string.IsNullOrWhiteSpace(options.ApiKey), "Authentication:ApiKey is required.")
            .ValidateOnStart();
        services.AddAuthorization();
    }
}

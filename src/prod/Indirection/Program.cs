using Indirection;
using Indirection.Data;
using Microsoft.EntityFrameworkCore;

var migrate = args.Contains("--migrate", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(argument => argument != "--migrate").ToArray());
Startup.ConfigureConfiguration(builder.Configuration);
Startup.ConfigureServices(builder.Services, builder.Configuration);

await using var app = builder.Build();
if (migrate)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IndirectionDbContext>().Database.MigrateAsync();
    return;
}

// Serve the static admin SPA without changing API-key authorization.
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapGet("/admin", () => Results.Redirect("/admin/")).AllowAnonymous();
await app.RunAsync();

public partial class Program;

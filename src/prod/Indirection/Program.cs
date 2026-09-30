using Indirection;
using Indirection.Data;
using Microsoft.EntityFrameworkCore;

var migrate = args.Contains("--migrate", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(argument => argument != "--migrate").ToArray());
Startup.ConfigureServices(builder.Services, builder.Configuration);

await using var app = builder.Build();
if (migrate)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<IndirectionDbContext>().Database.MigrateAsync();
    return;
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();
await app.RunAsync();

public partial class Program;

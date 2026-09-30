using System.Diagnostics;
using Indirection.Data;
using Indirection.Models;
using Microsoft.EntityFrameworkCore;

namespace Indirection.Tests;

[TestClass]
public sealed class MigrationCommandTests
{
    [TestMethod]
    public async Task MigrateCreatesDatabaseAndRepeatedExecutionPreservesData()
    {
        var directory = Directory.CreateTempSubdirectory("indirection-migration-");
        try
        {
            var connectionString = $"Data Source={Path.Combine(directory.FullName, "urls.db")};Pooling=False";
            var first = await RunMigration(connectionString);
            Assert.AreEqual(0, first.ExitCode, first.Output);
            var options = new DbContextOptionsBuilder<IndirectionDbContext>().UseSqlite(connectionString).Options;
            await using (var db = new IndirectionDbContext(options))
            {
                Assert.HasCount(1, (await db.Database.GetAppliedMigrationsAsync()).ToList());
                Assert.IsEmpty((await db.Database.GetPendingMigrationsAsync()).ToList());
                db.ShortUrls.Add(new ShortUrl { Code = "persisted", Destination = "target", CreatedUtc = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }

            var second = await RunMigration(connectionString);
            Assert.AreEqual(0, second.ExitCode, second.Output);
            await using var reopened = new IndirectionDbContext(options);
            Assert.AreEqual("target", (await reopened.ShortUrls.SingleAsync()).Destination);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task MigrateFailureReturnsNonzeroExitCode()
    {
        var missingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var result = await RunMigration($"Data Source={Path.Combine(missingDirectory, "urls.db")}");
        Assert.AreNotEqual(0, result.ExitCode, result.Output);
        Assert.IsFalse(Directory.Exists(missingDirectory));
    }

    private static async Task<(int ExitCode, string Output)> RunMigration(string connectionString)
    {
        var assembly = typeof(Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(assembly);
        start.ArgumentList.Add("--migrate");
        start.ArgumentList.Add("--Database:Provider=Sqlite");
        start.ArgumentList.Add($"--ConnectionStrings:Urls={connectionString}");
        start.ArgumentList.Add("--Logging:LogLevel:Default=None");
        // Migration mode must exit without starting HTTP or requiring management credentials.
        start.ArgumentList.Add("--Authentication:ApiKey=");
        using var process = Process.Start(start);
        Assert.IsNotNull(process);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await output + await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}

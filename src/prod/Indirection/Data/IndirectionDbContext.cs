using Indirection.Models;
using Microsoft.EntityFrameworkCore;

namespace Indirection.Data;

public sealed class IndirectionDbContext(DbContextOptions<IndirectionDbContext> options) : DbContext(options)
{
    public DbSet<ShortUrl> ShortUrls => Set<ShortUrl>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShortUrl>().HasKey(url => url.Code);
        modelBuilder.Entity<ShortUrl>().Property(url => url.CreatedUtc)
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }
}

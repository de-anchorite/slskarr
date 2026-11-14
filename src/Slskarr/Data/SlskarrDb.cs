using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Slskarr.Data;

public sealed class SlskarrDb(DbContextOptions<SlskarrDb> options) : DbContext(options)
{
    public DbSet<HitEntity> Hits => Set<HitEntity>();

    public DbSet<SearchJobEntity> SearchJobs => Set<SearchJobEntity>();

    public DbSet<DownloadJobEntity> DownloadJobs => Set<DownloadJobEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Ticks keep SQLite ordering and comparison correct (its text form is not offset-aware).
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<NullableUtcTicksConverter>();
        configurationBuilder.Properties<SearchJobState>().HaveConversion<string>();
        configurationBuilder.Properties<DownloadState>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HitEntity>(e =>
        {
            e.HasKey(h => h.Id);
            e.HasIndex(h => new { h.SearchKey, h.Username, h.RemotePath }).IsUnique();
            e.HasIndex(h => h.SeenAt);
        });
        modelBuilder.Entity<SearchJobEntity>(e =>
        {
            e.HasKey(s => s.Key);
            e.HasIndex(s => s.State);
        });
        modelBuilder.Entity<DownloadJobEntity>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.HitId);
            e.HasIndex(d => d.State);
        });
    }

    private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        v => v.UtcTicks,
        v => new DateTimeOffset(v, TimeSpan.Zero));

    private sealed class NullableUtcTicksConverter() : ValueConverter<DateTimeOffset?, long?>(
        v => v == null ? null : v.Value.UtcTicks,
        v => v == null ? null : new DateTimeOffset(v.Value, TimeSpan.Zero));
}

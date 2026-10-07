using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using YtDownloader.Domain;

namespace YtDownloader.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DownloadJob> DownloadJobs => Set<DownloadJob>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfiguration(new DownloadJobConfiguration());
}

public sealed class DownloadJobConfiguration : IEntityTypeConfiguration<DownloadJob>
{
    public void Configure(EntityTypeBuilder<DownloadJob> builder)
    {
        builder.ToTable("DownloadJobs");
        builder.HasKey(job => job.Id);
        builder.Property(job => job.Id).ValueGeneratedNever();
        builder.Property(job => job.Url).IsRequired().HasMaxLength(2048);
        builder.Property(job => job.VideoTitle).HasMaxLength(500);
        builder.Property(job => job.Quality).IsRequired().HasMaxLength(20);
        builder.Property(job => job.FilePath).HasMaxLength(4096);
        builder.Property(job => job.ErrorMessage).HasMaxLength(2000);
        builder.Property(job => job.Status).IsConcurrencyToken();
        // SQLite cannot compare DateTimeOffset natively; UTC ticks preserve ordering.
        var utcTicks = new ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(job => job.CreatedAt).HasConversion(utcTicks);
        builder.Property(job => job.CompletedAt).HasConversion(utcTicks);
        builder.Property(job => job.ExpiresAt).HasConversion(utcTicks);
        builder.HasIndex(job => new { job.Status, job.ExpiresAt });
        builder.HasIndex(job => job.CreatedAt);
    }
}
